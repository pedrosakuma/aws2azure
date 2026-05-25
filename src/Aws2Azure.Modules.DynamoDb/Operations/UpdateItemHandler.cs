using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// <c>UpdateItem</c> handler. DynamoDB UpdateItem is atomic on the
/// server side; Cosmos exposes optimistic concurrency via <c>_etag</c>
/// + <c>If-Match</c> instead. The implementation is therefore a
/// GET → mutate → PUT(If-Match) loop with a small bounded retry on 412
/// (Precondition Failed) to converge on a stable read/write under
/// contention.
///
/// <para>UpdateItem also has "upsert" semantics: when the target item
/// does not exist, the operation creates it with the result of running
/// the update against an empty map. That branch performs a POST
/// (`x-ms-documentdb-is-upsert: true`) instead of a PUT.</para>
///
/// <para>The handler accepts both modern <c>UpdateExpression</c> and
/// legacy <c>AttributeUpdates</c> requests; the latter is normalised
/// into the same <see cref="UpdateExpressionAst"/> via
/// <see cref="AttributeUpdatesNormaliser"/> so the executor stays
/// uniform.</para>
/// </summary>
internal static class UpdateItemHandler
{
    private const int MaxOptimisticRetries = 4;

    public static Task HandleUpdateItemAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        UpdateItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.UpdateItemRequest);
        }
        catch (JsonException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException", ex.Message);
        }

        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.");
        }

        if (HasContent(req.ConditionExpression) && (HasContent(req.Expected) || HasContent(req.ConditionalOperator)))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ConditionExpression and the legacy Expected/ConditionalOperator parameters are mutually exclusive.");
        }

        if (!IsAllowedReturnValuesOnConditionCheckFailure(
                req.ReturnValuesOnConditionCheckFailure, out var rvccfCanonical, out var rvccfErr))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvccfErr);
        }

        var hasUpdateExpr = HasContent(req.UpdateExpression);
        var hasAttrUpdates = HasContent(req.AttributeUpdates);
        if (hasUpdateExpr == hasAttrUpdates)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Exactly one of UpdateExpression or AttributeUpdates must be present.");
        }

        if (!IsAllowedReturnValues(req.ReturnValues, out var rvCanonical, out var rvErr))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvErr);
        }

        UpdateExpressionAst ast;
        ConditionNode? condition;
        try
        {
            IReadOnlyDictionary<string, string>? names = null;
            IReadOnlyDictionary<string, JsonElement>? values = null;
            bool hasConditionExpr = !string.IsNullOrWhiteSpace(req.ConditionExpression);
            if (hasUpdateExpr)
            {
                names = TryMaterialise(req.ExpressionAttributeNames, requireStringValues: true);
                values = TryMaterialiseValues(req.ExpressionAttributeValues);
                ast = UpdateExpressionParser.Parse(req.UpdateExpression!, names, values);
            }
            else
            {
                if ((HasContent(req.ExpressionAttributeNames) || HasContent(req.ExpressionAttributeValues))
                    && !hasConditionExpr)
                {
                    return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        "ExpressionAttributeNames / ExpressionAttributeValues require UpdateExpression or ConditionExpression.");
                }
                if (hasConditionExpr)
                {
                    names = TryMaterialise(req.ExpressionAttributeNames, requireStringValues: true);
                    values = TryMaterialiseValues(req.ExpressionAttributeValues);
                }
                ast = AttributeUpdatesNormaliser.Build(req.AttributeUpdates!.Value);
            }
            condition = ConditionGate.TryParse(
                req.ConditionExpression, req.Expected, req.ConditionalOperator, names, values);
        }
        catch (ExpressionSyntaxException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Invalid expression (offset {ex.Position}): {ex.Message}");
        }
        catch (UpdateValidationException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message);
        }
        catch (ConditionParseConflictException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message);
        }

        return UpdateItemCoreAsync(ctx, req, ast, condition, rvCanonical, rvccfCanonical, cosmos, ct);
    }

    private static async Task UpdateItemCoreAsync(
        HttpContext ctx, UpdateItemRequest req, UpdateExpressionAst ast, ConditionNode? condition,
        string returnValues, string returnValuesOnConditionCheckFailure,
        CosmosClient cosmos, CancellationToken ct)
    {
        using var metaResult = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, req.TableName!, ct).ConfigureAwait(false);
        if (metaResult.Status == CosmosOpsShared.TableMetadataReadStatus.CosmosError)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, metaResult.ErrorResponse!, ct).ConfigureAwait(false);
            return;
        }
        if (metaResult.Status == CosmosOpsShared.TableMetadataReadStatus.NotFound)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                $"Table not found: {req.TableName}").ConfigureAwait(false);
            return;
        }
        var meta = metaResult.Metadata!;

        if (!ValidateKey(req.Key, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuild(req.Key, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var docLink = collLink + "/docs/" + id;

        for (int attempt = 0; attempt < MaxOptimisticRetries; attempt++)
        {
            // GET current item (if any) to capture old image + etag.
            Dictionary<string, JsonElement>? existingItem = null;
            string? etag = null;
            using (var getResp = await cosmos.SendAsync(
                HttpMethod.Get, "docs", docLink, "/" + docLink,
                content: null,
                new[] { new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader) },
                ct).ConfigureAwait(false))
            {
                if (getResp.StatusCode == HttpStatusCode.NotFound)
                {
                    if (CosmosOpsShared.Is404ContainerMissing(getResp))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                            $"Table not found: {req.TableName}").ConfigureAwait(false);
                        return;
                    }
                    // existingItem stays null → upsert path.
                }
                else if (!getResp.IsSuccessStatusCode)
                {
                    await CosmosOpsShared.WriteCosmosErrorAsync(ctx, getResp, ct).ConfigureAwait(false);
                    return;
                }
                else
                {
                    etag = ExtractETag(getResp);
                    await using var s = await getResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    existingItem = ItemHandlers.ExtractItemFromCosmosDoc(s);
                    // ExtractItem returns null only when the envelope is
                    // missing; treat as "empty item" rather than failing
                    // so docs migrated by hand still work.
                    existingItem ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                }
            }

            UpdateExecutor.ExecutionResult execResult;
            try
            {
                if (condition is not null)
                {
                    bool pass;
                    try { pass = ConditionEvaluator.Evaluate(condition, existingItem); }
                    catch (ConditionEvaluationException cex)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", cex.Message).ConfigureAwait(false);
                        return;
                    }
                    if (!pass)
                    {
                        await ConditionFailureResponder.WriteAsync(
                            ctx, existingItem, returnValuesOnConditionCheckFailure).ConfigureAwait(false);
                        return;
                    }
                }
                execResult = UpdateExecutor.Apply(ast, existingItem);
            }
            catch (UpdateValidationException ex)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message).ConfigureAwait(false);
                return;
            }

            // Ensure key attributes were not removed and still match the
            // request key (the executor will happily REMOVE them).
            if (!ReinforceKeyAttributes(execResult.NewItem, req.Key, meta, out var keyAttrError))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyAttrError).ConfigureAwait(false);
                return;
            }

            // Validate the mutated item against the same shape + reserved-
            // name rules a PutItem would face — an UpdateExpression can
            // SET an attribute whose name collides with a reserved Cosmos
            // root prop (e.g. `_a2aFoo`) or whose payload kind is
            // malformed via :v parameters; we must surface those as
            // client ValidationException rather than let them throw out
            // of the encoder.
            var newItemJson = SerialiseItemMap(execResult.NewItem);
            if (!ItemHandlers.ValidateItemShape(newItemJson, out var shapeError))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", shapeError).ConfigureAwait(false);
                return;
            }

            // Build the Cosmos doc envelope from the mutated item map.
            var docJson = ItemHandlers.BuildItemDocument(id, pk, newItemJson);

            HttpResponseMessage writeResp;
            if (execResult.ItemExistedBefore && etag is not null)
            {
                // Optimistic replace: PUT with If-Match.
                using var content = new StringContent(docJson, Encoding.UTF8, "application/json");
                var headers = new[]
                {
                    new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                    new KeyValuePair<string, string>("If-Match", etag),
                };
                writeResp = await cosmos.SendAsync(
                    HttpMethod.Put, "docs", docLink, "/" + docLink,
                    content, headers, ct).ConfigureAwait(false);
            }
            else
            {
                // Item did not exist → atomic create with If-None-Match: *
                // so a concurrent UpdateItem cannot race us and lose the
                // other writer's mutation. On 409/412 the loop re-reads
                // and replays the update against the winner's state.
                using var content = new StringContent(docJson, Encoding.UTF8, "application/json");
                var headers = new[]
                {
                    new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                    new KeyValuePair<string, string>("If-None-Match", "*"),
                };
                writeResp = await cosmos.SendAsync(
                    HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
                    content, headers, ct).ConfigureAwait(false);
            }

            using (writeResp)
            {
                if (writeResp.StatusCode == HttpStatusCode.PreconditionFailed
                    || writeResp.StatusCode == HttpStatusCode.Conflict)
                {
                    // Lost a race; another writer mutated or created the
                    // doc since our GET. Re-loop so the next iteration
                    // re-reads and re-applies the update to the winner.
                    continue;
                }
                if (writeResp.StatusCode == HttpStatusCode.NotFound)
                {
                    // Container vanished mid-op.
                    if (CosmosOpsShared.Is404ContainerMissing(writeResp))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                            $"Table not found: {req.TableName}").ConfigureAwait(false);
                        return;
                    }
                    // Item disappeared between our GET and PUT. Restart.
                    continue;
                }
                if (!writeResp.IsSuccessStatusCode)
                {
                    await CosmosOpsShared.WriteCosmosErrorAsync(ctx, writeResp, ct).ConfigureAwait(false);
                    return;
                }
            }

            await WriteSuccessAsync(ctx, returnValues, execResult).ConfigureAwait(false);
            return;
        }

        // Retry budget exhausted under sustained contention.
        await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
            "UpdateItem failed to converge after repeated concurrency conflicts.").ConfigureAwait(false);
    }

    // ----- response composition -------------------------------------

    private static Task WriteSuccessAsync(
        HttpContext ctx, string returnValues, UpdateExecutor.ExecutionResult result)
    {
        Dictionary<string, JsonElement>? attrs = returnValues switch
        {
            "NONE" => null,
            "ALL_OLD" => result.ItemExistedBefore ? result.OldItem : null,
            "ALL_NEW" => result.NewItem,
            "UPDATED_OLD" => ProjectAttributes(result.OldItem, result.UpdatedAttributes),
            "UPDATED_NEW" => ProjectAttributes(result.NewItem, result.UpdatedAttributes),
            _ => null,
        };
        var response = new UpdateItemResponse { Attributes = attrs };
        return CosmosOpsShared.WriteJsonAsync(ctx, 200, response, ItemJsonContext.Default.UpdateItemResponse);
    }

    private static Dictionary<string, JsonElement>? ProjectAttributes(
        Dictionary<string, JsonElement>? source, HashSet<string> top)
    {
        if (source is null || top.Count == 0) return null;
        var projected = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in top)
        {
            if (source.TryGetValue(name, out var v)) projected[name] = v;
        }
        return projected.Count == 0 ? null : projected;
    }

    // ----- helpers --------------------------------------------------

    private static string? ExtractETag(HttpResponseMessage resp)
    {
        if (resp.Headers.ETag is EntityTagHeaderValue etag) return etag.Tag;
        if (resp.Headers.TryGetValues("etag", out var values))
        {
            foreach (var v in values) return v;
        }
        return null;
    }

    /// <summary>
    /// Serialises the in-memory item map back into a wire-form
    /// <see cref="JsonElement"/> for storage inside the Cosmos envelope.
    /// Uses each child's raw bytes so number precision is preserved.
    /// </summary>
    private static JsonElement SerialiseItemMap(Dictionary<string, JsonElement> item)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var kv in item)
            {
                w.WritePropertyName(kv.Key);
                kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static bool ReinforceKeyAttributes(
        Dictionary<string, JsonElement> item, JsonElement requestKey,
        TableMetadata meta, out string error)
    {
        foreach (var k in meta.KeySchema)
        {
            if (!requestKey.TryGetProperty(k.Name, out var keyAttr))
            {
                error = $"Internal: request Key missing '{k.Name}'.";
                return false;
            }
            // Re-stamp the key attributes from the request so they
            // survive a REMOVE / SET that would otherwise drop them.
            item[k.Name] = keyAttr.Clone();
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateKey(JsonElement key, TableMetadata meta, out string error)
    {
        if (key.ValueKind != JsonValueKind.Object)
        {
            error = "Key must be a JSON object.";
            return false;
        }
        foreach (var k in meta.KeySchema)
        {
            if (!key.TryGetProperty(k.Name, out var attr))
            {
                error = $"Key is missing required attribute '{k.Name}'.";
                return false;
            }
            if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
            {
                error = typeError;
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool IsAllowedReturnValues(string? raw, out string canonical, out string error)
    {
        canonical = string.IsNullOrEmpty(raw) ? "NONE" : raw!;
        if (canonical is "NONE" or "ALL_OLD" or "UPDATED_OLD" or "ALL_NEW" or "UPDATED_NEW")
        {
            error = string.Empty;
            return true;
        }
        error = $"ReturnValues='{raw}' is not a valid UpdateItem ReturnValues mode.";
        return false;
    }

    private static bool IsAllowedReturnValuesOnConditionCheckFailure(
        string? raw, out string canonical, out string error)
    {
        canonical = string.IsNullOrEmpty(raw) ? "NONE" : raw!;
        if (canonical is "NONE" or "ALL_OLD")
        {
            error = string.Empty;
            return true;
        }
        error = $"ReturnValuesOnConditionCheckFailure='{raw}' must be NONE or ALL_OLD.";
        return false;
    }

    private static IReadOnlyDictionary<string, string>? TryMaterialise(JsonElement? el, bool requireStringValues)
    {
        if (el is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in v.EnumerateObject())
        {
            if (requireStringValues && prop.Value.ValueKind != JsonValueKind.String)
                throw new UpdateValidationException(
                    $"ExpressionAttributeNames['{prop.Name}'] must be a string.");
            dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, JsonElement>? TryMaterialiseValues(JsonElement? el)
    {
        if (el is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in v.EnumerateObject())
        {
            if (!ParsedAttributeValue.TryParse(prop.Value, out _))
                throw new UpdateValidationException(
                    $"ExpressionAttributeValues['{prop.Name}'] must be a single-property typed attribute value.");
            dict[prop.Name] = prop.Value;
        }
        return dict;
    }

    private static bool HasContent(string? s) => !string.IsNullOrEmpty(s);
    private static bool HasContent(JsonElement? el)
    {
        if (el is not { } v) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Object => v.EnumerateObject().MoveNext(),
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrEmpty(v.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };
    }
}
