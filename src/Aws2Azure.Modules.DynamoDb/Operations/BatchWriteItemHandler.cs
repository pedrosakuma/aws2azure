using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>BatchWriteItem</c> → fan-out of per-item Cosmos
/// <c>POST</c> (upsert) and <c>DELETE</c> calls with bounded
/// concurrency. AWS semantics:
///
/// <list type="bullet">
///   <item>At most 25 write requests per call across all tables;
///   rejected with <c>ValidationException</c> if exceeded.</item>
///   <item>Each entry is exactly one of <c>PutRequest</c> /
///   <c>DeleteRequest</c> (mutually exclusive).</item>
///   <item>No conditional writes (DynamoDB itself forbids this in
///   BatchWriteItem).</item>
///   <item>Per-item throttling (Cosmos <c>429</c>) is surfaced in
///   <c>UnprocessedItems</c> — the rest of the batch still succeeds.</item>
///   <item>A <c>PutRequest</c> and <c>DeleteRequest</c> on the same
///   primary key in the same call is rejected — matches DynamoDB.</item>
/// </list>
/// </summary>
internal static class BatchWriteItemHandler
{
    private const int MaxItemsPerCall = 25;
    private const int MaxParallelism = 10;

    public static async Task HandleBatchWriteItemAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        BatchWriteItemRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, BatchWriteItemJsonContext.Default.BatchWriteItemRequest);
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

        var work = new List<WriteWorkUnit>();
        // De-dup guard across the whole batch — DDB rejects multiple
        // writes targeting the same (table, key) pair in one call.
        var seenKeys = new HashSet<(string Table, string Pk, string Id)>();

        int totalCount = 0;
        foreach (var (tableName, entries) in req.RequestItems)
        {
            if (!DynamoDbNames.IsValidTableName(tableName))
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"Invalid TableName '{tableName}'.").ConfigureAwait(false);
                return;
            }
            if (entries is null || entries.Count == 0)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"RequestItems['{tableName}'] must contain at least one write request.").ConfigureAwait(false);
                return;
            }
            totalCount += entries.Count;
            if (totalCount > MaxItemsPerCall)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                    $"BatchWriteItem accepts at most {MaxItemsPerCall} write requests per call.").ConfigureAwait(false);
                return;
            }

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

            foreach (var entryRange in entries)
            {
                // Open a short-lived, pooled JsonDocument over the captured
                // envelope bytes instead of retaining one JsonElement DOM per
                // action across the whole batch. The validators/key-extraction
                // traverse this transient document; it is disposed at the end of
                // each iteration so its rented metadata DB returns to the pool.
                using var entryDoc = JsonDocument.Parse(body.AsMemory(entryRange.Start, entryRange.Length));
                var entry = entryDoc.RootElement;
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"RequestItems['{tableName}'] entries must be objects with exactly one of PutRequest/DeleteRequest.").ConfigureAwait(false);
                    return;
                }
                bool putPresent = entry.TryGetProperty("PutRequest", out var putEl);
                bool delPresent = entry.TryGetProperty("DeleteRequest", out var delEl);
                if (putPresent == delPresent)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        "Each entry must specify exactly one of PutRequest or DeleteRequest.").ConfigureAwait(false);
                    return;
                }
                bool hasPut = putPresent;
                if (hasPut && putEl.ValueKind != JsonValueKind.Object)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        "PutRequest must be an object.").ConfigureAwait(false);
                    return;
                }
                if (!hasPut && delEl.ValueKind != JsonValueKind.Object)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        "DeleteRequest must be an object.").ConfigureAwait(false);
                    return;
                }

                if (hasPut)
                {
                    if (!putEl.TryGetProperty("Item", out var itemEl) || itemEl.ValueKind != JsonValueKind.Object)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            "PutRequest.Item is required and must be an object.").ConfigureAwait(false);
                        return;
                    }
                    if (!ItemHandlers.ValidateItemShape(itemEl, out var shapeError))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", shapeError).ConfigureAwait(false);
                        return;
                    }
                    foreach (var k in meta.KeySchema)
                    {
                        if (!itemEl.TryGetProperty(k.Name, out var attr))
                        {
                            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                                $"Item in PutRequest is missing required key attribute '{k.Name}'.").ConfigureAwait(false);
                            return;
                        }
                        if (!ItemKeyFormatter.ValidateKeyAttributeType(attr, meta, k.Name, out var typeError))
                        {
                            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", typeError).ConfigureAwait(false);
                            return;
                        }
                    }
                    if (!ItemKeyFormatter.TryBuildFromItem(itemEl, meta, out var pk, out var id, out var keyError))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", keyError).ConfigureAwait(false);
                        return;
                    }
                    if (!seenKeys.Add((tableName, pk, id)))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            $"BatchWriteItem contains duplicate write targeting the same key in table '{tableName}'.").ConfigureAwait(false);
                        return;
                    }
                    // Batch units are built upfront and live across all
                    // parallel sends, so a GC-managed byte[] (leak-safe on
                    // every early-return path) is preferred over a pooled
                    // buffer here. Still removes the string / StringContent
                    // re-encode the previous BuildItemDocument path incurred.
                    int? ttlSeconds = TtlTranslation.ComputeItemTtlSeconds(
                        itemEl, meta.TimeToLive, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    var doc = ItemHandlers.BuildItemDocumentBytes(id, pk, itemEl, cosmos.CosmosBinaryRequests, ttlSeconds);
                    // Keep only the envelope's byte range for any UnprocessedItems
                    // echo (sliced from the request buffer on demand), not a
                    // retained DOM clone.
                    work.Add(new WriteWorkUnit(tableName, pk, id, WriteKind.Put, doc, entryRange));
                }
                else
                {
                    if (!delEl.TryGetProperty("Key", out var keyEl) || keyEl.ValueKind != JsonValueKind.Object)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            "DeleteRequest.Key is required and must be an object.").ConfigureAwait(false);
                        return;
                    }
                    foreach (var k in meta.KeySchema)
                    {
                        if (!keyEl.TryGetProperty(k.Name, out var attr))
                        {
                            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                                $"Key in DeleteRequest is missing required attribute '{k.Name}'.").ConfigureAwait(false);
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
                    if (!seenKeys.Add((tableName, pk, id)))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            $"BatchWriteItem contains duplicate write targeting the same key in table '{tableName}'.").ConfigureAwait(false);
                        return;
                    }
                    work.Add(new WriteWorkUnit(tableName, pk, id, WriteKind.Delete, null, entryRange));
                }
            }
        }

        var results = new WriteResult[work.Count];
        using (var sem = new SemaphoreSlim(MaxParallelism))
        {
            var tasks = new Task[work.Count];
            for (int i = 0; i < work.Count; i++)
            {
                var idx = i;
                var unit = work[i];
                tasks[i] = ExecuteOneAsync(cosmos, unit, sem, results, idx, ct);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        Dictionary<string, List<JsonElement>>? unprocessed = null;
        for (int i = 0; i < work.Count; i++)
        {
            var r = results[i];
            var unit = work[i];
            if (r.HardError is not null)
            {
                await CosmosOpsShared.WriteErrorAsync(ctx, r.HardError.Value.Status,
                    r.HardError.Value.Code, r.HardError.Value.Message).ConfigureAwait(false);
                return;
            }
            if (r.Throttled)
            {
                unprocessed ??= new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
                if (!unprocessed.TryGetValue(unit.Table, out var list))
                {
                    list = new List<JsonElement>();
                    unprocessed[unit.Table] = list;
                }
                // Rare path (Cosmos 429): re-materialize the original action
                // envelope from its captured byte range only for the throttled
                // entries that must be echoed back. Clone() detaches a standalone
                // JsonElement that outlives the transient document, so it can be
                // serialized into the response. The common (un-throttled) path
                // never allocates this DOM.
                using var echoDoc = JsonDocument.Parse(
                    body.AsMemory(unit.OriginalEntryRange.Start, unit.OriginalEntryRange.Length));
                list.Add(echoDoc.RootElement.Clone());
            }
        }

        var resp = new BatchWriteItemResponse
        {
            UnprocessedItems = unprocessed,
        };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, resp,
            BatchWriteItemJsonContext.Default.BatchWriteItemResponse).ConfigureAwait(false);
    }

    private static async Task ExecuteOneAsync(
        CosmosClient cosmos, WriteWorkUnit unit, SemaphoreSlim sem,
        WriteResult[] results, int idx, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + unit.Table;
            var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(unit.Pk);
            HttpResponseMessage resp;
            if (unit.Kind == WriteKind.Put)
            {
                var headers = new[]
                {
                    new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                    new KeyValuePair<string, string>("x-ms-documentdb-is-upsert", "true"),
                };
                resp = await cosmos.SendAsync(
                    HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
                    unit.Doc!, "application/json", headers, ct).ConfigureAwait(false);
            }
            else
            {
                var docLink = collLink + "/docs/" + unit.Id;
                var headers = new[]
                {
                    new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                };
                resp = await cosmos.SendAsync(
                    HttpMethod.Delete, "docs", docLink, "/" + docLink,
                    content: null, headers, ct).ConfigureAwait(false);
            }

            try
            {
                if (resp.StatusCode == (HttpStatusCode)429)
                {
                    results[idx] = new WriteResult(true, null);
                    return;
                }
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    if (CosmosOpsShared.Is404ContainerMissing(resp))
                    {
                        results[idx] = new WriteResult(false,
                            new HardError(400, "ResourceNotFoundException",
                                $"Table not found: {unit.Table}"));
                        return;
                    }
                    // DELETE of a missing item: success (DynamoDB delete is idempotent).
                    results[idx] = new WriteResult(false, null);
                    return;
                }
                if (resp.IsSuccessStatusCode)
                {
                    results[idx] = new WriteResult(false, null);
                    return;
                }
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
                results[idx] = new WriteResult(false,
                    new HardError(awsStatus, code, string.IsNullOrEmpty(body) ? (resp.ReasonPhrase ?? "Cosmos request failed.") : body));
            }
            finally
            {
                resp.Dispose();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            results[idx] = new WriteResult(false, new HardError(500, "InternalServerError", ex.Message));
        }
        finally
        {
            sem.Release();
        }
    }

    private enum WriteKind { Put, Delete }

    private sealed record WriteWorkUnit(
        string Table, string Pk, string Id, WriteKind Kind,
        byte[]? Doc, JsonRange OriginalEntryRange);

    private readonly record struct WriteResult(bool Throttled, HardError? HardError);

    private readonly record struct HardError(int Status, string Code, string Message);
}
