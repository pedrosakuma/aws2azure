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
/// Handlers for the DynamoDB item-level operations
/// (<c>PutItem</c>, <c>GetItem</c>, <c>DeleteItem</c>). Each handler:
///
/// <list type="number">
/// <item>Parses the JSON 1.0 request into the matching DTO.</item>
/// <item>Reads the table's sidecar metadata to learn the HASH/RANGE
///   attribute names + scalar types.</item>
/// <item>Routes the doc by formatting the key into Cosmos
///   <c>pk</c> / <c>id</c> scalars (HASH → <c>pk</c>, RANGE → <c>id</c>
///   when composite, else HASH → both).</item>
/// <item>Stores the full DynamoDB Item map verbatim under <c>item</c> so
///   reads round-trip without any type erosion.</item>
/// </list>
///
/// <para>Conditional writes, projection expressions, and ReturnValues
/// modes other than <c>NONE</c> are deferred to the expression-parser
/// slices and rejected here with <c>ValidationException</c> so callers
/// surface the gap explicitly.</para>
/// </summary>
internal static class ItemHandlers
{
    // Sentinel property names inside the Cosmos doc. Item attributes
    // live under "item" to keep them separate from the routing fields
    // (id / pk) and the future indexing fields (_a2a / _meta).
    internal const string ItemEnvelopeProperty = "item";
    internal const string DiscriminatorProperty = "_a2a";
    internal const string DiscriminatorValueItem = "item";

    public static Task HandlePutItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        PutItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.PutItemRequest);
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

        if (req.Item.ValueKind != JsonValueKind.Object)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Item is required and must be a JSON object.");
        }

        if (HasContent(req.ConditionExpression) && (HasContent(req.Expected) || HasContent(req.ConditionalOperator)))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ConditionExpression and the legacy Expected/ConditionalOperator parameters are mutually exclusive.");
        }

        ConditionNode? condition;
        try
        {
            var names = TryMaterialiseNames(req.ExpressionAttributeNames);
            var values = TryMaterialiseValues(req.ExpressionAttributeValues);
            condition = ConditionGate.TryParse(
                req.ConditionExpression, req.Expected, req.ConditionalOperator, names, values);
        }
        catch (ExpressionSyntaxException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Invalid expression (offset {ex.Position}): {ex.Message}");
        }
        catch (ConditionParseConflictException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(req.ConditionExpression)
            && (HasContent(req.ExpressionAttributeNames) || HasContent(req.ExpressionAttributeValues)))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ExpressionAttributeNames/Values were supplied but no ConditionExpression references them.");
        }

        if (!IsAllowedReturnValuesForWrite(req.ReturnValues, out var rvError))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvError);
        }

        if (!IsAllowedRvccf(req.ReturnValuesOnConditionCheckFailure, out var rvccf, out var rvccfErr))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvccfErr);
        }

        return PutItemCoreAsync(ctx, req, condition, rvccf, cosmos, ct);
    }

    private static async Task PutItemCoreAsync(
        HttpContext ctx, PutItemRequest req, ConditionNode? condition, string rvccf,
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

        // Validate that key attribute type tags inside the Item match the
        // table's AttributeDefinitions before we touch Cosmos.
        if (!ValidateKeyAttributesInItem(req.Item, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ValidateItemShape(req.Item, out var shapeError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", shapeError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuildFromItem(req.Item, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        var docJson = BuildItemDocument(id, pk, req.Item);
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var docLink = collLink + "/docs/" + id;

        if (condition is null)
        {
            // Fast path: no condition → unconditional upsert (existing
            // behaviour, preserves the property that PutItem is idempotent
            // when the caller doesn't care about prior state).
            using var content = new StringContent(docJson, Encoding.UTF8, "application/json");
            var headers = new[]
            {
                new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                new KeyValuePair<string, string>("x-ms-documentdb-is-upsert", "true"),
            };
            using var resp = await cosmos.SendAsync(
                HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
                content, headers, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                    $"Table not found: {req.TableName}").ConfigureAwait(false);
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
                return;
            }
            await CosmosOpsShared.WriteJsonAsync(ctx, 200, new PutItemResponse(),
                ItemJsonContext.Default.PutItemResponse).ConfigureAwait(false);
            return;
        }

        // Conditional path: GET → evaluate → PUT(If-Match) or POST(If-None-Match: *),
        // with bounded retry on 412/409 so concurrent writers replay.
        const int MaxRetries = 4;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
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
                    existingItem = ExtractItemFromCosmosDoc(s);
                }
            }

            bool pass;
            try { pass = ConditionEvaluator.Evaluate(condition, existingItem); }
            catch (ConditionEvaluationException cex)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", cex.Message).ConfigureAwait(false);
                return;
            }
            if (!pass)
            {
                await ConditionFailureResponder.WriteAsync(ctx, existingItem, rvccf).ConfigureAwait(false);
                return;
            }

            HttpResponseMessage writeResp;
            using (var content = new StringContent(docJson, Encoding.UTF8, "application/json"))
            {
                if (existingItem is not null && etag is not null)
                {
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
                    var headers = new[]
                    {
                        new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                        new KeyValuePair<string, string>("If-None-Match", "*"),
                    };
                    writeResp = await cosmos.SendAsync(
                        HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
                        content, headers, ct).ConfigureAwait(false);
                }
            }

            using (writeResp)
            {
                if (writeResp.StatusCode == HttpStatusCode.PreconditionFailed
                    || writeResp.StatusCode == HttpStatusCode.Conflict)
                {
                    // Lost a race; loop re-reads and re-checks.
                    continue;
                }
                if (writeResp.StatusCode == HttpStatusCode.NotFound)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                        $"Table not found: {req.TableName}").ConfigureAwait(false);
                    return;
                }
                if (!writeResp.IsSuccessStatusCode)
                {
                    await CosmosOpsShared.WriteCosmosErrorAsync(ctx, writeResp, ct).ConfigureAwait(false);
                    return;
                }
            }

            await CosmosOpsShared.WriteJsonAsync(ctx, 200, new PutItemResponse(),
                ItemJsonContext.Default.PutItemResponse).ConfigureAwait(false);
            return;
        }

        await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
            "Failed to converge on a conditional PutItem after multiple retries due to contention.").ConfigureAwait(false);
    }

    public static Task HandleGetItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        GetItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.GetItemRequest);
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

        if (HasContent(req.AttributesToGet)
            || HasContent(req.ProjectionExpression)
            || HasContent(req.ExpressionAttributeNames))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Projection / AttributesToGet are not supported in this slice.");
        }

        return GetItemCoreAsync(ctx, req, cosmos, ct);
    }

    private static async Task GetItemCoreAsync(
        HttpContext ctx, GetItemRequest req, CosmosClient cosmos, CancellationToken ct)
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

        if (!ValidateKeyAttributesInKey(req.Key, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuild(req.Key, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName + "/docs/" + id;
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-ms-documentdb-partitionkey", pkHeader),
        };
        if (req.ConsistentRead == true)
        {
            // Cosmos has no perfect mapping for DynamoDB strong consistency,
            // but session-bound Strong-equivalent is selected via the
            // x-ms-consistency-level header for accounts that allow it.
            // Configured accounts at "Session" or below will simply ignore.
            headers.Add(new("x-ms-consistency-level", "Strong"));
        }

        using var resp = await cosmos.SendAsync(
            HttpMethod.Get, "docs", docLink, "/" + docLink,
            content: null, headers, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // Distinguish "container deleted between metadata read and op"
            // (DynamoDB ResourceNotFoundException) from "item not present"
            // (DynamoDB GetItem returns 200 with no `Item`). Cosmos signals
            // the former via x-ms-substatus: 1003.
            if (CosmosOpsShared.Is404ContainerMissing(resp))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                    $"Table not found: {req.TableName}").ConfigureAwait(false);
                return;
            }
            // DynamoDB GetItem returns 200 with an empty body when the
            // item does not exist; consumers must not depend on a 404.
            await CosmosOpsShared.WriteJsonAsync(ctx, 200, new GetItemResponse(),
                ItemJsonContext.Default.GetItemResponse).ConfigureAwait(false);
            return;
        }

        if (!resp.IsSuccessStatusCode)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var item = ExtractItemFromCosmosDoc(stream);
        var response = new GetItemResponse { Item = item };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, response,
            ItemJsonContext.Default.GetItemResponse).ConfigureAwait(false);
    }

    public static Task HandleDeleteItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        DeleteItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ItemJsonContext.Default.DeleteItemRequest);
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

        ConditionNode? condition;
        try
        {
            var names = TryMaterialiseNames(req.ExpressionAttributeNames);
            var values = TryMaterialiseValues(req.ExpressionAttributeValues);
            condition = ConditionGate.TryParse(
                req.ConditionExpression, req.Expected, req.ConditionalOperator, names, values);
        }
        catch (ExpressionSyntaxException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Invalid expression (offset {ex.Position}): {ex.Message}");
        }
        catch (ConditionParseConflictException ex)
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(req.ConditionExpression)
            && (HasContent(req.ExpressionAttributeNames) || HasContent(req.ExpressionAttributeValues)))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ExpressionAttributeNames/Values were supplied but no ConditionExpression references them.");
        }

        if (!IsAllowedReturnValuesForWrite(req.ReturnValues, out var rvError))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvError);
        }

        if (!IsAllowedRvccf(req.ReturnValuesOnConditionCheckFailure, out var rvccf, out var rvccfErr))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", rvccfErr);
        }

        return DeleteItemCoreAsync(ctx, req, condition, rvccf, cosmos, ct);
    }

    private static async Task DeleteItemCoreAsync(
        HttpContext ctx, DeleteItemRequest req, ConditionNode? condition, string rvccf,
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

        if (!ValidateKeyAttributesInKey(req.Key, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuild(req.Key, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName + "/docs/" + id;
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);

        if (condition is null)
        {
            // Fast path: unconditional delete (existing behaviour). DDB's
            // DeleteItem is idempotent — a missing item is a success.
            var headers = new[]
            {
                new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
            };
            using var resp = await cosmos.SendAsync(
                HttpMethod.Delete, "docs", docLink, "/" + docLink,
                content: null, headers, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (CosmosOpsShared.Is404ContainerMissing(resp))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                        $"Table not found: {req.TableName}").ConfigureAwait(false);
                    return;
                }
                await CosmosOpsShared.WriteJsonAsync(ctx, 200, new DeleteItemResponse(),
                    ItemJsonContext.Default.DeleteItemResponse).ConfigureAwait(false);
                return;
            }
            if (resp.IsSuccessStatusCode)
            {
                await CosmosOpsShared.WriteJsonAsync(ctx, 200, new DeleteItemResponse(),
                    ItemJsonContext.Default.DeleteItemResponse).ConfigureAwait(false);
                return;
            }

            await CosmosOpsShared.WriteCosmosErrorAsync(ctx, resp, ct).ConfigureAwait(false);
            return;
        }

        // Conditional path: GET → evaluate → DELETE(If-Match), with
        // bounded retry on 412 so concurrent writers replay.
        const int MaxRetries = 4;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
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
                    // Item missing → existingItem stays null. Condition must
                    // still be evaluated against the missing-item state so
                    // e.g. attribute_not_exists conditions on a missing item
                    // pass and DeleteItem is a successful no-op.
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
                    existingItem = ExtractItemFromCosmosDoc(s);
                }
            }

            bool pass;
            try { pass = ConditionEvaluator.Evaluate(condition, existingItem); }
            catch (ConditionEvaluationException cex)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", cex.Message).ConfigureAwait(false);
                return;
            }
            if (!pass)
            {
                await ConditionFailureResponder.WriteAsync(ctx, existingItem, rvccf).ConfigureAwait(false);
                return;
            }

            if (existingItem is null)
            {
                // Condition passed against a missing item → nothing to
                // delete. DDB returns success with no Attributes.
                await CosmosOpsShared.WriteJsonAsync(ctx, 200, new DeleteItemResponse(),
                    ItemJsonContext.Default.DeleteItemResponse).ConfigureAwait(false);
                return;
            }

            var deleteHeaders = new[]
            {
                new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                new KeyValuePair<string, string>("If-Match", etag!),
            };
            using var delResp = await cosmos.SendAsync(
                HttpMethod.Delete, "docs", docLink, "/" + docLink,
                content: null, deleteHeaders, ct).ConfigureAwait(false);

            if (delResp.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                continue;
            }
            if (delResp.StatusCode == HttpStatusCode.NotFound)
            {
                if (CosmosOpsShared.Is404ContainerMissing(delResp))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                        $"Table not found: {req.TableName}").ConfigureAwait(false);
                    return;
                }
                // Doc deleted between GET and DELETE (another writer) →
                // re-loop so the condition is re-evaluated against the
                // new state.
                continue;
            }
            if (!delResp.IsSuccessStatusCode)
            {
                await CosmosOpsShared.WriteCosmosErrorAsync(ctx, delResp, ct).ConfigureAwait(false);
                return;
            }

            await CosmosOpsShared.WriteJsonAsync(ctx, 200, new DeleteItemResponse(),
                ItemJsonContext.Default.DeleteItemResponse).ConfigureAwait(false);
            return;
        }

        await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
            "Failed to converge on a conditional DeleteItem after multiple retries due to contention.").ConfigureAwait(false);
    }

    // ----- helpers ---------------------------------------------------

    /// <summary>
    /// Validates every attribute in the Item is a single-property typed
    /// value (per the DynamoDB JSON wire format). Catches malformed
    /// inputs early so a write can't poison the partition with a doc
    /// that GetItem cannot parse.
    /// </summary>
    private static bool ValidateItemShape(JsonElement item, out string error)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (!ParsedAttributeValue.TryParse(prop.Value, out _))
            {
                error = $"Attribute '{prop.Name}' must be a single-property typed attribute value.";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static bool ValidateKeyAttributesInItem(JsonElement item, TableMetadata meta, out string error)
    {
        foreach (var k in meta.KeySchema)
        {
            if (!item.TryGetProperty(k.Name, out var attr))
            {
                error = $"Item is missing required key attribute '{k.Name}'.";
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

    private static bool ValidateKeyAttributesInKey(JsonElement key, TableMetadata meta, out string error)
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

    /// <summary>
    /// Composes the Cosmos doc shape:
    /// <c>{"id":"&lt;id&gt;","pk":"&lt;pk&gt;","_a2a":"item","item":&lt;raw map&gt;}</c>.
    /// The raw item is written via <see cref="JsonElement.GetRawText"/>
    /// so the on-wire bytes are preserved (number precision intact).
    /// </summary>
    internal static string BuildItemDocument(string id, string pk, JsonElement item)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("pk", pk);
            writer.WriteString(DiscriminatorProperty, DiscriminatorValueItem);
            writer.WritePropertyName(ItemEnvelopeProperty);
            item.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Extracts the <c>item</c> envelope from a Cosmos doc, projecting
    /// each attribute as a <see cref="JsonElement"/> clone so the
    /// returned dictionary outlives the source document.
    /// </summary>
    internal static Dictionary<string, JsonElement>? ExtractItemFromCosmosDoc(Stream cosmosDocBody)
    {
        using var doc = JsonDocument.Parse(cosmosDocBody);
        if (!doc.RootElement.TryGetProperty(ItemEnvelopeProperty, out var envelope)
            || envelope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in envelope.EnumerateObject())
        {
            // Clone so the returned element is detached from the
            // JsonDocument we're about to dispose.
            result[prop.Name] = prop.Value.Clone();
        }
        return result;
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

    private static bool IsAllowedReturnValuesForWrite(string? rv, out string error)
    {
        if (string.IsNullOrEmpty(rv) || rv == "NONE")
        {
            error = string.Empty;
            return true;
        }
        error = $"ReturnValues='{rv}' is not supported in this slice (only NONE).";
        return false;
    }

    private static bool IsAllowedRvccf(string? raw, out string canonical, out string error)
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

    private static IReadOnlyDictionary<string, string>? TryMaterialiseNames(JsonElement? el)
    {
        if (el is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in v.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                throw new ExpressionSyntaxException(0,
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
            dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }

    private static string? ExtractETag(HttpResponseMessage resp)
    {
        if (resp.Headers.ETag is { } e) return e.Tag;
        if (resp.Headers.TryGetValues("etag", out var vs))
        {
            foreach (var v in vs) return v;
        }
        return null;
    }
}
