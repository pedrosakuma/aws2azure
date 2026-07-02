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
using Aws2Azure.Core.Buffers;
using Aws2Azure.Core.Configuration;
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
        HttpContext ctx, byte[] body, CosmosClient cosmos, SprocContext? sprocCtx, CancellationToken ct)
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

        return UpdateItemCoreAsync(ctx, req, ast, condition, rvCanonical, rvccfCanonical, cosmos, sprocCtx, ct);
    }

    private static async Task UpdateItemCoreAsync(
        HttpContext ctx, UpdateItemRequest req, UpdateExpressionAst ast, ConditionNode? condition,
        string returnValues, string returnValuesOnConditionCheckFailure,
        CosmosClient cosmos, SprocContext? sprocCtx, CancellationToken ct)
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

        // Sproc path: single atomic call if enabled. Skipped when the table has
        // TTL enabled: the server-side JS sproc upserts the new document without
        // recomputing the Cosmos native `ttl` (it has no access to the table's
        // TTL config or a recompute-on-write step), so an update that changes or
        // removes the TTL attribute would leave a stale/absent expiry. The C#
        // GET → apply → PUT fallback below recomputes `ttl` from the merged item.
        //
        // Likewise skipped when the table has N-typed GSI sort keys: the sproc
        // merges the update server-side and cannot emit the order-preserving
        // `_a2a$ord$<attr>` fields (#482), so an update that changes such a sort
        // attribute would leave a stale order key and mis-order ordered GSI
        // queries. The C# fallback rebuilds those fields from the merged item.
        if (sprocCtx is { IsSprocEnabled: true }
            && meta.TimeToLive is not { Enabled: true }
            && meta.NumericGsiSortKeys.Count == 0)
        {
            // For UpdateItem upsert case, pass the key attributes so sproc can
            // build a new item. Written straight into a pooled UTF-8 buffer (no
            // string / StringContent round-trip) and spliced into the sproc
            // params as raw bytes.
            using var keyAttrsBuf = BuildKeyAttributesBytes(id, pk);
            var sprocResult = await SprocDispatcher.TryUpdateItemAsync(
                sprocCtx,
                cosmos,
                req.TableName!,
                pk,
                id,
                keyAttrsBuf.WrittenMemory,
                condition,
                ast,
                returnValues,
                returnValuesOnConditionCheckFailure,
                ct).ConfigureAwait(false);

            if (sprocResult.Attempted)
            {
                if (sprocResult.Success)
                {
                    // Build response with ReturnValues from sproc result
                    var sprocExecResult = new UpdateExecutor.ExecutionResult
                    {
                        ItemExistedBefore = sprocResult.OldItem is not null,
                        OldItem = sprocResult.OldItem,
                        NewItem = sprocResult.NewItem ?? new Dictionary<string, JsonElement>()
                    };
                    await WriteSuccessAsync(ctx, returnValues, sprocExecResult, ast).ConfigureAwait(false);
                    return;
                }
                if (sprocResult.ConditionFailed)
                {
                    // Condition failed - return ConditionalCheckFailedException
                    await ConditionFailureResponder.WriteAsync(ctx, sprocResult.OldItem, returnValuesOnConditionCheckFailure).ConfigureAwait(false);
                    return;
                }
                // Sproc failed with an error but mode is Preferred - fall through to retry loop
                if (sprocCtx.Mode == StoredProcedureMode.Required)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
                        $"Sproc execution failed: {sprocResult.Error}").ConfigureAwait(false);
                    return;
                }
                // mode is Preferred: fall through to optimistic concurrency path
            }
            // Sproc not attempted (not available) - fall through if Preferred
            else if (sprocCtx.Mode == StoredProcedureMode.Required)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
                    "Stored procedure not available and mode is Required").ConfigureAwait(false);
                return;
            }
        }

        // Fallback: GET → evaluate → PUT(If-Match) or POST(upsert),
        // with bounded retry on 412 so concurrent writers replay.
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
                    existingItem = await CosmosOpsShared.ReadAndExtractItemAsync(
                        getResp.Content, DynamoDbMetrics.OpUpdate, ct).ConfigureAwait(false);
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

            // Translate an enabled TTL attribute's absolute epoch expiry into a
            // Cosmos relative per-item ttl, recomputed on every update so the
            // absolute expiry stays correct across writes.
            int? ttlSeconds = TtlTranslation.ComputeItemTtlSeconds(
                newItemJson, meta.TimeToLive, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            // Build the Cosmos doc envelope from the mutated item map,
            // straight into a pooled UTF-8 buffer (no string / StringContent
            // round-trip on the write body).
            var orderKeys = SecondaryIndexOrderKeys.Compute(meta, newItemJson);
            using var docBuf = ItemHandlers.ItemDocumentBody.Create(id, pk, newItemJson, cosmos.CosmosBinaryRequests, ttlSeconds, orderKeys);
            var docBody = docBuf.Memory;

            HttpResponseMessage writeResp;
            if (execResult.ItemExistedBefore && etag is not null)
            {
                // Optimistic replace: PUT with If-Match.
                var headers = new[]
                {
                    new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                    new KeyValuePair<string, string>("If-Match", etag),
                };
                writeResp = await cosmos.SendAsync(
                    HttpMethod.Put, "docs", docLink, "/" + docLink,
                    docBody, "application/json", headers, ct).ConfigureAwait(false);
            }
            else
            {
                // Item did not exist → atomic create with If-None-Match: *
                // so a concurrent UpdateItem cannot race us and lose the
                // other writer's mutation. On 409/412 the loop re-reads
                // and replays the update against the winner's state.
                var headers = new[]
                {
                    new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                    new KeyValuePair<string, string>("If-None-Match", "*"),
                };
                writeResp = await cosmos.SendAsync(
                    HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
                    docBody, "application/json", headers, ct).ConfigureAwait(false);
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
        HttpContext ctx, string returnValues, UpdateExecutor.ExecutionResult result,
        UpdateExpressionAst? ast = null)
    {
        // Use UpdatedAttributes from result if available, otherwise extract from AST
        var updatedAttrs = result.UpdatedAttributes.Count > 0 
            ? result.UpdatedAttributes 
            : ExtractUpdatedAttributes(ast);
        
        Dictionary<string, JsonElement>? attrs = returnValues switch
        {
            "NONE" => null,
            "ALL_OLD" => result.ItemExistedBefore ? result.OldItem : null,
            "ALL_NEW" => result.NewItem,
            "UPDATED_OLD" => ProjectAttributes(result.OldItem, updatedAttrs),
            "UPDATED_NEW" => ProjectAttributes(result.NewItem, updatedAttrs),
            _ => null,
        };
        var response = new UpdateItemResponse { Attributes = attrs };
        return CosmosOpsShared.WriteJsonAsync(ctx, 200, response, ItemJsonContext.Default.UpdateItemResponse);
    }

    /// <summary>
    /// Extracts top-level attribute names from the UpdateExpression AST.
    /// Used when UpdatedAttributes aren't tracked by the executor (sproc path).
    /// </summary>
    private static HashSet<string> ExtractUpdatedAttributes(UpdateExpressionAst? ast)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (ast is null) return result;
        
        if (ast.Set is not null)
        {
            foreach (var action in ast.Set.Actions)
            {
                result.Add(action.Path.Root);
            }
        }
        if (ast.Remove is not null)
        {
            foreach (var path in ast.Remove.Paths)
            {
                result.Add(path.Root);
            }
        }
        if (ast.Add is not null)
        {
            foreach (var action in ast.Add.Actions)
            {
                result.Add(action.Path.Root);
            }
        }
        if (ast.Delete is not null)
        {
            foreach (var action in ast.Delete.Actions)
            {
                result.Add(action.Path.Root);
            }
        }
        return result;
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

    /// <summary>
    /// Builds the Cosmos envelope key attributes (<c>id</c>, <c>_a2a_pk</c>,
    /// <c>_a2a</c>) as a pooled UTF-8 buffer for the sproc upsert case. The
    /// object is fixed-shape and small; building it as bytes lets the sproc
    /// parameter list be assembled and sent without a <c>StringContent</c>
    /// re-encode of the whole body. Output is byte-identical to
    /// <see cref="BuildKeyAttributesJson"/>.
    /// </summary>
    private static PooledByteBufferWriter BuildKeyAttributesBytes(string id, string pk)
    {
        var json = BuildKeyAttributesJson(id, pk);
        var buf = new PooledByteBufferWriter(128);
        try
        {
            var span = buf.GetSpan(Encoding.UTF8.GetMaxByteCount(json.Length));
            buf.Advance(Encoding.UTF8.GetBytes(json, span));
        }
        catch
        {
            buf.Dispose();
            throw;
        }
        return buf;
    }

    /// <summary>
    /// Builds a JSON object with Cosmos envelope fields for sproc upsert case.
    /// Must match InferredAttributeStorage document structure: id, _a2a_pk, _a2a.
    /// </summary>
    private static string BuildKeyAttributesJson(string id, string pk)
    {
        var sb = new StringBuilder(128);
        sb.Append("{\"id\":\"");
        EscapeJsonStringTo(sb, id);
        sb.Append("\",\"_a2a_pk\":\"");
        EscapeJsonStringTo(sb, pk);
        sb.Append("\",\"_a2a\":\"item\"}");
        return sb.ToString();
    }

    private static void EscapeJsonStringTo(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
    }
}
