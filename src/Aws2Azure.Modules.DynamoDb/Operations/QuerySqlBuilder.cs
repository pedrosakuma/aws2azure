using System.Collections.Generic;
using System.Text;
using Aws2Azure.Modules.DynamoDb.Expressions;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class QueryHandler
{
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
}
