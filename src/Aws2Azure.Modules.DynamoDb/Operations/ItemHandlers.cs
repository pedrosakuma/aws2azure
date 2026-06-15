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
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
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
    // The Cosmos doc shape used by every item write is owned by
    // InferredAttributeStorage. See that class for the on-disk layout
    // and the inference rules for the read path.

    public static Task HandlePutItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, SprocContext? sprocCtx, CancellationToken ct)
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

        return PutItemCoreAsync(ctx, req, condition, rvccf, cosmos, sprocCtx, ct);
    }

    private static async Task PutItemCoreAsync(
        HttpContext ctx, PutItemRequest req, ConditionNode? condition, string rvccf,
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

        // Conditional path: try sproc (atomic) first, then fall back to GET → evaluate → PUT(If-Match)
        // with bounded retry on 412/409 so concurrent writers replay.

        // Sproc path: single atomic call if enabled
        if (sprocCtx is { IsSprocEnabled: true })
        {
            var sprocResult = await SprocDispatcher.TryPutItemAsync(
                sprocCtx,
                cosmos,
                req.TableName!,
                pk,
                id,
                docJson,
                condition,
                ct).ConfigureAwait(false);

            if (sprocResult.Attempted)
            {
                if (sprocResult.Success)
                {
                    await CosmosOpsShared.WriteJsonAsync(ctx, 200, new PutItemResponse(),
                        ItemJsonContext.Default.PutItemResponse).ConfigureAwait(false);
                    return;
                }
                if (sprocResult.ConditionFailed)
                {
                    // Condition failed - return ConditionalCheckFailedException
                    // TODO: For ReturnValuesOnConditionCheckFailure, we'd need the old item from sproc
                    await ConditionFailureResponder.WriteAsync(ctx, null, rvccf).ConfigureAwait(false);
                    return;
                }
                // Sproc failed with an error but mode is Preferred - fall through to retry loop
                if (sprocCtx.Mode == Core.Configuration.StoredProcedureMode.Required)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
                        $"Sproc execution failed: {sprocResult.Error}").ConfigureAwait(false);
                    return;
                }
                // mode is Preferred: fall through to optimistic concurrency path
            }
            // Sproc not attempted (not available) - fall through if Preferred
            else if (sprocCtx.Mode == Core.Configuration.StoredProcedureMode.Required)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 500, "InternalServerError",
                    "Stored procedure not available and mode is Required").ConfigureAwait(false);
                return;
            }
        }

        // Fallback: GET → evaluate → PUT(If-Match) or POST(If-None-Match: *),
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
                    existingItem = await CosmosOpsShared.ReadAndExtractItemAsync(
                        getResp.Content, DynamoDbMetrics.OpPut, ct).ConfigureAwait(false);
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
        await CosmosOpsShared.WriteGetItemEnvelopeAsync(ctx, stream, ct).ConfigureAwait(false);
    }

    public static Task HandleDeleteItemAsync(HttpContext ctx, byte[] body, CosmosClient cosmos, SprocContext? sprocCtx, CancellationToken ct)
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

        return DeleteItemCoreAsync(ctx, req, condition, rvccf, cosmos, sprocCtx, ct);
    }

    private static async Task DeleteItemCoreAsync(
        HttpContext ctx, DeleteItemRequest req, ConditionNode? condition, string rvccf,
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

        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;

        // Conditional path: try sproc (atomic) first, then fall back to GET → evaluate → DELETE(If-Match)
        // with bounded retry on 412 so concurrent writers replay.

        // Sproc path: single atomic call if enabled
        if (sprocCtx is { IsSprocEnabled: true })
        {
            var sprocResult = await SprocDispatcher.TryDeleteItemAsync(
                sprocCtx,
                cosmos,
                req.TableName!,
                pk,
                id,
                condition,
                ct).ConfigureAwait(false);

            if (sprocResult.Attempted)
            {
                if (sprocResult.Success)
                {
                    await CosmosOpsShared.WriteJsonAsync(ctx, 200, new DeleteItemResponse(),
                        ItemJsonContext.Default.DeleteItemResponse).ConfigureAwait(false);
                    return;
                }
                if (sprocResult.ConditionFailed)
                {
                    // Condition failed - return ConditionalCheckFailedException
                    await ConditionFailureResponder.WriteAsync(ctx, null, rvccf).ConfigureAwait(false);
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

        // Fallback: GET → evaluate → DELETE(If-Match), with
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
                    existingItem = await CosmosOpsShared.ReadAndExtractItemAsync(
                        getResp.Content, DynamoDbMetrics.OpDelete, ct).ConfigureAwait(false);
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
    /// value (per the DynamoDB JSON wire format) AND that each payload's
    /// shape matches its declared type tag (S/N/B → string, BOOL →
    /// boolean, NULL → true, M → object, L → array, SS/NS/BS → array
    /// of strings). Catches malformed inputs early so a write can't
    /// poison the partition with a doc that GetItem cannot parse and so
    /// the encoder's invariants always hold by the time we call it.
    /// </summary>
    internal static bool ValidateItemShape(JsonElement item, out string error)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (InferredAttributeStorage.IsReservedTopLevelName(prop.Name)
                && !InferredAttributeStorage.IsShadowEncodableName(prop.Name))
            {
                error = $"Attribute '{prop.Name}' uses a reserved name and would collide with proxy metadata.";
                return false;
            }
            if (!ValidateAttributePayload(prop.Name, prop.Value, out error))
            {
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Recursive shape validator for a single DDB AttributeValue. Mirrors
    /// the type discipline the inferred encoder relies on, so any
    /// rejection here surfaces as a client <c>ValidationException</c>
    /// instead of an encoder <c>ArgumentException</c> deeper down the
    /// stack. Number / Binary / Set payloads must be strings; sets are
    /// arrays of strings; maps recurse; lists recurse.
    /// </summary>
    private static bool ValidateAttributePayload(string attrName, JsonElement attr, out string error)
    {
        if (!ParsedAttributeValue.TryParse(attr, out var parsed))
        {
            error = $"Attribute '{attrName}' must be a single-property typed attribute value.";
            return false;
        }

        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
            case AttributeValueTypes.Number:
            case AttributeValueTypes.Binary:
                if (parsed.Value.ValueKind != JsonValueKind.String)
                {
                    error = $"Attribute '{attrName}' payload for type {parsed.TypeTag} must be a JSON string.";
                    return false;
                }
                break;

            case AttributeValueTypes.Bool:
                if (parsed.Value.ValueKind != JsonValueKind.True && parsed.Value.ValueKind != JsonValueKind.False)
                {
                    error = $"Attribute '{attrName}' payload for type BOOL must be a JSON boolean.";
                    return false;
                }
                break;

            case AttributeValueTypes.Null:
                if (parsed.Value.ValueKind != JsonValueKind.True)
                {
                    error = $"Attribute '{attrName}' payload for type NULL must be the literal true.";
                    return false;
                }
                break;

            case AttributeValueTypes.Map:
                if (parsed.Value.ValueKind != JsonValueKind.Object)
                {
                    error = $"Attribute '{attrName}' payload for type M must be a JSON object.";
                    return false;
                }
                foreach (var entry in parsed.Value.EnumerateObject())
                {
                    if (entry.Name.StartsWith(
                            InferredAttributeStorage.EnvelopeTagPrefix, StringComparison.Ordinal))
                    {
                        // Encoder enforces this too (InferredAttributeStorage.cs:293)
                        // but raising here keeps the error surface as
                        // ValidationException at the API boundary instead of
                        // an encoder ArgumentException deeper in the stack.
                        error = $"Attribute '{attrName}.{entry.Name}' uses the reserved '"
                            + InferredAttributeStorage.EnvelopeTagPrefix
                            + "' prefix.";
                        return false;
                    }
                    if (!ValidateAttributePayload($"{attrName}.{entry.Name}", entry.Value, out error))
                        return false;
                }
                break;

            case AttributeValueTypes.List:
                if (parsed.Value.ValueKind != JsonValueKind.Array)
                {
                    error = $"Attribute '{attrName}' payload for type L must be a JSON array.";
                    return false;
                }
                int li = 0;
                foreach (var entry in parsed.Value.EnumerateArray())
                {
                    if (!ValidateAttributePayload($"{attrName}[{li}]", entry, out error))
                        return false;
                    li++;
                }
                break;

            case AttributeValueTypes.StringSet:
            case AttributeValueTypes.NumberSet:
            case AttributeValueTypes.BinarySet:
                if (parsed.Value.ValueKind != JsonValueKind.Array)
                {
                    error = $"Attribute '{attrName}' payload for type {parsed.TypeTag} must be a JSON array.";
                    return false;
                }
                foreach (var member in parsed.Value.EnumerateArray())
                {
                    if (member.ValueKind != JsonValueKind.String)
                    {
                        error = $"Attribute '{attrName}' members of {parsed.TypeTag} must be JSON strings.";
                        return false;
                    }
                }
                break;
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
    /// Composes the Cosmos doc shape used for item writes. Thin wrapper
    /// over <see cref="InferredAttributeStorage.BuildCosmosDocument"/>;
    /// kept for call-site stability across the DynamoDb module.
    /// </summary>
    internal static string BuildItemDocument(string id, string pk, JsonElement item)
        => InferredAttributeStorage.BuildCosmosDocument(id, pk, item);

    /// <summary>
    /// Extracts the DDB attribute map from a Cosmos doc, projecting
    /// every non-reserved root property back into AttributeValue form
    /// via <see cref="InferredAttributeStorage.ExtractItem(Stream)"/>.
    /// </summary>
    internal static Dictionary<string, JsonElement>? ExtractItemFromCosmosDoc(Stream cosmosDocBody)
        => InferredAttributeStorage.ExtractItem(cosmosDocBody);

    internal static Dictionary<string, JsonElement>? ExtractItemFromCosmosDoc(ReadOnlyMemory<byte> cosmosDocBody)
    {
        using var doc = JsonDocument.Parse(cosmosDocBody);
        return InferredAttributeStorage.ExtractItem(doc.RootElement);
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
