using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>BatchGetItem</c> → fan-out of per-item Cosmos
/// <c>GET /docs/{id}</c> requests with bounded concurrency. Mirrors
/// the AWS semantics:
///
/// <list type="bullet">
///   <item>At most 100 keys per request (across all tables); requests
///   that exceed the cap are rejected with
///   <c>ValidationException</c>.</item>
///   <item>Per-key throttling (Cosmos <c>429</c>) is surfaced as
///   <c>UnprocessedKeys</c> so the SDK retries the throttled subset
///   only — the rest of the batch still succeeds.</item>
///   <item><c>ProjectionExpression</c> + <c>ExpressionAttributeNames</c>
///   honoured per table (top-level attribute names / <c>#alias</c> only,
///   matching <see cref="QueryHandler"/> and <see cref="ScanHandler"/>).</item>
///   <item><c>ConsistentRead=true</c> per table forwards
///   <c>x-ms-consistency-level: Strong</c> on every GET for that table.</item>
///   <item>Legacy <c>AttributesToGet</c> is rejected loudly.</item>
/// </list>
/// </summary>
internal static class BatchGetItemHandler
{
    private const int MaxItemsPerCall = 100;
    private const int MaxParallelism = 16;

    public static async Task HandleBatchGetItemAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        BatchGetItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, BatchGetItemJsonContext.Default.BatchGetItemRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || req.RequestItems is null || req.RequestItems.Count == 0)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "RequestItems is required and must contain at least one table.").ConfigureAwait(false);
            return;
        }

        // Flatten the request into per-key work units while validating
        // every table & key up-front. Failing fast on a single bad key
        // matches DynamoDB: the *whole* batch is rejected on a syntactic
        // error; only runtime/throttling errors fall into UnprocessedKeys.
        var work = new List<WorkUnit>();
        var tableMeta = new Dictionary<string, TableMetadata>(StringComparer.Ordinal);
        var tableProjection = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
        var tableConsistent = new Dictionary<string, bool>(StringComparer.Ordinal);

        int totalKeys = 0;
        foreach (var (tableName, perTable) in req.RequestItems)
        {
            if (!DynamoDbNames.IsValidTableName(tableName))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"Invalid TableName '{tableName}'.").ConfigureAwait(false);
                return;
            }
            if (perTable.ValueKind != JsonValueKind.Object)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"RequestItems['{tableName}'] must be an object.").ConfigureAwait(false);
                return;
            }
            if (perTable.TryGetProperty("AttributesToGet", out _))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    "Legacy AttributesToGet is not supported; use ProjectionExpression.").ConfigureAwait(false);
                return;
            }
            if (!perTable.TryGetProperty("Keys", out var keysEl) || keysEl.ValueKind != JsonValueKind.Array)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"RequestItems['{tableName}'].Keys must be a non-empty array.").ConfigureAwait(false);
                return;
            }
            int keyCount = keysEl.GetArrayLength();
            if (keyCount == 0)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"RequestItems['{tableName}'].Keys must be a non-empty array.").ConfigureAwait(false);
                return;
            }
            totalKeys += keyCount;
            if (totalKeys > MaxItemsPerCall)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"BatchGetItem accepts at most {MaxItemsPerCall} keys per call.").ConfigureAwait(false);
                return;
            }

            // Read table metadata once per table.
            using var metaRead = await CosmosOpsShared.TryReadTableMetadataAsync(cosmos, tableName, ct).ConfigureAwait(false);
            if (metaRead.Status == CosmosOpsShared.TableMetadataReadStatus.CosmosError)
            {
                await CosmosOpsShared.WriteCosmosErrorAsync(ctx, metaRead.ErrorResponse!, ct).ConfigureAwait(false);
                return;
            }
            if (metaRead.Status == CosmosOpsShared.TableMetadataReadStatus.NotFound)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                    $"Table not found: {tableName}").ConfigureAwait(false);
                return;
            }
            var meta = metaRead.Metadata!;
            tableMeta[tableName] = meta;

            // ConsistentRead per table.
            bool consistent = false;
            if (perTable.TryGetProperty("ConsistentRead", out var crEl) && crEl.ValueKind == JsonValueKind.True)
            {
                consistent = true;
            }
            tableConsistent[tableName] = consistent;

            // ProjectionExpression per table.
            IReadOnlyList<string>? projection = null;
            if (perTable.TryGetProperty("ProjectionExpression", out var peEl)
                && peEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(peEl.GetString()))
            {
                IReadOnlyDictionary<string, string>? names = null;
                if (perTable.TryGetProperty("ExpressionAttributeNames", out var eanEl)
                    && eanEl.ValueKind == JsonValueKind.Object)
                {
                    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in eanEl.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.String)
                        {
                            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                                $"ExpressionAttributeNames['{p.Name}'] must be a string.").ConfigureAwait(false);
                            return;
                        }
                        dict[p.Name] = p.Value.GetString()!;
                    }
                    names = dict;
                }
                try
                {
                    projection = ProjectionExpressionParser.Parse(peEl.GetString()!, names);
                }
                catch (ExpressionSyntaxException ex)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"Invalid ProjectionExpression (offset {ex.Position}): {ex.Message}").ConfigureAwait(false);
                    return;
                }
            }
            tableProjection[tableName] = projection;

            // Build one WorkUnit per key, validating both type tags and
            // routing while the metadata is still in hand.
            foreach (var keyEl in keysEl.EnumerateArray())
            {
                if (keyEl.ValueKind != JsonValueKind.Object)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"RequestItems['{tableName}'].Keys entries must be objects.").ConfigureAwait(false);
                    return;
                }
                foreach (var k in meta.KeySchema)
                {
                    if (!keyEl.TryGetProperty(k.Name, out var attr))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            $"Key in '{tableName}' is missing required attribute '{k.Name}'.").ConfigureAwait(false);
                        return;
                    }
                    if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", typeError).ConfigureAwait(false);
                        return;
                    }
                }
                if (!ItemKeyFormatter.TryBuild(keyEl, meta, out var pk, out var id, out var keyError))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
                    return;
                }
                var cloned = keyEl.Clone();
                work.Add(new WorkUnit(tableName, pk, id, cloned));
            }
        }

        // Fan-out: parallel GETs with a semaphore so we never queue
        // more than MaxParallelism concurrent Cosmos calls regardless
        // of the per-batch cap.
        var results = new PerItemResult[work.Count];
        using (var sem = new SemaphoreSlim(MaxParallelism))
        {
            var tasks = new Task[work.Count];
            for (int i = 0; i < work.Count; i++)
            {
                var idx = i;
                var unit = work[i];
                tasks[i] = ExecuteOneAsync(cosmos, unit, tableConsistent[unit.Table], sem, results, idx, ct);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // Stitch results into Responses + UnprocessedKeys. A non-throttle
        // Cosmos error on any item fails the whole batch (matches
        // DynamoDB: only throttling lands in UnprocessedKeys).
        var responses = new Dictionary<string, List<Dictionary<string, JsonElement>>>(StringComparer.Ordinal);
        Dictionary<string, BatchGetUnprocessedTable>? unprocessed = null;
        for (int i = 0; i < work.Count; i++)
        {
            var r = results[i];
            var unit = work[i];
            if (r.HardError is not null)
            {
                // Surface the first hard error verbatim. Drains pending
                // tasks already completed above; nothing else to do.
                await CosmosOpsShared.WriteErrorAsync(ctx, r.HardError.Value.Status,
                    r.HardError.Value.Code, r.HardError.Value.Message).ConfigureAwait(false);
                return;
            }
            if (r.Throttled)
            {
                unprocessed ??= new Dictionary<string, BatchGetUnprocessedTable>(StringComparer.Ordinal);
                if (!unprocessed.TryGetValue(unit.Table, out var u))
                {
                    u = new BatchGetUnprocessedTable
                    {
                        Keys = new List<Dictionary<string, JsonElement>>(),
                        ConsistentRead = tableConsistent[unit.Table] ? true : null,
                    };
                    // Echo the original projection metadata so the retry
                    // is a verbatim re-issue.
                    if (req.RequestItems[unit.Table].TryGetProperty("ProjectionExpression", out var peEl)
                        && peEl.ValueKind == JsonValueKind.String)
                    {
                        u.ProjectionExpression = peEl.GetString();
                    }
                    if (req.RequestItems[unit.Table].TryGetProperty("ExpressionAttributeNames", out var eanEl)
                        && eanEl.ValueKind == JsonValueKind.Object)
                    {
                        var d = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var p in eanEl.EnumerateObject())
                        {
                            d[p.Name] = p.Value.GetString()!;
                        }
                        u.ExpressionAttributeNames = d;
                    }
                    unprocessed[unit.Table] = u;
                }
                u.Keys!.Add(JsonElementToDict(unit.OriginalKey));
                continue;
            }
            if (r.Item is null)
            {
                // Missing item — DynamoDB simply omits it from Responses.
                continue;
            }
            if (!responses.TryGetValue(unit.Table, out var list))
            {
                list = new List<Dictionary<string, JsonElement>>();
                responses[unit.Table] = list;
            }
            var item = r.Item;
            var projection = tableProjection[unit.Table];
            if (projection is not null)
            {
                item = Project(item, projection);
            }
            list.Add(item);
        }

        var resp = new BatchGetItemResponse
        {
            Responses = responses,
            UnprocessedKeys = unprocessed,
        };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, resp,
            BatchGetItemJsonContext.Default.BatchGetItemResponse).ConfigureAwait(false);
    }

    private static async Task ExecuteOneAsync(
        CosmosClient cosmos, WorkUnit unit, bool consistent,
        SemaphoreSlim sem, PerItemResult[] results, int idx, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + unit.Table + "/docs/" + unit.Id;
            var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(unit.Pk);
            var headers = new List<KeyValuePair<string, string>>
            {
                new("x-ms-documentdb-partitionkey", pkHeader),
            };
            if (consistent)
            {
                headers.Add(new("x-ms-consistency-level", "Strong"));
            }
            using var resp = await cosmos.SendAsync(
                HttpMethod.Get, "docs", docLink, "/" + docLink,
                content: null, headers, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (CosmosOpsShared.Is404ContainerMissing(resp))
                {
                    results[idx] = new PerItemResult(null, false,
                        new HardError(400, "ResourceNotFoundException",
                            $"Table not found: {unit.Table}"));
                    return;
                }
                // DynamoDB returns missing items by omitting them — no error.
                results[idx] = new PerItemResult(null, false, null);
                return;
            }
            if (resp.StatusCode == (HttpStatusCode)429)
            {
                results[idx] = new PerItemResult(null, true, null);
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                string body = string.Empty;
                try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch { }
                var status = (int)resp.StatusCode;
                var (awsStatus, code) = status switch
                {
                    401 or 403 => (400, "AccessDeniedException"),
                    _ when status >= 500 => (500, "InternalServerError"),
                    _ => (400, "ValidationException"),
                };
                results[idx] = new PerItemResult(null, false,
                    new HardError(awsStatus, code, string.IsNullOrEmpty(body) ? (resp.ReasonPhrase ?? "Cosmos request failed.") : body));
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var item = ItemHandlers.ExtractItemFromCosmosDoc(stream);
            results[idx] = new PerItemResult(item, false, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            results[idx] = new PerItemResult(null, false,
                new HardError(500, "InternalServerError", ex.Message));
        }
        finally
        {
            sem.Release();
        }
    }

    private static Dictionary<string, JsonElement> Project(
        Dictionary<string, JsonElement> item, IReadOnlyList<string> attrs)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (int i = 0; i < attrs.Count; i++)
        {
            if (item.TryGetValue(attrs[i], out var v))
            {
                result[attrs[i]] = v;
            }
        }
        return result;
    }

    private static Dictionary<string, JsonElement> JsonElementToDict(JsonElement el)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
        {
            d[p.Name] = p.Value.Clone();
        }
        return d;
    }

    private readonly record struct WorkUnit(string Table, string Pk, string Id, JsonElement OriginalKey);

    private readonly record struct PerItemResult(
        Dictionary<string, JsonElement>? Item,
        bool Throttled,
        HardError? HardError);

    private readonly record struct HardError(int Status, string Code, string Message);
}
