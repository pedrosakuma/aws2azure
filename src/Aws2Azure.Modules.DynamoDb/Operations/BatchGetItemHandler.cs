using System;
using System.Collections.Generic;
using System.Globalization;
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
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>BatchGetItem</c> → Cosmos reads, batched per partition.
/// Keys are grouped by their Cosmos partition key; a group of two or
/// more keys is served by a single <c>SELECT * FROM c WHERE c.id IN
/// (...)</c> query scoped to that partition (one round-trip instead of
/// N point reads — see issue #185), while a singleton group keeps the
/// cheap <c>GET /docs/{id}</c> point read. Groups run concurrently
/// under a bounded semaphore so genuinely multi-partition requests stay
/// capped. Mirrors the AWS semantics:
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
        var seenKeys = new HashSet<(string Table, string Pk, string Id)>();

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
                if (!seenKeys.Add((tableName, pk, id)))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"BatchGetItem contains duplicate key in table '{tableName}'.").ConfigureAwait(false);
                    return;
                }
                var cloned = keyEl.Clone();
                work.Add(new WorkUnit(tableName, pk, id, cloned));
            }
        }

        // Group the work units by Cosmos partition key. Keys that share
        // a partition are served by a single IN-list query (one
        // round-trip); singletons fall back to a point read. Genuinely
        // multi-partition batches still fan out, bounded by the
        // semaphore below.
        var results = new PerItemResult[work.Count];
        var groups = new Dictionary<(string Table, string Pk), KeyGroup>();
        for (int i = 0; i < work.Count; i++)
        {
            var unit = work[i];
            var gk = (unit.Table, unit.Pk);
            if (!groups.TryGetValue(gk, out var g))
            {
                g = new KeyGroup(unit.Table, unit.Pk, tableConsistent[unit.Table],
                    noProjection: tableProjection[unit.Table] is null);
                groups[gk] = g;
            }
            g.Add(unit.Id, i);
        }

        // Fan-out: parallel Cosmos calls with a semaphore so we never
        // queue more than MaxParallelism concurrent calls regardless of
        // the per-batch cap.
        using (var sem = new SemaphoreSlim(MaxParallelism))
        {
            var groupList = new List<KeyGroup>(groups.Values);
            var tasks = new Task[groupList.Count];
            for (int i = 0; i < groupList.Count; i++)
            {
                tasks[i] = ExecuteGroupAsync(cosmos, groupList[i], sem, results, ct);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // Stitch results into Responses + UnprocessedKeys. A non-throttle
        // Cosmos error on any item fails the whole batch (matches
        // DynamoDB: only throttling lands in UnprocessedKeys).
        var responses = new Dictionary<string, List<BatchGetResponseItem>>(StringComparer.Ordinal);
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
                list = new List<BatchGetResponseItem>();
                responses[unit.Table] = list;
            }
            var item = r.Item.Value;
            var projection = tableProjection[unit.Table];
            if (projection is not null)
            {
                // Projection tables always travel the map path (point reads and
                // the map sink), so Item.Map is populated here.
                item = new BatchGetResponseItem(Project(item.Map!, projection));
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

    private static async Task ExecuteGroupAsync(
        CosmosClient cosmos, KeyGroup group,
        SemaphoreSlim sem, PerItemResult[] results, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (group.Count == 1)
            {
                await ExecutePointReadAsync(
                    cosmos, group, group.Ids[0], group.Indices[0], results, ct).ConfigureAwait(false);
            }
            else
            {
                await ExecuteGroupQueryAsync(cosmos, group, results, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SetGroupResult(group, results,
                new PerItemResult(null, false, new HardError(500, "InternalServerError", ex.Message)));
        }
        finally
        {
            sem.Release();
        }
    }

    private static async Task ExecutePointReadAsync(
        CosmosClient cosmos, KeyGroup group, string id, int idx,
        PerItemResult[] results, CancellationToken ct)
    {
        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + group.Table + "/docs/" + id;
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(group.Pk);
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-ms-documentdb-partitionkey", pkHeader),
        };
        if (group.Consistent)
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
                        $"Table not found: {group.Table}"));
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
            results[idx] = await BuildHardErrorAsync(resp, ct).ConfigureAwait(false);
            return;
        }

        var item = await CosmosOpsShared.ReadAndExtractItemAsync(
            resp.Content, DynamoDbMetrics.OpBatchGet, ct).ConfigureAwait(false);
        results[idx] = new PerItemResult(
            item is null ? null : new BatchGetResponseItem(item), false, null);
    }

    /// <summary>
    /// Serves every key of a single-partition group with one Cosmos
    /// query (<c>SELECT * FROM c WHERE c._a2a = 'item' AND c.id IN
    /// (...)</c>). Keys whose document is absent are simply left as the
    /// default (missing) result; a Cosmos <c>429</c> throttles the whole
    /// group into <c>UnprocessedKeys</c>; any other failure surfaces as
    /// a hard error for every key in the group.
    /// </summary>
    private static async Task ExecuteGroupQueryAsync(
        CosmosClient cosmos, KeyGroup group, PerItemResult[] results, CancellationToken ct)
    {
        var sb = new StringBuilder("SELECT * FROM c WHERE c.")
            .Append(InferredAttributeStorage.DiscriminatorProperty)
            .Append(" = '")
            .Append(InferredAttributeStorage.DiscriminatorValueItem)
            .Append("' AND c.id IN (");
        var parameters = new List<CosmosSqlParameter>(group.Count);
        for (int i = 0; i < group.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var name = "@id" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(name);
            parameters.Add(new CosmosSqlParameter(name, group.Ids[i]));
        }
        sb.Append(')');
        using var queryBody = CosmosQueryBody.Build(sb.ToString(), parameters);

        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + group.Table;
        var collUri = "/" + collLink + "/docs";
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(group.Pk);
        var pageSize = group.Count.ToString(CultureInfo.InvariantCulture);

        string? continuation = null;
        while (true)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new("x-ms-documentdb-partitionkey", pkHeader),
                new("x-ms-documentdb-isquery", "true"),
                new("x-ms-max-item-count", pageSize),
            };
            if (group.Consistent)
            {
                headers.Add(new("x-ms-consistency-level", "Strong"));
            }
            if (!string.IsNullOrEmpty(continuation))
            {
                headers.Add(new("x-ms-continuation", continuation));
            }

            using var resp = await cosmos.SendAsync(
                HttpMethod.Post, "docs", collLink, collUri,
                queryBody.WrittenMemory, "application/query+json", headers, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (CosmosOpsShared.Is404ContainerMissing(resp))
                {
                    SetGroupResult(group, results,
                        new PerItemResult(null, false,
                            new HardError(400, "ResourceNotFoundException",
                                $"Table not found: {group.Table}")));
                    return;
                }
                // No matching documents — leave every key as the default
                // (missing) result, matching DynamoDB's omit semantics.
                return;
            }
            if (resp.StatusCode == (HttpStatusCode)429)
            {
                // Throttle only the keys still unresolved on this page —
                // items already fetched on earlier continuation pages stay
                // in Responses, matching the per-point-read semantics where
                // a throttle affects only the throttled subset.
                MarkUnresolvedThrottled(group, results);
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                SetGroupResult(group, results, await BuildHardErrorAsync(resp, ct).ConfigureAwait(false));
                return;
            }

            using var cosmosBody = await CosmosOpsShared.ReadCosmosJsonBodyAsync(resp.Content, ct).ConfigureAwait(false);
            var reader = new Utf8JsonTokenReader(cosmosBody.WrittenMemory.Span);
            if (group.NoProjection)
            {
                // No ProjectionExpression: keep each document's transformed bytes
                // and splice them verbatim into Responses, skipping the per-doc
                // map materialization (issue #443).
                InferredAttributeStorage.ExtractItemsFusedWithIdBytes(
                    ref reader, new GroupCorrelationBytesSink(group, results));
            }
            else
            {
                InferredAttributeStorage.ExtractItemsFusedWithId(
                    ref reader, new GroupCorrelationSink(group, results));
            }

            continuation = null;
            if (resp.Headers.TryGetValues("x-ms-continuation", out var ctValues))
            {
                foreach (var v in ctValues) { continuation = v; break; }
            }
            if (string.IsNullOrEmpty(continuation)) break;
        }
    }

    private static async Task<PerItemResult> BuildHardErrorAsync(
        HttpResponseMessage resp, CancellationToken ct)
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
        return new PerItemResult(null, false,
            new HardError(awsStatus, code,
                string.IsNullOrEmpty(body) ? (resp.ReasonPhrase ?? "Cosmos request failed.") : body));
    }

    private static void SetGroupResult(KeyGroup group, PerItemResult[] results, PerItemResult value)
    {
        for (int i = 0; i < group.Indices.Count; i++)
        {
            results[group.Indices[i]] = value;
        }
    }

    /// <summary>
    /// Marks every group key that has not yet been resolved (no item
    /// fetched, not already throttled, no hard error) as throttled. Used
    /// when a 429 lands part-way through draining a multi-page query so
    /// items already returned on earlier pages survive into Responses.
    /// </summary>
    private static void MarkUnresolvedThrottled(KeyGroup group, PerItemResult[] results)
    {
        for (int i = 0; i < group.Indices.Count; i++)
        {
            var idx = group.Indices[i];
            var r = results[idx];
            if (r.Item is null && !r.Throttled && r.HardError is null)
            {
                results[idx] = new PerItemResult(null, true, null);
            }
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

    /// <summary>
    /// A set of keys that share a Cosmos partition key within one table,
    /// served by a single query (or a point read when it holds one key).
    /// </summary>
    private sealed class KeyGroup
    {
        public KeyGroup(string table, string pk, bool consistent, bool noProjection)
        {
            Table = table;
            Pk = pk;
            Consistent = consistent;
            NoProjection = noProjection;
        }

        public string Table { get; }
        public string Pk { get; }
        public bool Consistent { get; }

        /// <summary>
        /// True when the table carries no <c>ProjectionExpression</c>, so the
        /// grouped query can keep each document's transformed bytes and splice
        /// them straight into <c>Responses</c> (issue #443) instead of
        /// materializing an AttributeValue map. Projection tables stay on the map
        /// path because <see cref="Project"/> needs structured access.
        /// </summary>
        public bool NoProjection { get; }
        public List<string> Ids { get; } = new();
        public List<int> Indices { get; } = new();
        private readonly Dictionary<string, int> _idToIndex = new(StringComparer.Ordinal);

        public int Count => Ids.Count;

        public void Add(string id, int index)
        {
            Ids.Add(id);
            Indices.Add(index);
            _idToIndex[id] = index;
        }

        public bool TryGetIndex(string id, out int index) => _idToIndex.TryGetValue(id, out index);
    }

    private readonly record struct PerItemResult(
        BatchGetResponseItem? Item,
        bool Throttled,
        HardError? HardError);

    private readonly record struct HardError(int Status, string Code, string Message);

    /// <summary>
    /// Streaming sink for <see cref="ExecuteGroupQueryAsync"/>: correlates each
    /// document the page walk surfaces back to its request-key index via the
    /// Cosmos <c>id</c> and stores the extracted item. A <c>struct</c> so
    /// <see cref="InferredAttributeStorage.ExtractItemsFusedWithId{TReader,TSink}"/>
    /// devirtualizes the callback with no allocation. Documents whose <c>id</c>
    /// is absent or unknown to the group are ignored (matching the legacy
    /// per-document correlation: missing keys stay at their default result).
    /// </summary>
    private readonly struct GroupCorrelationSink : IFusedItemWithIdSink
    {
        private readonly KeyGroup _group;
        private readonly PerItemResult[] _results;

        public GroupCorrelationSink(KeyGroup group, PerItemResult[] results)
        {
            _group = group;
            _results = results;
        }

        public void Accept(string? id, Dictionary<string, JsonElement> map)
        {
            if (id is null) return;
            if (!_group.TryGetIndex(id, out var idx)) return;
            _results[idx] = new PerItemResult(new BatchGetResponseItem(map), false, null);
        }
    }

    /// <summary>
    /// Byte-splicing twin of <see cref="GroupCorrelationSink"/> for no-projection
    /// tables (issue #443): correlates each document by its Cosmos <c>id</c> and
    /// retains the transformed item <i>bytes</i> instead of a materialized map,
    /// copying the shared scratch span into an owned array so it survives past the
    /// page walk and the pooled Cosmos body. A <c>struct</c> so the page walk
    /// monomorphizes and devirtualizes the callback with no delegate allocation.
    /// </summary>
    private readonly struct GroupCorrelationBytesSink : IFusedItemBytesWithIdSink
    {
        private readonly KeyGroup _group;
        private readonly PerItemResult[] _results;

        public GroupCorrelationBytesSink(KeyGroup group, PerItemResult[] results)
        {
            _group = group;
            _results = results;
        }

        public void Accept(string? id, ReadOnlySpan<byte> itemBytes)
        {
            if (id is null) return;
            if (!_group.TryGetIndex(id, out var idx)) return;
            _results[idx] = new PerItemResult(new BatchGetResponseItem(itemBytes.ToArray()), false, null);
        }
    }
}
