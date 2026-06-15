using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Errors;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Implements DynamoDB <c>Scan</c> as a cross-partition Cosmos SQL
/// query.
///
/// <para>Unlike <see cref="QueryHandler"/>, there is no partition-key
/// scoping: every request walks the container's logical partitions
/// (<c>x-ms-documentdb-query-enablecrosspartition: true</c>). This is
/// expensive — every Scan logs a cost warning via the standard
/// cancellation-token-aware logging pipeline.</para>
///
/// <para>Translation strategy:</para>
/// <list type="bullet">
///   <item><c>FilterExpression</c> is split by
///   <see cref="FilterPushdownVisitor"/>: the pushable fragment is
///   appended to the Cosmos WHERE clause; any residual is evaluated
///   in-process via <see cref="ConditionEvaluator"/>. <c>Count</c> always
///   reflects post-filter rows. <c>ScannedCount</c> must reflect rows
///   examined <em>before</em> the filter: when nothing is pushed it is the
///   streamed row count; when a fragment is pushed (so Cosmos pre-filters)
///   a complete unbounded pass recovers it with a server-side
///   <c>SELECT VALUE COUNT(1)</c> over the same scope minus the pushed
///   filter (see <see cref="ScannedCountQuery"/>). A pushed filter combined
///   with <c>Limit</c> is a documented divergence — see the gap doc.</item>
///   <item><c>ProjectionExpression</c> is applied in-process. Same
///   subset as Query: top-level attributes / <c>#alias</c>.</item>
///   <item><c>Limit</c> caps the pre-filter (scanned) count, just
///   like DynamoDB. Pagination round-trips the Cosmos
///   <c>x-ms-continuation</c> token in a sentinel
///   <c>__a2a_continuation</c> attribute on
///   <c>LastEvaluatedKey</c>.</item>
/// </list>
///
/// <para>Not supported in this slice: <c>IndexName</c>, parallel
/// scan (<c>Segment</c> / <c>TotalSegments</c>), legacy
/// <c>ScanFilter</c> / <c>ConditionalOperator</c> /
/// <c>AttributesToGet</c>.</para>
/// </summary>
internal static class ScanHandler
{
    private const string ContinuationSentinelAttr = "__a2a_continuation";
    private const int MaxBatchSize = 1000;

    public static async Task HandleScanAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, ILogger? logger, CancellationToken ct)
    {
        ScanRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ScanJsonContext.Default.ScanRequest);
        }
        catch (JsonException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "SerializationException",
                "Malformed JSON: " + ex.Message).ConfigureAwait(false);
            return;
        }
        if (req is null || !DynamoDbNames.IsValidTableName(req.TableName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "TableName is required and must match [a-zA-Z0-9_.-]{3,255}.").ConfigureAwait(false);
            return;
        }
        // Cost-warning telemetry: Scan is intrinsically expensive on
        // cross-partition Cosmos. Emit once per request so operators
        // can identify hot callers in dashboards / logs.
        if (logger is not null)
        {
            ScanLog.CrossPartitionScan(logger, req.TableName!);
        }
        if (!string.IsNullOrEmpty(req.IndexName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Scanning secondary indexes is not yet supported by the proxy.").ConfigureAwait(false);
            return;
        }
        if (req.Segment is not null || req.TotalSegments is not null)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Parallel scan (Segment/TotalSegments) is not yet supported by the proxy.").ConfigureAwait(false);
            return;
        }
        if (HasContent(req.ScanFilter) || !string.IsNullOrEmpty(req.ConditionalOperator)
            || HasContent(req.AttributesToGet))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Legacy ScanFilter / ConditionalOperator / AttributesToGet are not supported; use FilterExpression and ProjectionExpression.").ConfigureAwait(false);
            return;
        }
        if (req.Limit is int lim && lim <= 0)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Limit must be a positive integer.").ConfigureAwait(false);
            return;
        }
        if (!IsAllowedSelect(req.Select, out var selectError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", selectError).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<string, string>? names;
        IReadOnlyDictionary<string, JsonElement>? values;
        try
        {
            names = TryMaterialiseNames(req.ExpressionAttributeNames);
            values = TryMaterialiseValues(req.ExpressionAttributeValues);
        }
        catch (ExpressionSyntaxException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message).ConfigureAwait(false);
            return;
        }

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

        ConditionNode? filter = null;
        IReadOnlyList<string>? projection = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(req.FilterExpression))
                filter = ConditionExpressionParser.Parse(req.FilterExpression!, names, values);
            if (!string.IsNullOrWhiteSpace(req.ProjectionExpression))
                projection = ProjectionExpressionParser.Parse(req.ProjectionExpression!, names);
        }
        catch (ExpressionSyntaxException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Invalid expression (offset {ex.Position}): {ex.Message}").ConfigureAwait(false);
            return;
        }

        string? continuationIn;
        try
        {
            continuationIn = ExtractContinuation(req.ExclusiveStartKey);
        }
        catch (FormatException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "ExclusiveStartKey is malformed: " + ex.Message).ConfigureAwait(false);
            return;
        }

        await ExecuteScanAsync(ctx, req, filter, projection, continuationIn, cosmos, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteScanAsync(
        HttpContext ctx, ScanRequest req,
        ConditionNode? filter, IReadOnlyList<string>? projection, string? continuationIn,
        CosmosClient cosmos, CancellationToken ct)
    {
        bool countOnly = string.Equals(req.Select, "COUNT", StringComparison.OrdinalIgnoreCase);

        var pushdown = FilterPushdownVisitor.Translate(filter);
        // The residual replaces the parsed filter: the pushable half
        // is already enforced by Cosmos so only the leftover needs
        // client-side evaluation.
        filter = pushdown.Residual;
        var queryBody = BuildScanQueryBody(pushdown);
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var collUri = "/" + collLink + "/docs";

        // Fused fast path: with no client-side filtering (nothing pushed to
        // Cosmos AND no residual), no projection, and a full item select, each
        // Cosmos document can be pumped straight into the response Items array
        // with no JsonDocument DOM, no AttributeValue map, and no per-item model
        // re-serialization. ScannedCount == Count (no pre-filter), so the
        // server-side count fixup below is moot. Anything that needs to inspect
        // an item (filter/projection/COUNT) falls through to the materialized
        // path.
        if (!countOnly && filter is null && projection is null && pushdown.Sql is null)
        {
            await ExecuteScanFusedAsync(
                ctx, req, continuationIn, queryBody, collLink, collUri, cosmos, ct).ConfigureAwait(false);
            return;
        }

        int wantedScanned = req.Limit ?? int.MaxValue;
        var items = new List<Dictionary<string, JsonElement>>();
        int scanned = 0;
        int matched = 0;
        string? continuationOut = continuationIn;

        while (true)
        {
            int remaining = wantedScanned == int.MaxValue
                ? MaxBatchSize
                : Math.Max(1, wantedScanned - scanned);
            int pageSize = Math.Min(MaxBatchSize, remaining);

            using var content = new StringContent(queryBody, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");

            var headers = new List<KeyValuePair<string, string>>
            {
                new("x-ms-documentdb-isquery", "true"),
                new("x-ms-documentdb-query-enablecrosspartition", "true"),
                new("x-ms-max-item-count", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            if (req.ConsistentRead == true)
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-consistency-level", "Strong"));
            }
            if (!string.IsNullOrEmpty(continuationOut))
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-continuation", continuationOut));
            }

            using var resp = await cosmos.SendAsync(
                HttpMethod.Post, "docs", collLink, collUri,
                content, headers, ct).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
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

            using var cosmosBody = await CosmosOpsShared.ReadCosmosJsonBodyAsync(resp.Content, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(cosmosBody.WrittenMemory);

            if (doc.RootElement.TryGetProperty("Documents", out var docsEl)
                && docsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var docEl in docsEl.EnumerateArray())
                {
                    var itemMap = ExtractItemEnvelope(docEl);
                    if (itemMap is null) continue;
                    scanned++;

                    if (filter is not null)
                    {
                        bool keep;
                        try { keep = ConditionEvaluator.Evaluate(filter, itemMap); }
                        catch (ConditionEvaluationException ex)
                        {
                            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message).ConfigureAwait(false);
                            return;
                        }
                        if (!keep) continue;
                    }
                    matched++;
                    if (!countOnly)
                    {
                        items.Add(projection is null ? itemMap : Project(itemMap, projection));
                    }
                }
            }

            continuationOut = null;
            if (resp.Headers.TryGetValues("x-ms-continuation", out var ctValues))
            {
                foreach (var v in ctValues) { continuationOut = v; break; }
            }

            if (scanned >= wantedScanned) break;
            if (string.IsNullOrEmpty(continuationOut)) break;
            // When the filter was pushed (fully or partially) into the
            // Cosmos SQL, our `scanned` counter is post-prefilter and
            // doesn't model DDB's "evaluated items" cap. Topping up
            // across continuation pages here silently moves the page
            // boundary forward and yields a different LastEvaluatedKey
            // than DDB would. Preserve the Cosmos page boundary: stop
            // after the first non-empty page that produced a
            // continuation. Empty pages still iterate so we don't
            // return a zero-row page when Cosmos has more rows
            // waiting.
            if (pushdown.Sql is not null && matched > 0) break;
        }

        // With the filter pushed into Cosmos, `scanned` counts only matched
        // documents — not DynamoDB's pre-filter ScannedCount. For a single
        // complete unbounded pass (no Limit, no incoming ExclusiveStartKey, no
        // outgoing continuation), recover the faithful count with a cheap
        // server-side aggregate over the same scope minus the pushed filter.
        // The Limit/paginated case stays a documented divergence: the aggregate
        // spans the whole scope, so on a resumed (continuationIn) or partial
        // (continuationOut) page it would not match DynamoDB's per-page count.
        if (pushdown.Sql is not null
            && wantedScanned == int.MaxValue
            && string.IsNullOrEmpty(continuationIn)
            && string.IsNullOrEmpty(continuationOut))
        {
            var faithful = await ScannedCountQuery.CountAsync(
                cosmos, collLink, collUri, BuildScanCountSql(),
                System.Array.Empty<CosmosSqlParameter>(),
                partitionKeyHeader: null, strong: req.ConsistentRead == true, ct).ConfigureAwait(false);
            if (faithful is int fc && fc >= matched)
            {
                scanned = fc;
            }
        }

        var response = new ScanResponse
        {
            Items = countOnly ? null : items,
            Count = matched,
            ScannedCount = scanned,
            LastEvaluatedKey = string.IsNullOrEmpty(continuationOut) ? null : BuildContinuationKey(continuationOut),
        };
        DynamoDbMetrics.RecordReadTransformPath(DynamoDbMetrics.OpScan, DynamoDbMetrics.PathMaterialized);
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, response, ScanJsonContext.Default.ScanResponse)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a no-filter / no-projection Scan straight to the wire: each
    /// Cosmos page's <c>Documents</c> are transformed into the response
    /// <c>Items</c> array via <see cref="Persistence.InferredAttributeStorage.WriteTransformedDocuments"/>
    /// with no JsonDocument DOM, no AttributeValue map, and no per-item model
    /// re-serialization. The whole response is built into a pooled scratch
    /// buffer and only handed to the socket once, so an error on any page (or a
    /// malformed document) still surfaces a clean error response — nothing has
    /// reached <see cref="HttpResponse.Body"/> until the final write. Output is
    /// byte-identical to the materialized
    /// <c>ExtractItem → ScanResponse → SerializeAsync</c> path (pinned by the
    /// golden corpus).
    /// </summary>
    private static async Task ExecuteScanFusedAsync(
        HttpContext ctx, ScanRequest req, string? continuationIn,
        string queryBody, string collLink, string collUri,
        CosmosClient cosmos, CancellationToken ct)
    {
        int wantedScanned = req.Limit ?? int.MaxValue;
        int count = 0;
        string? continuationOut = continuationIn;

        using var outBuf = new PooledByteBufferWriter(8192);
        using var writer = new Utf8JsonWriter(outBuf);
        writer.WriteStartObject();
        writer.WritePropertyName(ItemsNameU8);
        writer.WriteStartArray();

        while (true)
        {
            int remaining = wantedScanned == int.MaxValue
                ? MaxBatchSize
                : Math.Max(1, wantedScanned - count);
            int pageSize = Math.Min(MaxBatchSize, remaining);

            using var content = new StringContent(queryBody, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");

            var headers = new List<KeyValuePair<string, string>>
            {
                new("x-ms-documentdb-isquery", "true"),
                new("x-ms-documentdb-query-enablecrosspartition", "true"),
                new("x-ms-max-item-count", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            if (req.ConsistentRead == true)
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-consistency-level", "Strong"));
            }
            if (!string.IsNullOrEmpty(continuationOut))
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-continuation", continuationOut));
            }

            using var resp = await cosmos.SendAsync(
                HttpMethod.Post, "docs", collLink, collUri,
                content, headers, ct).ConfigureAwait(false);

            // Nothing has been written to ctx.Response yet (the envelope lives
            // in outBuf), so an error here can still emit a clean response.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
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

            using var page = await CosmosOpsShared.ReadCosmosJsonBodyAsync(resp.Content, ct).ConfigureAwait(false);
            var reader = new Internal.Utf8JsonTokenReader(page.WrittenMemory.Span);
            count += Persistence.InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader);

            continuationOut = null;
            if (resp.Headers.TryGetValues("x-ms-continuation", out var ctValues))
            {
                foreach (var v in ctValues) { continuationOut = v; break; }
            }

            if (count >= wantedScanned) break;
            if (string.IsNullOrEmpty(continuationOut)) break;
        }

        writer.WriteEndArray();
        writer.WriteNumber(CountNameU8, count);
        writer.WriteNumber(ScannedCountNameU8, count);
        if (!string.IsNullOrEmpty(continuationOut))
        {
            // LastEvaluatedKey: {"__a2a_continuation":{"S":"<base64(continuation)>"}}
            writer.WritePropertyName(LastEvaluatedKeyNameU8);
            writer.WriteStartObject();
            writer.WritePropertyName(ContinuationSentinelAttr);
            writer.WriteStartObject();
            writer.WriteString(TypedStringNameU8,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(continuationOut)));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();

        DynamoDbMetrics.RecordReadTransformPath(DynamoDbMetrics.OpScan, DynamoDbMetrics.PathFused);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        await ctx.Response.BodyWriter.WriteAsync(outBuf.WrittenMemory, ct).ConfigureAwait(false);
    }

    private static ReadOnlySpan<byte> ItemsNameU8 => "Items"u8;
    private static ReadOnlySpan<byte> CountNameU8 => "Count"u8;
    private static ReadOnlySpan<byte> ScannedCountNameU8 => "ScannedCount"u8;
    private static ReadOnlySpan<byte> LastEvaluatedKeyNameU8 => "LastEvaluatedKey"u8;
    private static ReadOnlySpan<byte> TypedStringNameU8 => "S"u8;

    // ----- helpers (mirror QueryHandler) ------------------------------

    /// <summary>
    /// Scan's Cosmos SQL starts at <c>SELECT * FROM c WHERE c._a2a =
    /// 'item'</c> (skip the table-metadata sidecar) and appends any
    /// fragment pushed down by <see cref="FilterPushdownVisitor"/>.
    /// Everything else is evaluated in-process.
    /// </summary>
    internal static string BuildScanQueryBody(FilterPushdownResult pushdown)
    {
        var sql = pushdown.Sql is { } f
            ? "SELECT * FROM c WHERE c._a2a = 'item' AND " + f
            : "SELECT * FROM c WHERE c._a2a = 'item'";
        return CosmosQueryBody.Build(sql, pushdown.Parameters);
    }

    /// <summary>
    /// The aggregate counterpart of <see cref="BuildScanQueryBody"/> used to
    /// recover a faithful <c>ScannedCount</c>: the base scan predicate with
    /// no pushed filter, projected as a server-side count.
    /// </summary>
    internal static string BuildScanCountSql()
        => "SELECT VALUE COUNT(1) FROM c WHERE c._a2a = 'item'";

    private static Dictionary<string, JsonElement>? ExtractItemEnvelope(JsonElement docEl)
        => Persistence.InferredAttributeStorage.ExtractItem(docEl);

    private static Dictionary<string, JsonElement> Project(
        Dictionary<string, JsonElement> item, IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            if (item.TryGetValue(p, out var v)) result[p] = v;
        }
        return result;
    }

    private static string? ExtractContinuation(JsonElement? exclusiveStartKey)
    {
        if (exclusiveStartKey is not { } esk) return null;
        if (esk.ValueKind != JsonValueKind.Object) return null;
        if (!esk.TryGetProperty(ContinuationSentinelAttr, out var sentinel)) return null;
        if (sentinel.ValueKind != JsonValueKind.Object) return null;
        if (!sentinel.TryGetProperty("S", out var sEl) || sEl.ValueKind != JsonValueKind.String)
            throw new FormatException("continuation attribute must be a typed string.");
        var b64 = sEl.GetString();
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            throw new FormatException("continuation attribute is not valid base64.");
        }
    }

    private static Dictionary<string, JsonElement> BuildContinuationKey(string cosmosContinuation)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cosmosContinuation));
        var json = $"{{\"S\":\"{b64}\"}}";
        using var doc = JsonDocument.Parse(json);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [ContinuationSentinelAttr] = doc.RootElement.Clone(),
        };
    }

    private static bool IsAllowedSelect(string? select, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(select)) return true;
        if (string.Equals(select, "ALL_ATTRIBUTES", StringComparison.Ordinal)
            || string.Equals(select, "COUNT", StringComparison.Ordinal)
            || string.Equals(select, "SPECIFIC_ATTRIBUTES", StringComparison.Ordinal))
            return true;
        if (string.Equals(select, "ALL_PROJECTED_ATTRIBUTES", StringComparison.Ordinal))
        {
            error = "Select=ALL_PROJECTED_ATTRIBUTES requires IndexName, which is not supported in this slice.";
            return false;
        }
        error = $"Select '{select}' is not a recognised value.";
        return false;
    }

    private static IReadOnlyDictionary<string, string>? TryMaterialiseNames(JsonElement? src)
    {
        if (src is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                throw new ExpressionSyntaxException(0, $"ExpressionAttributeNames['{p.Name}'] must be a string.");
            d[p.Name] = p.Value.GetString()!;
        }
        return d;
    }

    private static IReadOnlyDictionary<string, JsonElement>? TryMaterialiseValues(JsonElement? src)
    {
        if (src is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            d[p.Name] = p.Value.Clone();
        }
        return d;
    }

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

/// <summary>
/// Source-generated logging helpers for <see cref="ScanHandler"/>.
/// Kept as a separate static partial class so the
/// <c>LoggerMessage</c> generator (AOT-safe, zero reflection) can
/// emit the backing implementation.
/// </summary>
internal static partial class ScanLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Cross-partition DynamoDB Scan against {Table} — Scan walks every partition and is expensive on Cosmos; prefer Query when a partition key is known.")]
    public static partial void CrossPartitionScan(ILogger logger, string table);
}
