using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
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
/// <para>Local Secondary Index scans are supported via
/// <c>IndexName</c>: the scan stays cross-partition but is restricted to the
/// index's member items with an <c>IS_DEFINED(c.&lt;lsiSort&gt;)</c> guard, and
/// the index projection (ALL / KEYS_ONLY / INCLUDE) is resolved in-process.
/// Global Secondary Index scans are rejected.</para>
///
/// <para>Not supported in this slice: GSI <c>IndexName</c>, parallel
/// scan (<c>Segment</c> / <c>TotalSegments</c>), legacy
/// <c>ScanFilter</c> / <c>ConditionalOperator</c> /
/// <c>AttributesToGet</c>.</para>
/// </summary>
internal static class ScanHandler
{
    private const string ContinuationSentinelAttr = "__a2a_continuation";
    private const int MaxBatchSize = 1000;

    public static async Task HandleScanAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, ILogger? logger, bool enableGsi, CancellationToken ct)
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
            DynamoDbLog.CrossPartitionScan(logger, req.TableName!);
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
        var meta = metaResult.Metadata!;

        // Resolve IndexName against the table's secondary index schemas. Local
        // Secondary Index scans are always supported; Global Secondary Index
        // scans are gated behind the opt-in EnableGlobalSecondaryIndexQueries
        // flag (cross-partition; off by default). Unknown indices are rejected
        // loudly. An index scan is still cross-partition (Scan never scopes to a
        // partition) but is restricted to the index's member items — those that
        // define the LSI sort attribute, or the GSI hash (plus sort when
        // composite) attribute(s) — and honours the index projection.
        TableIndexDefinition? lsi = null;
        TableIndexDefinition? gsi = null;
        string? lsiSortName = null;
        string? gsiHashName = null;
        string? gsiSortName = null;
        if (!string.IsNullOrEmpty(req.IndexName))
        {
            var outcome = SecondaryIndexResolver.ResolveIndex(meta, req.IndexName!, out var index);
            switch (outcome)
            {
                case SecondaryIndexResolver.IndexResolution.Lsi:
                    lsi = index;
                    if (!SecondaryIndexResolver.TryGetLsiSortKey(meta, lsi!, out var skName, out _, out var lsiError))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", lsiError).ConfigureAwait(false);
                        return;
                    }
                    lsiSortName = skName;
                    break;
                case SecondaryIndexResolver.IndexResolution.Gsi:
                    if (!enableGsi)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            "Scanning global secondary indexes is not yet supported by the proxy.").ConfigureAwait(false);
                        return;
                    }
                    // A GSI scan is eventually consistent in DynamoDB; strongly
                    // consistent reads are not permitted on a GSI.
                    if (req.ConsistentRead == true)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            "Consistent reads are not supported on global secondary indexes.").ConfigureAwait(false);
                        return;
                    }
                    gsi = index;
                    if (!SecondaryIndexResolver.TryGetGsiKeys(meta, gsi!,
                            out var ghName, out _, out var gsName, out _, out var gsiError))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", gsiError).ConfigureAwait(false);
                        return;
                    }
                    gsiHashName = ghName;
                    gsiSortName = gsName;
                    break;
                case SecondaryIndexResolver.IndexResolution.NotFound:
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"The table does not have the specified index: {req.IndexName}").ConfigureAwait(false);
                    return;
            }
        }
        bool isLsiScan = lsi is not null;
        bool isGsiScan = gsi is not null;
        bool isIndexScan = isLsiScan || isGsiScan;
        // A GSI is a projected view: when its projection is not ALL, only the
        // index's projected attributes are physically available in DynamoDB.
        // The proxy reads the full base-container document, so it must enforce
        // the projected set itself to avoid leaking non-projected attributes.
        bool gsiProjectionAll = isGsiScan
            && string.Equals(gsi!.ProjectionType, "ALL", StringComparison.OrdinalIgnoreCase);

        if (!IsAllowedSelect(req.Select, isIndexScan, out var selectError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", selectError).ConfigureAwait(false);
            return;
        }

        // Select=SPECIFIC_ATTRIBUTES requires a ProjectionExpression (legacy
        // AttributesToGet is already rejected above). Without one DynamoDB
        // rejects the request; the proxy must too, otherwise the scan would
        // fall through with no projection and return every attribute — which on
        // a non-ALL GSI would leak attributes outside the index's projected set.
        if (string.Equals(req.Select, "SPECIFIC_ATTRIBUTES", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(req.ProjectionExpression))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Cannot use Select Type SPECIFIC_ATTRIBUTES without providing the ProjectionExpression parameter.").ConfigureAwait(false);
            return;
        }

        // Select=ALL_ATTRIBUTES on a GSI is only legal when the index projects
        // ALL — a GSI cannot fetch non-projected attributes from the base table
        // (unlike an LSI). Reject it, matching DynamoDB.
        if (isGsiScan && !gsiProjectionAll
            && string.Equals(req.Select, "ALL_ATTRIBUTES", StringComparison.Ordinal))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Select=ALL_ATTRIBUTES is not supported on global secondary index '{req.IndexName}' whose projection type is not ALL.").ConfigureAwait(false);
            return;
        }

        ConditionNode? filter = null;
        Projection? projection = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(req.FilterExpression))
                filter = ConditionExpressionParser.Parse(req.FilterExpression!, names, values);
            if (!string.IsNullOrWhiteSpace(req.ProjectionExpression))
            {
                projection = ProjectionExpressionParser.Parse(req.ProjectionExpression!, names);
            }
            else if (isIndexScan
                && (string.IsNullOrEmpty(req.Select)
                    || string.Equals(req.Select, "ALL_PROJECTED_ATTRIBUTES", StringComparison.Ordinal)))
            {
                // For an index scan DynamoDB defaults Select to
                // ALL_PROJECTED_ATTRIBUTES when neither Select nor a
                // ProjectionExpression is supplied. ALL_PROJECTED_ATTRIBUTES
                // resolves against the index projection: ALL → all attributes (no
                // in-process projection); KEYS_ONLY → base keys + the index's own
                // key attributes; INCLUDE → those keys plus the index's
                // NonKeyAttributes. An explicit ProjectionExpression (handled
                // above) always takes precedence; an explicit ALL_ATTRIBUTES /
                // SPECIFIC_ATTRIBUTES / COUNT Select skips this branch.
                projection = isGsiScan
                    ? Wrap(SecondaryIndexResolver.ResolveIndexProjection(meta, gsi!, gsiHashName!, gsiSortName))
                    : Wrap(SecondaryIndexResolver.ResolveIndexProjection(meta, lsi!, lsiSortName!));
            }
        }
        catch (ExpressionSyntaxException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Invalid expression (offset {ex.Position}): {ex.Message}").ConfigureAwait(false);
            return;
        }

        // The GSI only projects a subset of attributes when its projection type
        // is not ALL; a ProjectionExpression cannot reference attributes outside
        // that set (DynamoDB cannot fetch them from the base table for a GSI).
        // Reject any non-projected path.
        if (isGsiScan && !gsiProjectionAll && !string.IsNullOrWhiteSpace(req.ProjectionExpression))
        {
            var allowed = SecondaryIndexResolver.ResolveIndexProjection(
                meta, gsi!, gsiHashName!, gsiSortName)!;
            foreach (var path in projection!.RootNames)
            {
                bool projected = false;
                foreach (var a in allowed)
                {
                    if (string.Equals(a, path, StringComparison.Ordinal)) { projected = true; break; }
                }
                if (!projected)
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"ProjectionExpression references attribute '{path}' that is not projected into index '{req.IndexName}'.").ConfigureAwait(false);
                    return;
                }
            }
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

        // Index membership guards: restrict the cross-partition scan to the
        // index's member items via IS_DEFINED. For an LSI, the sort attribute;
        // for a GSI, the hash attribute (plus the sort attribute when the GSI is
        // composite — both must be defined for an item to be an index member).
        IReadOnlyList<string>? membershipAttrs = null;
        if (isLsiScan)
        {
            membershipAttrs = new[] { lsiSortName! };
        }
        else if (isGsiScan)
        {
            membershipAttrs = gsiSortName is null
                ? new[] { gsiHashName! }
                : new[] { gsiHashName!, gsiSortName };
        }

        await ExecuteScanAsync(ctx, req, filter, projection, membershipAttrs, continuationIn, cosmos, ct).ConfigureAwait(false);
    }

    private static async Task ExecuteScanAsync(
        HttpContext ctx, ScanRequest req,
        ConditionNode? filter, Projection? projection, IReadOnlyList<string>? membershipAttrs, string? continuationIn,
        CosmosClient cosmos, CancellationToken ct)
    {
        bool countOnly = string.Equals(req.Select, "COUNT", StringComparison.OrdinalIgnoreCase);

        var pushdown = FilterPushdownVisitor.Translate(filter);
        // The residual replaces the parsed filter: the pushable half
        // is already enforced by Cosmos so only the leftover needs
        // client-side evaluation.
        filter = pushdown.Residual;
        using var queryBody = BuildScanQueryBody(pushdown, membershipAttrs);
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
                ctx, req, continuationIn, queryBody.WrittenMemory, collLink, collUri, cosmos, ct).ConfigureAwait(false);
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
                queryBody.WrittenMemory, "application/query+json", headers, ct).ConfigureAwait(false);

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

            var pageItems = await CosmosOpsShared.ReadAndExtractItemsAsync(
                resp.Content, DynamoDbMetrics.OpScan, ct).ConfigureAwait(false);

            foreach (var itemMap in pageItems)
            {
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
                    items.Add(projection is null ? itemMap : projection.Apply(itemMap));
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
                cosmos, collLink, collUri, BuildScanCountSql(membershipAttrs),
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
        ReadOnlyMemory<byte> queryBody, string collLink, string collUri,
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
                queryBody, "application/query+json", headers, ct).ConfigureAwait(false);

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

            using var page = await CosmosOpsShared.ReadCosmosRawBodyAsync(resp.Content, ct).ConfigureAwait(false);
            // Pure binary-direct streaming transform: stream Cosmos documents straight
            // into the DDB envelope. No decode-to-text step and no fallback — a reader
            // decline propagates as an error, exactly like a malformed text page would.
            if (Internal.CosmosBinaryDecoder.IsBinary(page.WrittenMemory.Span))
            {
                DynamoDbMetrics.RecordReadDecodePath(DynamoDbMetrics.OpScan, DynamoDbMetrics.DecodeBinary);
                var reader = new Internal.CosmosBinaryReader(page.WrittenMemory.Span);
                try
                {
                    count += Persistence.InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader);
                }
                finally
                {
                    reader.Dispose();
                }
            }
            else
            {
                DynamoDbMetrics.RecordReadDecodePath(DynamoDbMetrics.OpScan, DynamoDbMetrics.DecodeText);
                var reader = new Internal.Utf8JsonTokenReader(page.WrittenMemory.Span);
                count += Persistence.InferredAttributeStorage.WriteTransformedDocuments(writer, ref reader);
            }

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
    /// Everything else is evaluated in-process. For a secondary-index scan,
    /// an explicit <c>IS_DEFINED(c.&lt;attr&gt;)</c> guard per index key
    /// attribute restricts the scan to the index's member items
    /// (sparse-index / GSI-membership semantics) — for an LSI the single sort
    /// attribute, for a GSI the hash attribute (plus the sort attribute when
    /// the GSI is composite). The streamed row count is then already the
    /// index's ScannedCount.
    /// </summary>
    internal static PooledByteBufferWriter BuildScanQueryBody(
        FilterPushdownResult pushdown, IReadOnlyList<string>? membershipAttrs = null)
    {
        var sb = new StringBuilder("SELECT * FROM c WHERE c._a2a = 'item'");
        AppendMembershipGuards(sb, membershipAttrs);
        if (pushdown.Sql is { } f)
        {
            sb.Append(" AND ").Append(f);
        }
        return CosmosQueryBody.Build(sb.ToString(), pushdown.Parameters);
    }

    /// <summary>
    /// The aggregate counterpart of <see cref="BuildScanQueryBody"/> used to
    /// recover a faithful <c>ScannedCount</c>: the base scan predicate (plus
    /// the index membership guards when scanning an index) with no pushed
    /// filter, projected as a server-side count.
    /// </summary>
    internal static string BuildScanCountSql(IReadOnlyList<string>? membershipAttrs = null)
    {
        var sb = new StringBuilder("SELECT VALUE COUNT(1) FROM c WHERE c._a2a = 'item'");
        AppendMembershipGuards(sb, membershipAttrs);
        return sb.ToString();
    }

    private static void AppendMembershipGuards(StringBuilder sb, IReadOnlyList<string>? membershipAttrs)
    {
        if (membershipAttrs is null) return;
        foreach (var attr in membershipAttrs)
        {
            var path = CosmosPathTranslator.Translate(
                new DocumentPath(new[] { new AttributePathSegment(attr) }));
            sb.Append(" AND IS_DEFINED(").Append(path).Append(')');
        }
    }

    private static Projection? Wrap(IReadOnlyList<string>? topLevelNames)
        => topLevelNames is null ? null : Projection.FromTopLevelNames(topLevelNames);

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

    private static bool IsAllowedSelect(string? select, bool isLsi, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(select)) return true;
        if (string.Equals(select, "ALL_ATTRIBUTES", StringComparison.Ordinal)
            || string.Equals(select, "COUNT", StringComparison.Ordinal)
            || string.Equals(select, "SPECIFIC_ATTRIBUTES", StringComparison.Ordinal))
            return true;
        if (string.Equals(select, "ALL_PROJECTED_ATTRIBUTES", StringComparison.Ordinal))
        {
            if (isLsi) return true;
            error = "Select=ALL_PROJECTED_ATTRIBUTES requires IndexName.";
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
