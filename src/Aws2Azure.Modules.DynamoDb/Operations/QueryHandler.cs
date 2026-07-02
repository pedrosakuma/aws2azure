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
        HttpContext ctx, byte[] body, CosmosClient cosmos, bool enableGsi,
        bool enableLsiNumericOrdering, CancellationToken ct)
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
        if (!string.IsNullOrEmpty(req.IndexName) && string.IsNullOrEmpty(req.IndexName.Trim()))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                "IndexName must not be blank.").ConfigureAwait(false);
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
        // Secondary Index queries are always supported; Global Secondary Index
        // queries are gated behind a default-off config flag (cross-partition,
        // eventually consistent — see DynamoDbSettings.EnableGlobalSecondaryIndexQueries).
        // Unknown indices are rejected loudly.
        TableIndexDefinition? lsi = null;
        TableIndexDefinition? gsi = null;
        KeyConditionAnalyser.IndexSortKeySpec? lsiSortKey = null;
        KeyConditionAnalyser.IndexHashKeySpec? gsiHashKey = null;
        KeyConditionAnalyser.IndexSortKeySpec? gsiSortKey = null;
        if (!string.IsNullOrEmpty(req.IndexName))
        {
            var outcome = SecondaryIndexResolver.ResolveIndex(meta, req.IndexName!, out var resolved);
            switch (outcome)
            {
                case SecondaryIndexResolver.IndexResolution.Lsi:
                    lsi = resolved;
                    break;
                case SecondaryIndexResolver.IndexResolution.Gsi:
                    if (!enableGsi)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            "Querying global secondary indexes is not yet supported by the proxy.").ConfigureAwait(false);
                        return;
                    }
                    // A GSI query is eventually consistent in DynamoDB; strongly
                    // consistent reads are not permitted on a GSI.
                    if (req.ConsistentRead == true)
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                            "Consistent reads are not supported on global secondary indexes.").ConfigureAwait(false);
                        return;
                    }
                    gsi = resolved;
                    if (!SecondaryIndexResolver.TryGetGsiKeys(meta, gsi!,
                            out var ghName, out var ghType, out var gsName, out var gsType, out var gsiError))
                    {
                        await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", gsiError).ConfigureAwait(false);
                        return;
                    }
                    gsiHashKey = new KeyConditionAnalyser.IndexHashKeySpec(ghName, ghType);
                    if (gsName is not null)
                        gsiSortKey = new KeyConditionAnalyser.IndexSortKeySpec(gsName, gsType!);
                    break;
                case SecondaryIndexResolver.IndexResolution.NotFound:
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        $"The table does not have the specified index: {req.IndexName}").ConfigureAwait(false);
                    return;
            }

            if (lsi is not null)
            {
                // An LSI's KeySchema is [HASH (= base table HASH), RANGE (alternate
                // sort attribute)]. Resolve the alternate sort attribute + its
                // declared type for the sort-key predicate translation.
                if (!SecondaryIndexResolver.TryGetLsiSortKey(meta, lsi!, out var skName, out var skType, out var lsiError))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", lsiError).ConfigureAwait(false);
                    return;
                }
                lsiSortKey = new KeyConditionAnalyser.IndexSortKeySpec(skName, skType);
            }
        }
        bool isLsiQuery = lsi is not null;
        bool isGsiQuery = gsi is not null;
        // A GSI is a projected view: when its projection is not ALL, only the
        // index's projected attributes are physically available in DynamoDB.
        // The proxy reads the full base-container document, so it must enforce
        // the projected set itself to avoid leaking non-projected attributes.
        bool gsiProjectionAll = isGsiQuery
            && string.Equals(gsi!.ProjectionType, "ALL", StringComparison.OrdinalIgnoreCase);

        if (!IsAllowedSelect(req.Select, isLsiQuery || isGsiQuery, out var selectError))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException", selectError).ConfigureAwait(false);
            return;
        }

        // Select=SPECIFIC_ATTRIBUTES requires a ProjectionExpression (the proxy
        // does not support the legacy AttributesToGet form, which STJ drops).
        // Without one DynamoDB rejects the request; the proxy must too,
        // otherwise the query would fall through with no projection and return
        // every attribute — which on a non-ALL GSI would leak attributes outside
        // the index's projected set.
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
        if (isGsiQuery && !gsiProjectionAll
            && string.Equals(req.Select, "ALL_ATTRIBUTES", StringComparison.Ordinal))
        {
            await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                $"Select=ALL_ATTRIBUTES is not supported on global secondary index '{req.IndexName}' whose projection type is not ALL.").ConfigureAwait(false);
            return;
        }

        KeyConditionAnalyser.AnalysedKeyCondition? keyCond = null;
        KeyConditionAnalyser.AnalysedGsiKeyCondition? gsiKey = null;
        ConditionNode? filter = null;
        Projection? projection = null;
        try
        {
            var kceAst = ConditionExpressionParser.Parse(req.KeyConditionExpression!, names, values);
            if (isGsiQuery)
                gsiKey = KeyConditionAnalyser.AnalyseForGsi(kceAst, gsiHashKey!, gsiSortKey);
            else if (isLsiQuery)
                keyCond = KeyConditionAnalyser.AnalyseForIndex(kceAst, meta, lsiSortKey!);
            else
                keyCond = KeyConditionAnalyser.Analyse(kceAst, meta);

            if (!string.IsNullOrWhiteSpace(req.FilterExpression))
            {
                filter = ConditionExpressionParser.Parse(req.FilterExpression!, names, values);
            }
            if (!string.IsNullOrWhiteSpace(req.ProjectionExpression))
            {
                projection = ProjectionExpressionParser.Parse(req.ProjectionExpression!, names);
                if (isGsiQuery && !gsiProjectionAll)
                {
                    // The GSI only projects a subset of attributes; a
                    // ProjectionExpression cannot reference attributes outside
                    // that set (DynamoDB cannot fetch them from the base table
                    // for a GSI). Reject any non-projected path.
                    var allowed = SecondaryIndexResolver.ResolveIndexProjection(
                        meta, gsi!, gsiHashKey!.Name, gsiSortKey?.Name)!;
                    foreach (var path in projection.RootNames)
                    {
                        bool projected = false;
                        foreach (var a in allowed)
                        {
                            if (string.Equals(a, path, StringComparison.Ordinal)) { projected = true; break; }
                        }
                        if (!projected)
                            throw new KeyConditionException(
                                $"ProjectionExpression references attribute '{path}' that is not projected into index '{req.IndexName}'.");
                    }
                }
            }
            else if ((isLsiQuery || isGsiQuery)
                && (string.IsNullOrEmpty(req.Select)
                    || string.Equals(req.Select, "ALL_PROJECTED_ATTRIBUTES", StringComparison.Ordinal)))
            {
                // For an index query DynamoDB defaults Select to
                // ALL_PROJECTED_ATTRIBUTES when neither Select nor a
                // ProjectionExpression is supplied. ALL_PROJECTED_ATTRIBUTES
                // resolves against the index projection: ALL → all attributes
                // (no in-process projection); KEYS_ONLY → base keys + the
                // index's own key attributes; INCLUDE → those keys plus the
                // index's NonKeyAttributes. An explicit ProjectionExpression
                // (handled above) always takes precedence; an explicit
                // ALL_ATTRIBUTES / SPECIFIC_ATTRIBUTES / COUNT Select skips
                // this branch.
                projection = isGsiQuery
                    ? Wrap(SecondaryIndexResolver.ResolveIndexProjection(meta, gsi!, gsiHashKey!.Name, gsiSortKey?.Name))
                    : Wrap(SecondaryIndexResolver.ResolveIndexProjection(meta, lsi!, lsiSortKey!.Name));
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

        await ExecuteQueryAsync(ctx, req, meta, keyCond, gsiKey, gsiHashKey?.Name, gsiSortKey?.Name,
            gsiSortKey?.Type, filter, projection, lsiSortKey?.Name, lsiSortKey?.Type,
            enableLsiNumericOrdering, continuationIn, cosmos, ct)
            .ConfigureAwait(false);
    }

    private static async Task ExecuteQueryAsync(
        HttpContext ctx, QueryRequest req, TableMetadata meta,
        KeyConditionAnalyser.AnalysedKeyCondition? keyCond,
        KeyConditionAnalyser.AnalysedGsiKeyCondition? gsiKey,
        string? gsiHashName, string? gsiSortName, string? gsiSortType,
        ConditionNode? filter, Projection? projection, string? lsiSortName, string? lsiSortType,
        bool enableLsiNumericOrdering, string? continuationIn,
        CosmosClient cosmos, CancellationToken ct)
    {
        bool forward = req.ScanIndexForward ?? true;
        bool isLsiQuery = lsiSortName is not null;
        bool isGsiQuery = gsiKey is not null;
        bool countOnly = string.Equals(req.Select, "COUNT", StringComparison.OrdinalIgnoreCase);

        string sql;
        List<CosmosSqlParameter> sqlParams;
        var pushdown = FilterPushdownVisitor.Translate(filter);
        if (isGsiQuery)
        {
            // GSI query: there is no partition-key routing. The mandatory HASH
            // equality and optional sort-key predicate target the RAW stored
            // index attributes (`c.<gsiHash>` / `c.<gsiSort>`) and are pushed
            // via the filter translator using distinct `@gh*` / `@sk*` prefixes
            // so they never collide with the user FilterExpression's `@fp*`
            // bindings. Their residuals (e.g. a high-precision envelope N) join
            // the in-process filter chain. The query fans out cross-partition.
            var hashPush = FilterPushdownVisitor.Translate(
                gsiKey!.HashNode, CosmosPathTranslator.DefaultRootAlias, "gh");
            var skPush = FilterPushdownVisitor.Translate(
                gsiKey.SkNode, CosmosPathTranslator.DefaultRootAlias, "sk");

            // A composite GSI SELECT query must be ordered by the GSI sort key.
            // The Cosmos gateway will not serve an ordered cross-partition query
            // in one request (#480), so divert to the client-side fan-out +
            // merge-sort executor. COUNT does not need ordering and is served by
            // the unordered cross-partition loop below (emitOrderBy: false).
            if (gsiSortName is not null && !countOnly)
            {
                // A binary (B) GSI sort key is stored as a Cosmos envelope object
                // ({"_a2a:B":...}), not a scalar, so the per-range `ORDER BY
                // c.<gsiSort>` orders it as an object (type-equal) rather than by
                // the binary payload — the merge precondition (each range locally
                // sorted by the sort value) cannot hold. Reject it rather than
                // return silently mis-ordered results.
                if (string.Equals(gsiSortType, "B", StringComparison.Ordinal))
                {
                    await CosmosOpsShared.WriteErrorAsync(ctx, 400, "ValidationException",
                        "Ordered queries on a global secondary index with a binary (B) sort key are not supported.")
                        .ConfigureAwait(false);
                    return;
                }

                // High-precision numeric sort key (#482): the raw-attribute
                // pushdown cannot exactly filter {"_a2a:N":…} envelope values
                // (ordered N comparisons widen to IS_DEFINED + client residual),
                // which would over-scan under Limit and mis-paginate. Re-translate
                // the sort-key KeyCondition against the order-preserving encoded
                // field so it filters exactly in the same domain the ORDER BY /
                // resume bound use — no residual, no over-scan.
                bool numericSort = string.Equals(gsiSortType, "N", StringComparison.Ordinal);
                if (numericSort && gsiKey.SkNode is not null)
                {
                    var encodedPath = CosmosPathTranslator.Translate(new DocumentPath(new[]
                    {
                        new AttributePathSegment(
                            Persistence.InferredAttributeStorage.OrderKeyPropertyPrefix + gsiSortName),
                    }));
                    var encodedSk = BuildNumericSortKeyPushdown(gsiKey.SkNode, encodedPath, "sk");
                    if (encodedSk is not null)
                    {
                        skPush = encodedSk;
                    }
                }

                var residual = CombineResidual(
                    CombineResidual(hashPush.Residual, skPush.Residual), pushdown.Residual);
                await CrossPartitionOrderByQuery.ExecuteAsync(
                    ctx, req, gsiHashName!, gsiSortName, numericSort, forward,
                    hashPush, skPush, pushdown, residual, projection, cosmos, ct)
                    .ConfigureAwait(false);
                return;
            }

            (sql, sqlParams) = BuildGsiSql(
                gsiHashName!, gsiSortName, forward, hashPush, skPush, pushdown,
                emitOrderBy: !countOnly);
            filter = CombineResidual(CombineResidual(hashPush.Residual, skPush.Residual), pushdown.Residual);
        }
        else if (isLsiQuery)
        {
            // LSI query: the sort-key predicate (if any) targets the RAW
            // stored attribute `c.<lsiSort>` (Option-A), not `c.id`. Reuse the
            // filter pushdown to translate it — distinct `@sk*` parameter
            // prefix so it never collides with the user FilterExpression's
            // `@fp*` bindings. Its residual (e.g. high-precision envelope N)
            // joins the in-process filter chain. ORDER BY targets the LSI sort
            // attribute. Items missing the attribute are excluded by an explicit
            // IS_DEFINED guard in BuildLsiSql, matching LSI sparse-index semantics.
            //
            // #504: for a numeric (N) LSI sort key with the opt-in flag enabled,
            // order by (and range-filter against) the order-preserving encoded
            // `_a2a$ord$<attr>` field instead of the raw envelope attribute, so
            // high-precision values sort numerically. Opt-in because the encoded
            // ORDER BY excludes items lacking the field (pre-encoded-field data),
            // which would silently drop legacy items from this always-on feature.
            bool lsiNumericOrder = enableLsiNumericOrdering
                && string.Equals(lsiSortType, "N", StringComparison.Ordinal);
            string? lsiOrderPath = null;
            FilterPushdownResult skPush;
            if (lsiNumericOrder)
            {
                lsiOrderPath = CosmosPathTranslator.Translate(new DocumentPath(new[]
                {
                    new AttributePathSegment(
                        Persistence.InferredAttributeStorage.OrderKeyPropertyPrefix + lsiSortName),
                }));
                var rawSkPush = FilterPushdownVisitor.Translate(
                    keyCond!.IndexSortKeyNode, CosmosPathTranslator.DefaultRootAlias, "sk");
                var encodedSk = keyCond.IndexSortKeyNode is null
                    ? null
                    : BuildNumericSortKeyPushdown(keyCond.IndexSortKeyNode, lsiOrderPath, "sk");
                skPush = encodedSk ?? rawSkPush;
            }
            else
            {
                skPush = FilterPushdownVisitor.Translate(
                    keyCond!.IndexSortKeyNode, CosmosPathTranslator.DefaultRootAlias, "sk");
            }
            (sql, sqlParams) = BuildLsiSql(lsiSortName!, forward, skPush, pushdown, lsiOrderPath);
            filter = CombineResidual(skPush.Residual, pushdown.Residual);
        }
        else
        {
            (sql, sqlParams) = BuildSql(
                keyCond!, forward, FindKey(meta, "RANGE") is not null, pushdown);
            // Replace the parsed filter with the residual; the pushable
            // half is already enforced by Cosmos, so we only need to
            // evaluate what could not be translated.
            filter = pushdown.Residual;
        }

        using var queryBody = CosmosQueryBody.Build(sql, sqlParams);
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + req.TableName;
        var collUri = "/" + collLink + "/docs";
        // A GSI query is cross-partition: there is no single partition-key
        // scope, so no partition-key header is sent (the cross-partition
        // header is added on the request below instead).
        var pkHeader = isGsiQuery ? null : CosmosOpsShared.BuildPartitionKeyHeader(keyCond!.HashValue);

        // Fast path: no in-process per-item work (no residual filter, no
        // projection) and nothing pushed to Cosmos (so ScannedCount ==
        // Count). Stream Cosmos documents straight into the DynamoDB
        // response envelope without materializing an AttributeValue map
        // per item. Mirrors ScanHandler.ExecuteScanFusedAsync. LSI and GSI
        // queries always use the materialized path: the fused path assumes
        // base-table semantics (single partition, sort on `c.id`, no index
        // projection).
        if (!isLsiQuery && !isGsiQuery && !countOnly && filter is null && projection is null && pushdown.Sql is null)
        {
            await ExecuteQueryFusedAsync(
                ctx, req, continuationIn, queryBody.WrittenMemory, collLink, collUri, pkHeader!, cosmos, ct)
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

            var headers = new List<KeyValuePair<string, string>>
            {
                new("x-ms-documentdb-isquery", "true"),
                new("x-ms-max-item-count", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            if (isGsiQuery)
            {
                // Cross-partition fan-out: no partition-key scope.
                headers.Add(new KeyValuePair<string, string>("x-ms-documentdb-query-enablecrosspartition", "true"));
            }
            else
            {
                headers.Add(new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader!));
            }
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
            // continuation, returning the continuation as
            // LastEvaluatedKey. Empty pages still iterate so we don't
            // return a zero-row page when Cosmos has more rows
            // waiting. LSI queries always pre-filter (sort-key predicate
            // and/or the sparse ORDER BY), so they follow the same rule. GSI
            // queries always pre-filter (the cross-partition HASH equality
            // and/or sparse ORDER BY) and additionally cannot recover a
            // faithful pre-filter ScannedCount, so they preserve the Cosmos
            // page boundary too.
            if ((pushdown.Sql is not null || isLsiQuery || isGsiQuery) && matched > 0) break;
        }

        // See ScanHandler: a pushed filter makes `scanned` count only matched
        // documents. Recover DynamoDB's pre-filter ScannedCount only for a
        // single complete unbounded pass (no Limit, no incoming
        // ExclusiveStartKey, no outgoing continuation) via a server-side
        // aggregate over the same key scope minus the pushed filter
        // (single-partition, so cheap). Limit/paginated queries remain a
        // documented divergence: the aggregate spans the whole scope and would
        // not match DynamoDB's per-page count on a resumed/partial page. LSI
        // and GSI queries skip the aggregate recovery entirely (the base-table
        // count scope is on `c.id` / a single partition, not the index key
        // attribute) — ScannedCount reflects rows after the Cosmos prefilter of
        // the index key predicate (a documented divergence).
        if (!isLsiQuery && !isGsiQuery
            && pushdown.Sql is not null
            && wantedScanned == int.MaxValue
            && string.IsNullOrEmpty(continuationIn)
            && string.IsNullOrEmpty(continuationOut))
        {
            var (countSql, countParams) = BuildCountSql(keyCond!, FindKey(meta, "RANGE") is not null);
            var faithful = await ScannedCountQuery.CountAsync(
                cosmos, collLink, collUri, countSql, countParams,
                partitionKeyHeader: pkHeader!, strong: req.ConsistentRead == true, ct).ConfigureAwait(false);
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
        ReadOnlyMemory<byte> queryBody, string collLink, string collUri, string pkHeader,
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

    /// <summary>
    /// Builds the partition-scoped Cosmos SQL for a Local Secondary Index
    /// query. The sort-key predicate (if any) and the user FilterExpression
    /// are pushed via the filter translator against the RAW stored attributes
    /// (<c>c.&lt;lsiSort&gt;</c>); <c>ORDER BY c.&lt;lsiSort&gt;</c> honours
    /// <c>ScanIndexForward</c>. Items missing the LSI sort attribute are
    /// excluded by an explicit <c>IS_DEFINED</c> guard so that sparse-index
    /// semantics hold regardless of the container's indexing policy (an
    /// <c>ORDER BY</c> alone only excludes undefined paths for certain
    /// indexing policies, which the sidecar does not control).
    /// </summary>
    internal static (string sql, List<CosmosSqlParameter> parameters) BuildLsiSql(
        string lsiSortName, bool forward,
        FilterPushdownResult skPush, FilterPushdownResult userPush,
        string? orderByPathOverride = null)
    {
        var lsiPath = CosmosPathTranslator.Translate(
            new DocumentPath(new[] { new AttributePathSegment(lsiSortName) }));
        var sb = new StringBuilder("SELECT * FROM c WHERE c._a2a = 'item'");
        var parameters = new List<CosmosSqlParameter>();
        sb.Append(" AND IS_DEFINED(").Append(lsiPath).Append(')');
        // #504 numeric ordering: also require the encoded order field so items
        // written before it existed are excluded rather than mis-ordered (the
        // opt-in flag's documented backfill contract).
        if (orderByPathOverride is not null)
        {
            sb.Append(" AND IS_DEFINED(").Append(orderByPathOverride).Append(')');
        }
        if (skPush.Sql is { } skSql)
        {
            sb.Append(" AND ").Append(skSql);
            foreach (var p in skPush.Parameters) parameters.Add(p);
        }
        if (userPush.Sql is { } fSql)
        {
            sb.Append(" AND ").Append(fSql);
            foreach (var fp in userPush.Parameters) parameters.Add(fp);
        }
        var orderPath = orderByPathOverride ?? lsiPath;
        sb.Append(" ORDER BY ").Append(orderPath).Append(forward ? " ASC" : " DESC");
        return (sb.ToString(), parameters);
    }

    /// <summary>
    /// Builds the cross-partition Cosmos SQL for a Global Secondary Index
    /// query. Both the mandatory HASH equality and the optional sort-key
    /// predicate are pushed against the RAW stored index attributes
    /// (<c>c.&lt;gsiHash&gt;</c> / <c>c.&lt;gsiSort&gt;</c>) via the filter
    /// translator, alongside the user FilterExpression. An explicit
    /// <c>IS_DEFINED</c> guard on each index key attribute enforces GSI
    /// membership semantics (an item is an index member only if it carries the
    /// index's key attributes) independent of the container's indexing policy.
    /// <c>ORDER BY c.&lt;gsiSort&gt;</c> honours <c>ScanIndexForward</c> and is
    /// emitted only for a composite GSI (a hash-only GSI returns unordered).
    /// </summary>
    internal static (string sql, List<CosmosSqlParameter> parameters) BuildGsiSql(
        string gsiHashName, string? gsiSortName, bool forward,
        FilterPushdownResult hashPush, FilterPushdownResult skPush, FilterPushdownResult userPush,
        bool emitOrderBy = true,
        string? resumeFilterSql = null,
        IReadOnlyList<CosmosSqlParameter>? resumeParams = null,
        string? orderByPathOverride = null)
    {
        var hashPath = CosmosPathTranslator.Translate(
            new DocumentPath(new[] { new AttributePathSegment(gsiHashName) }));
        var sb = new StringBuilder("SELECT * FROM c WHERE c._a2a = 'item'");
        var parameters = new List<CosmosSqlParameter>();
        sb.Append(" AND IS_DEFINED(").Append(hashPath).Append(')');
        if (gsiSortName is not null)
        {
            var sortPath = CosmosPathTranslator.Translate(
                new DocumentPath(new[] { new AttributePathSegment(gsiSortName) }));
            sb.Append(" AND IS_DEFINED(").Append(sortPath).Append(')');
        }
        // High-precision numeric sort key (#482): ordering targets the stored
        // order-preserving `_a2a$ord$<attr>` field instead of the raw attribute
        // (whose {"_a2a:N":…} envelope Cosmos orders as an object). Items
        // written before that field existed lack it — exclude them from ordered
        // results rather than mis-order them (backfill gap; documented).
        if (orderByPathOverride is not null)
        {
            sb.Append(" AND IS_DEFINED(").Append(orderByPathOverride).Append(')');
        }
        if (hashPush.Sql is { } hSql)
        {
            sb.Append(" AND ").Append(hSql);
            foreach (var p in hashPush.Parameters) parameters.Add(p);
        }
        if (skPush.Sql is { } skSql)
        {
            sb.Append(" AND ").Append(skSql);
            foreach (var p in skPush.Parameters) parameters.Add(p);
        }
        if (userPush.Sql is { } fSql)
        {
            sb.Append(" AND ").Append(fSql);
            foreach (var fp in userPush.Parameters) parameters.Add(fp);
        }
        // Cross-partition ordered resume bound (#481): restricts each physical
        // partition to rows at/after the continuation boundary value.
        if (resumeFilterSql is not null)
        {
            sb.Append(" AND ").Append(resumeFilterSql);
            if (resumeParams is not null)
            {
                foreach (var rp in resumeParams) parameters.Add(rp);
            }
        }
        if (emitOrderBy && gsiSortName is not null)
        {
            var orderPath = orderByPathOverride ?? CosmosPathTranslator.Translate(
                new DocumentPath(new[] { new AttributePathSegment(gsiSortName) }));
            sb.Append(" ORDER BY ").Append(orderPath).Append(forward ? " ASC" : " DESC");
        }
        return (sb.ToString(), parameters);
    }

    /// <summary>Combines two optional residual condition nodes with AND.</summary>
    private static ConditionNode? CombineResidual(ConditionNode? a, ConditionNode? b)
        => (a, b) switch
        {
            (null, null) => null,
            (ConditionNode l, null) => l,
            (null, ConditionNode r) => r,
            (ConditionNode l, ConditionNode r) => new AndCondition(l, r),
        };

    /// <summary>
    /// Translates a numeric (N) GSI sort-key KeyCondition (<c>=, &lt;, &lt;=,
    /// &gt;, &gt;=, BETWEEN</c>) into an exactly-pushable Cosmos predicate over
    /// the order-preserving encoded field (<c>_a2a$ord$&lt;attr&gt;</c>, #482).
    /// The encoded field is an order-preserving string whose lexical order
    /// equals numeric order, so each operand is encoded via
    /// <see cref="KeyScalarCodec.TryEncodeNumberOrderKey"/> and compared as a
    /// string — no envelope/residual widening, so range conditions filter
    /// exactly (and do not over-scan under Limit). Returns <c>null</c> when the
    /// node cannot be encoded, leaving the caller's raw-attribute pushdown +
    /// client residual in place. The <see cref="KeyConditionAnalyser"/> has
    /// already restricted the node to Compare/Between with a typed N value on
    /// the sort key.
    /// </summary>
    internal static FilterPushdownResult? BuildNumericSortKeyPushdown(
        ConditionNode skNode, string encodedPath, string paramPrefix)
    {
        switch (skNode)
        {
            case CompareCondition cmp when cmp.Right is ConditionValueOperand v:
            {
                if (!TryEncodeSortKeyOperand(v, out var enc)) return null;
                string? op = cmp.Op switch
                {
                    CompareOp.Equal => " = ",
                    CompareOp.Less => " < ",
                    CompareOp.LessEqual => " <= ",
                    CompareOp.Greater => " > ",
                    CompareOp.GreaterEqual => " >= ",
                    _ => null,
                };
                if (op is null) return null;
                var p0 = "@" + paramPrefix + "0";
                return new FilterPushdownResult(
                    "(" + encodedPath + op + p0 + ")",
                    new[] { new CosmosSqlParameter(p0, enc) },
                    Residual: null);
            }

            case BetweenCondition bt
                when bt.Lower is ConditionValueOperand lo && bt.Upper is ConditionValueOperand hi:
            {
                if (!TryEncodeSortKeyOperand(lo, out var encLo)
                    || !TryEncodeSortKeyOperand(hi, out var encHi))
                {
                    return null;
                }
                var pL = "@" + paramPrefix + "0";
                var pU = "@" + paramPrefix + "1";
                return new FilterPushdownResult(
                    "(" + encodedPath + " >= " + pL + " AND " + encodedPath + " <= " + pU + ")",
                    new[] { new CosmosSqlParameter(pL, encLo), new CosmosSqlParameter(pU, encHi) },
                    Residual: null);
            }

            default:
                return null;
        }

        static bool TryEncodeSortKeyOperand(ConditionValueOperand operand, out string encoded)
        {
            encoded = string.Empty;
            if (!ParsedAttributeValue.TryParse(operand.Value.Value, out var parsed)) return false;
            if (!string.Equals(parsed.TypeTag, AttributeValueTypes.Number, StringComparison.Ordinal))
                return false;
            var raw = parsed.Value.GetString();
            return raw is not null && KeyScalarCodec.TryEncodeNumberOrderKey(raw, out encoded, out _);
        }
    }

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
                    parameters.Add(new("@sk0", cmp.Value));
                    break;
                case KeyConditionAnalyser.SkBetween bt:
                    sb.Append(" AND c.id >= @skLo AND c.id <= @skHi");
                    parameters.Add(new("@skLo", bt.Lo));
                    parameters.Add(new("@skHi", bt.Hi));
                    break;
                case KeyConditionAnalyser.SkBeginsWith bw:
                    sb.Append(" AND STARTSWITH(c.id, @sk0)");
                    parameters.Add(new("@sk0", bw.Prefix));
                    break;
            }
        }
    }

    private static Projection? Wrap(IReadOnlyList<string>? topLevelNames)
        => topLevelNames is null ? null : Projection.FromTopLevelNames(topLevelNames);

    internal static string? ExtractContinuation(JsonElement? exclusiveStartKey)
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

    internal static Dictionary<string, JsonElement> BuildContinuationKey(string cosmosContinuation)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cosmosContinuation));
        var json = $"{{\"S\":\"{b64}\"}}";
        using var doc = JsonDocument.Parse(json);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [ContinuationSentinelAttr] = doc.RootElement.Clone(),
        };
    }

    private static bool IsAllowedSelect(string? select, bool hasLsi, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(select)) return true;
        if (string.Equals(select, "ALL_ATTRIBUTES", StringComparison.Ordinal)
            || string.Equals(select, "COUNT", StringComparison.Ordinal)
            || string.Equals(select, "SPECIFIC_ATTRIBUTES", StringComparison.Ordinal))
            return true;
        if (string.Equals(select, "ALL_PROJECTED_ATTRIBUTES", StringComparison.Ordinal))
        {
            if (hasLsi) return true;
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
