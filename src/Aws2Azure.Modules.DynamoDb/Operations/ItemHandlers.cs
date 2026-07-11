using System;
using System.Buffers;
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
internal static partial class ItemHandlers
{
    // The Cosmos doc shape used by every item write is owned by
    // InferredAttributeStorage. See that class for the on-disk layout
    // and the inference rules for the read path.

    // The item-level request models deserialize with AllowTrailingCommas (see
    // ItemJsonContext), so the on-demand pooled parse of the captured item byte
    // range must accept the same grammar or a body that deserialized fine could
    // throw on re-parse. Comments stay at the serializer default (Disallow).
    private static readonly JsonDocumentOptions ItemDocumentParseOptions = new()
    {
        AllowTrailingCommas = true,
    };

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

        if (!req.Item.IsPresent || body[req.Item.Start] != (byte)'{')
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

        return PutItemCoreAsync(ctx, body, req, condition, rvccf, cosmos, sprocCtx, ct);
    }

    private static async Task PutItemCoreAsync(
        HttpContext ctx, byte[] body, PutItemRequest req, ConditionNode? condition, string rvccf,
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

        // Recover the item's raw UTF-8 bytes from the request buffer (the
        // JsonRange the converter captured) and parse a SHORT-LIVED, pooled
        // JsonDocument for the shape/key validators. Unlike a deserialized
        // JsonElement, this document's metadata DB is rented from the array pool
        // and returned on Dispose, so the validators run unchanged with ~flat
        // allocation regardless of item size. The encoder reads straight from
        // the same bytes (no re-parse, no JsonElement traversal).
        var itemUtf8 = body.AsMemory(req.Item.Start, req.Item.Length);
        using var itemDoc = JsonDocument.Parse(itemUtf8, ItemDocumentParseOptions);
        var item = itemDoc.RootElement;

        // When TTL is enabled, translate the item's absolute epoch-seconds expiry
        // attribute into a Cosmos relative per-item ttl (recomputed every write,
        // so updates keep the absolute expiry correct). Null leaves the doc
        // non-expiring, matching DynamoDB for items lacking the attribute.
        int? ttlSeconds = TtlTranslation.ComputeItemTtlSeconds(
            item, meta.TimeToLive, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Validate that key attribute type tags inside the Item match the
        // table's AttributeDefinitions before we touch Cosmos.
        if (!ValidateKeyAttributesInItem(item, meta, out var validationError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", validationError).ConfigureAwait(false);
            return;
        }

        if (!ValidateItemShape(item, out var shapeError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", shapeError).ConfigureAwait(false);
            return;
        }

        if (!ItemKeyFormatter.TryBuildFromItem(item, meta, out var pk, out var id, out var keyError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
            return;
        }

        // Order-preserving numeric keys for N-typed GSI sort attributes, so
        // ordered GSI queries sort high-precision values correctly (#482). Null
        // for the common table with no such index.
        var orderKeys = SecondaryIndexOrderKeys.Compute(meta, item);

        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(pk);
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var docLink = collLink + "/docs/" + id;

        if (condition is null)
        {
            // Fast path: no condition → unconditional upsert (existing
            // behaviour, preserves the property that PutItem is idempotent
            // when the caller doesn't care about prior state). Zero-copy:
            // the doc is written straight into a pooled UTF-8 buffer handed
            // to the HTTP layer, with no string / StringContent round-trip.
            using var docBuf = ItemDocumentBody.CreateFromItemBytes(id, pk, itemUtf8.Span, cosmos.CosmosBinaryRequests, ttlSeconds, orderKeys);
            var headers = new[]
            {
                new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                new KeyValuePair<string, string>("x-ms-documentdb-is-upsert", "true"),
            };
            using var resp = await cosmos.SendAsync(
                HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
                docBuf.Memory, "application/json", headers, ct).ConfigureAwait(false);

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
            // The sproc embeds the document as a raw JSON value inside its
            // parameter array. Build it once into a pooled UTF-8 buffer (no
            // string / StringContent round-trip) and splice those bytes into
            // the sproc params. Sproc bodies are always text (CosmosBinary does
            // not apply to stored-procedure input), so force the text encoder.
            using var docBuf = ItemDocumentBody.CreateTextFromItemBytes(id, pk, itemUtf8.Span, ttlSeconds, orderKeys);
            var sprocResult = await SprocDispatcher.TryPutItemAsync(
                sprocCtx,
                cosmos,
                req.TableName!,
                pk,
                id,
                docBuf.Memory,
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
        // with bounded retry on 412/409 so concurrent writers replay. The doc
        // is written once into a pooled UTF-8 buffer and re-sent zero-copy on
        // each attempt (no string / StringContent round-trip).
        using var fallbackDoc = ItemDocumentBody.CreateFromItemBytes(id, pk, itemUtf8.Span, cosmos.CosmosBinaryRequests, ttlSeconds, orderKeys);
        var docBody = fallbackDoc.Memory;
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
                        docBody, "application/json", headers, ct).ConfigureAwait(false);
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
                        docBody, "application/json", headers, ct).ConfigureAwait(false);
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

        // Legacy AttributesToGet is rejected loudly (matching Query/Scan/
        // BatchGetItem); clients must use ProjectionExpression instead.
        if (HasContent(req.AttributesToGet))
        {
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Legacy AttributesToGet is not supported; use ProjectionExpression.");
        }

        Projection? projection = null;
        if (HasContent(req.ProjectionExpression))
        {
            IReadOnlyDictionary<string, string>? names = null;
            if (req.ExpressionAttributeNames is { ValueKind: JsonValueKind.Object } eanEl)
            {
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in eanEl.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String)
                    {
                        return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            $"ExpressionAttributeNames['{p.Name}'] must be a string.");
                    }
                    dict[p.Name] = p.Value.GetString()!;
                }
                names = dict;
            }

            try
            {
                projection = ProjectionExpressionParser.Parse(req.ProjectionExpression!, names);
            }
            catch (ExpressionSyntaxException ex)
            {
                return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"Invalid ProjectionExpression (offset {ex.Position}): {ex.Message}");
            }
        }
        else if (HasContent(req.ExpressionAttributeNames))
        {
            // ExpressionAttributeNames is only meaningful alongside an
            // expression; GetItem's sole expression is ProjectionExpression.
            return CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ExpressionAttributeNames can only be specified when using expressions.");
        }

        return GetItemCoreAsync(ctx, req, projection, cosmos, ct);
    }

    private static async Task GetItemCoreAsync(
        HttpContext ctx, GetItemRequest req, Projection? projection, CosmosClient cosmos, CancellationToken ct)
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

        if (projection is not null && projection.HasNestedPaths)
        {
            // Nested ProjectionExpression: the materialized path (extract the
            // item into an AttributeValue map, prune in-process, write the
            // result). Nested pruning needs bounded lookahead to honour
            // DynamoDB's "omit a path that does not exist" semantics, so it is
            // not yet streamed. The item-present response is item-bounded (may
            // exceed the serializer flush threshold), so it is buffered off-pipe
            // and committed with a single write (the GetItem error-wall invariant).
            var item = await CosmosOpsShared.ReadAndExtractItemAsync(
                resp.Content, DynamoDbMetrics.OpGetItem, ct).ConfigureAwait(false);
            if (item is null)
            {
                await CosmosOpsShared.WriteJsonAsync(ctx, 200, new GetItemResponse(),
                    ItemJsonContext.Default.GetItemResponse).ConfigureAwait(false);
                return;
            }
            var response = new GetItemResponse { Item = projection.Apply(item) };
            await CosmosOpsShared.WriteJsonBufferedAsync(ctx, 200, response,
                ItemJsonContext.Default.GetItemResponse, ct).ConfigureAwait(false);
            return;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (projection is not null)
        {
            // Top-level (non-nested) ProjectionExpression: single-pass streaming
            // over the Cosmos body, emitting only the projected top-level
            // attributes — no intermediate AttributeValue map, and the discarded
            // attributes are never materialized. Same fused/text/fallback
            // dispatch and single-write error wall as the non-projected path.
            await CosmosOpsShared.WriteProjectedGetItemEnvelopeAsync(ctx, stream, projection, ct).ConfigureAwait(false);
        }
        else
        {
            await CosmosOpsShared.WriteGetItemEnvelopeAsync(ctx, stream, ct).ConfigureAwait(false);
        }
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
