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

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Implements DynamoDB <c>Query</c> against a Cosmos container.
///
/// <para>Translation strategy:</para>
/// <list type="bullet">
///   <item>The HASH equality from <c>KeyConditionExpression</c>
///   becomes the Cosmos partition-key scope (<c>x-ms-documentdb-partitionkey</c>).
///   This means every Query is a single-partition Cosmos query — no
///   cross-partition fan-out.</item>
///   <item>The optional sort-key predicate becomes a SQL predicate on
///   <c>c.id</c> (the Cosmos document id), which we set to the
///   formatted RANGE value at write time.</item>
///   <item><c>FilterExpression</c> is split by
///   <see cref="FilterPushdownVisitor"/>: the pushable fragment is
///   appended to the Cosmos WHERE clause; any residual is evaluated
///   in-process via <see cref="ConditionEvaluator"/>. <c>Count</c> always
///   reflects post-filter rows. <c>ScannedCount</c> must reflect rows
///   examined <em>before</em> the filter: when nothing is pushed it is the
///   streamed row count; when a fragment is pushed (so Cosmos pre-filters)
///   a complete unbounded pass recovers it with a server-side
///   <c>SELECT VALUE COUNT(1)</c> over the same key scope minus the pushed
///   filter (see <see cref="ScannedCountQuery"/>). A pushed filter combined
///   with <c>Limit</c> is a documented divergence — see the gap doc.</item>
///   <item><c>ProjectionExpression</c> is applied in-process. Each
///   path may be a top-level attribute or a <c>#alias</c>; nested
///   path projection is deferred.</item>
///   <item><c>Limit</c> and pagination ride on the Cosmos
///   <c>x-ms-max-item-count</c> / <c>x-ms-continuation</c> machinery;
///   the DDB <c>LastEvaluatedKey</c> wire shape carries the Cosmos
///   continuation token in a sentinel attribute that round-trips
///   through every AWS SDK.</item>
/// </list>
///
/// Not supported in this slice: <c>IndexName</c> (GSI/LSI), the legacy
/// <c>KeyConditions</c>/<c>QueryFilter</c> shape, numeric sort-key
/// ordering (sort keys order lexicographically by their formatted
/// string — see the gap doc), parallel <c>Segment</c>/<c>TotalSegments</c>.
/// </summary>
internal static class QueryHandler
{
    private const string ContinuationSentinelAttr = "__a2a_continuation";
    private const int MaxBatchSize = 1000;

    public static async Task HandleQueryAsync(
        HttpContext ctx, byte[] body, CosmosClient cosmos, CancellationToken ct)
    {
        QueryRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, QueryJsonContext.Default.QueryRequest);
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
        if (string.IsNullOrWhiteSpace(req.KeyConditionExpression))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "KeyConditionExpression is required.").ConfigureAwait(false);
            return;
        }
        if (!string.IsNullOrEmpty(req.IndexName))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Querying secondary indexes is not yet supported by the proxy.").ConfigureAwait(false);
            return;
        }
        if (HasContent(req.KeyConditions) || HasContent(req.QueryFilter)
            || !string.IsNullOrEmpty(req.ConditionalOperator))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "Legacy KeyConditions / QueryFilter / ConditionalOperator are not supported; use KeyConditionExpression and FilterExpression.").ConfigureAwait(false);
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
        var meta = metaResult.Metadata!;

        KeyConditionAnalyser.AnalysedKeyCondition keyCond;
        ConditionNode? filter = null;
        IReadOnlyList<string>? projection = null;
        try
        {
            var kceAst = ConditionExpressionParser.Parse(req.KeyConditionExpression!, names, values);
            keyCond = KeyConditionAnalyser.Analyse(kceAst, meta);

            if (!string.IsNullOrWhiteSpace(req.FilterExpression))
            {
                filter = ConditionExpressionParser.Parse(req.FilterExpression!, names, values);
            }
            if (!string.IsNullOrWhiteSpace(req.ProjectionExpression))
            {
                projection = ProjectionExpressionParser.Parse(req.ProjectionExpression!, names);
            }
        }
        catch (KeyConditionException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", ex.Message).ConfigureAwait(false);
            return;
        }
        catch (ExpressionSyntaxException ex)
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Invalid expression (offset {ex.Position}): {ex.Message}").ConfigureAwait(false);
            return;
        }

        // Decode incoming continuation token, if any.
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

        await ExecuteQueryAsync(ctx, req, meta, keyCond, filter, projection, continuationIn, cosmos, ct)
            .ConfigureAwait(false);
    }

    private static async Task ExecuteQueryAsync(
        HttpContext ctx, QueryRequest req, TableMetadata meta,
        KeyConditionAnalyser.AnalysedKeyCondition keyCond,
        ConditionNode? filter, IReadOnlyList<string>? projection, string? continuationIn,
        CosmosClient cosmos, CancellationToken ct)
    {
        bool forward = req.ScanIndexForward ?? true;
        bool countOnly = string.Equals(req.Select, "COUNT", StringComparison.OrdinalIgnoreCase);

        var pushdown = FilterPushdownVisitor.Translate(filter);
        var (sql, sqlParams) = BuildSql(
            keyCond, forward, FindKey(meta, "RANGE") is not null, pushdown);
        // Replace the parsed filter with the residual; the pushable
        // half is already enforced by Cosmos, so we only need to
        // evaluate what could not be translated.
        filter = pushdown.Residual;

        var queryBody = CosmosQueryBody.Build(sql, sqlParams);
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var collUri = "/" + collLink + "/docs";
        var pkHeader = CosmosOpsShared.BuildPartitionKeyHeader(keyCond.HashValue);

        // Fast path: no in-process per-item work (no residual filter, no
        // projection) and nothing pushed to Cosmos (so ScannedCount ==
        // Count). Stream Cosmos documents straight into the DynamoDB
        // response envelope without materializing an AttributeValue map
        // per item. Mirrors ScanHandler.ExecuteScanFusedAsync.
        if (!countOnly && filter is null && projection is null && pushdown.Sql is null)
        {
            await ExecuteQueryFusedAsync(
                ctx, req, continuationIn, queryBody, collLink, collUri, pkHeader, cosmos, ct)
                .ConfigureAwait(false);
            return;
        }

        int wantedScanned = req.Limit ?? int.MaxValue;
        var items = new List<Dictionary<string, JsonElement>>();
        int scanned = 0;
        int matched = 0;
        string? continuationOut = continuationIn;

        while (true)
        {
            // DynamoDB's Limit caps *evaluated* items (pre-filter). Ask
            // Cosmos for at most as many docs as we still have budget
            // for so we never read past the Limit boundary — that lets
            // us return the per-page continuation as the
            // LastEvaluatedKey without silently dropping rows.
            int remaining = wantedScanned == int.MaxValue
                ? MaxBatchSize
                : Math.Max(1, wantedScanned - scanned);
            int pageSize = Math.Min(MaxBatchSize, remaining);

            using var content = new StringContent(queryBody, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/query+json");

            var headers = new List<KeyValuePair<string, string>>
            {
                new("x-ms-documentdb-partitionkey", pkHeader),
                new("x-ms-documentdb-isquery", "true"),
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

            var pageItems = await CosmosOpsShared.ReadAndExtractItemsAsync(
                resp.Content, DynamoDbMetrics.OpQuery, ct).ConfigureAwait(false);

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
                    items.Add(projection is null ? itemMap : Project(itemMap, projection));
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
            // continuation, returning the continuation as
            // LastEvaluatedKey. Empty pages still iterate so we don't
            // return a zero-row page when Cosmos has more rows
            // waiting.
            if (pushdown.Sql is not null && matched > 0) break;
        }

        // See ScanHandler: a pushed filter makes `scanned` count only matched
        // documents. Recover DynamoDB's pre-filter ScannedCount only for a
        // single complete unbounded pass (no Limit, no incoming
        // ExclusiveStartKey, no outgoing continuation) via a server-side
        // aggregate over the same key scope minus the pushed filter
        // (single-partition, so cheap). Limit/paginated queries remain a
        // documented divergence: the aggregate spans the whole scope and would
        // not match DynamoDB's per-page count on a resumed/partial page.
        if (pushdown.Sql is not null
            && wantedScanned == int.MaxValue
            && string.IsNullOrEmpty(continuationIn)
            && string.IsNullOrEmpty(continuationOut))
        {
            var (countSql, countParams) = BuildCountSql(keyCond, FindKey(meta, "RANGE") is not null);
            var faithful = await ScannedCountQuery.CountAsync(
                cosmos, collLink, collUri, countSql, countParams,
                partitionKeyHeader: pkHeader, strong: req.ConsistentRead == true, ct).ConfigureAwait(false);
            if (faithful is int fc && fc >= matched)
            {
                scanned = fc;
            }
        }

        DynamoDbMetrics.RecordReadTransformPath(DynamoDbMetrics.OpQuery, DynamoDbMetrics.PathMaterialized);
        var response = new QueryResponse
        {
            Items = countOnly ? null : items,
            Count = matched,
            ScannedCount = scanned,
            LastEvaluatedKey = string.IsNullOrEmpty(continuationOut) ? null : BuildContinuationKey(continuationOut),
        };
        await CosmosOpsShared.WriteJsonAsync(ctx, 200, response, QueryJsonContext.Default.QueryResponse)
            .ConfigureAwait(false);
    }

    private static async Task ExecuteQueryFusedAsync(
        HttpContext ctx, QueryRequest req, string? continuationIn,
        string queryBody, string collLink, string collUri, string pkHeader,
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
                new("x-ms-documentdb-partitionkey", pkHeader),
                new("x-ms-documentdb-isquery", "true"),
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

            using var page = await CosmosOpsShared.ReadCosmosRawBodyAsync(resp.Content, ct).ConfigureAwait(false);
            // Pure binary-direct streaming transform: stream Cosmos documents straight
            // into the DDB envelope. No decode-to-text step and no fallback — a reader
            // decline propagates as an error, exactly like a malformed text page would.
            if (Internal.CosmosBinaryDecoder.IsBinary(page.WrittenMemory.Span))
            {
                DynamoDbMetrics.RecordReadDecodePath(DynamoDbMetrics.OpQuery, DynamoDbMetrics.DecodeBinary);
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
                DynamoDbMetrics.RecordReadDecodePath(DynamoDbMetrics.OpQuery, DynamoDbMetrics.DecodeText);
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

        DynamoDbMetrics.RecordReadTransformPath(DynamoDbMetrics.OpQuery, DynamoDbMetrics.PathFused);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        await ctx.Response.BodyWriter.WriteAsync(outBuf.WrittenMemory, ct).ConfigureAwait(false);
    }

    private static ReadOnlySpan<byte> ItemsNameU8 => "Items"u8;
    private static ReadOnlySpan<byte> CountNameU8 => "Count"u8;
    private static ReadOnlySpan<byte> ScannedCountNameU8 => "ScannedCount"u8;
    private static ReadOnlySpan<byte> LastEvaluatedKeyNameU8 => "LastEvaluatedKey"u8;
    private static ReadOnlySpan<byte> TypedStringNameU8 => "S"u8;

    // -------- helpers ------------------------------------------------

    internal static (string sql, List<CosmosSqlParameter> parameters) BuildSql(
        KeyConditionAnalyser.AnalysedKeyCondition keyCond, bool forward, bool composite,
        FilterPushdownResult pushdown)
    {
        var sb = new StringBuilder("SELECT * FROM c WHERE c._a2a = 'item'");
        var parameters = new List<CosmosSqlParameter>();
        AppendSortKeyPredicate(sb, keyCond, parameters);
        if (pushdown.Sql is { } fSql)
        {
            sb.Append(" AND ").Append(fSql);
            foreach (var fp in pushdown.Parameters) parameters.Add(fp);
        }
        if (composite)
        {
            sb.Append(" ORDER BY c.id ").Append(forward ? "ASC" : "DESC");
        }
        return (sb.ToString(), parameters);
    }

    /// <summary>
    /// The aggregate counterpart of <see cref="BuildSql"/> used to recover a
    /// faithful <c>ScannedCount</c>: the same partition/sort-key scope with no
    /// pushed filter and no ORDER BY (illegal alongside <c>VALUE COUNT</c>),
    /// projected as a server-side count.
    /// </summary>
    internal static (string sql, List<CosmosSqlParameter> parameters) BuildCountSql(
        KeyConditionAnalyser.AnalysedKeyCondition keyCond, bool composite)
    {
        var sb = new StringBuilder("SELECT VALUE COUNT(1) FROM c WHERE c._a2a = 'item'");
        var parameters = new List<CosmosSqlParameter>();
        AppendSortKeyPredicate(sb, keyCond, parameters);
        return (sb.ToString(), parameters);
    }

    private static void AppendSortKeyPredicate(
        StringBuilder sb, KeyConditionAnalyser.AnalysedKeyCondition keyCond,
        List<CosmosSqlParameter> parameters)
    {
        if (keyCond.Sk is { } sk)
        {
            switch (sk)
            {
                case KeyConditionAnalyser.SkCompare cmp:
                    sb.Append(" AND c.id ").Append(cmp.Op).Append(" @sk0");
                    parameters.Add(new("@sk0", CosmosQueryBody.StringValue(cmp.Value)));
                    break;
                case KeyConditionAnalyser.SkBetween bt:
                    sb.Append(" AND c.id >= @skLo AND c.id <= @skHi");
                    parameters.Add(new("@skLo", CosmosQueryBody.StringValue(bt.Lo)));
                    parameters.Add(new("@skHi", CosmosQueryBody.StringValue(bt.Hi)));
                    break;
                case KeyConditionAnalyser.SkBeginsWith bw:
                    sb.Append(" AND STARTSWITH(c.id, @sk0)");
                    parameters.Add(new("@sk0", CosmosQueryBody.StringValue(bw.Prefix)));
                    break;
            }
        }
    }

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

    private static TableKeySchemaElement? FindKey(TableMetadata meta, string role)
    {
        foreach (var k in meta.KeySchema)
        {
            if (string.Equals(k.KeyType, role, StringComparison.OrdinalIgnoreCase)) return k;
        }
        return null;
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
