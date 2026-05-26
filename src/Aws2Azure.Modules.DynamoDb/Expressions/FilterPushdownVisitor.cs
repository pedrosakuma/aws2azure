using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Persistence;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// A single Cosmos SQL parameter binding (e.g. <c>@fp0</c> bound to a
/// JSON value). Stays a <see cref="JsonElement"/> so the SQL body
/// builder can emit numbers, strings, bools, and null with their
/// original JSON kind.
/// </summary>
internal sealed record CosmosSqlParameter(string Name, JsonElement Value);

/// <summary>
/// Output of <see cref="FilterPushdownVisitor.Translate"/>.
/// <para><see cref="Sql"/> — the Cosmos SQL fragment that can be
/// pushed into the WHERE clause (no leading <c>WHERE</c> /
/// <c>AND</c>), or <c>null</c> when nothing was pushable.</para>
/// <para><see cref="Parameters"/> — bindings for the SQL fragment;
/// empty when <see cref="Sql"/> is <c>null</c>.</para>
/// <para><see cref="Residual"/> — the part of the AST that must be
/// evaluated client-side via <see cref="ConditionEvaluator"/>; null
/// when the whole expression pushed down.</para>
/// </summary>
internal sealed record FilterPushdownResult(
    string? Sql,
    IReadOnlyList<CosmosSqlParameter> Parameters,
    ConditionNode? Residual);

/// <summary>
/// Walks a parsed <see cref="ConditionNode"/> (the AST emitted by
/// <see cref="ConditionExpressionParser"/>) and produces a Cosmos SQL
/// fragment plus parameter bindings for the parts that can be safely
/// evaluated server-side, falling back to a residual AST for parts
/// that must be evaluated client-side (size, ambiguous types, etc.).
///
/// <para><b>Number storage hybrid.</b> Numeric values live in two
/// shapes after the flat-Cosmos rewrite: bare JSON numbers (when the
/// canonical form survives an IEEE-754 round-trip) and the
/// <c>{"_a2a:N":"&lt;canonical&gt;"}</c> envelope for everything else.
/// Numeric comparisons therefore emit a two-branch OR that matches
/// either shape — Cosmos picks the matching branch per-item. The
/// envelope branch goes through <c>StringToNumber</c> which means
/// comparisons beyond double precision (≳15 significant digits)
/// are unreliable for envelope-stored values; see the gap-doc
/// <c>behavior_differences</c> notes.</para>
///
/// <para><b>Invariant.</b> Every <see cref="Visit"/> invocation
/// either returns a non-null <see cref="VisitResult.Sql"/> (in which
/// case the parameters it bound stay live) or returns null
/// <see cref="VisitResult.Sql"/> with the original node as the
/// residual (in which case any parameters allocated during the
/// attempt are rolled back). This guarantees the returned
/// <see cref="FilterPushdownResult.Parameters"/> are exactly those
/// referenced by <see cref="FilterPushdownResult.Sql"/>.</para>
/// </summary>
internal static class FilterPushdownVisitor
{
    /// <summary>Default Cosmos parameter prefix (placeholder uses
    /// <c>@fp0</c>, <c>@fp1</c>, …). Picked to be distinct from the
    /// <c>@sk*</c> / <c>@pk*</c> names <see cref="QueryHandler"/>
    /// already uses for the key condition.</summary>
    internal const string DefaultParameterPrefix = "fp";

    public static FilterPushdownResult Translate(
        ConditionNode? root,
        string rootAlias = CosmosPathTranslator.DefaultRootAlias,
        string parameterPrefix = DefaultParameterPrefix)
    {
        if (root is null)
        {
            return new FilterPushdownResult(null, Array.Empty<CosmosSqlParameter>(), null);
        }

        var ctx = new Context(rootAlias, parameterPrefix);
        var result = Visit(root, ctx);
        return new FilterPushdownResult(result.Sql, ctx.Parameters, result.Residual);
    }

    // ---------------- visitor plumbing ------------------------------

    private sealed class Context
    {
        public Context(string rootAlias, string paramPrefix)
        {
            RootAlias = rootAlias;
            ParameterPrefix = paramPrefix;
            Parameters = new List<CosmosSqlParameter>();
        }

        public string RootAlias { get; }
        public string ParameterPrefix { get; }
        public List<CosmosSqlParameter> Parameters { get; }

        public string Bind(JsonElement value)
        {
            var name = "@" + ParameterPrefix + Parameters.Count.ToString(CultureInfo.InvariantCulture);
            Parameters.Add(new CosmosSqlParameter(name, value));
            return name;
        }

        public int Snapshot() => Parameters.Count;

        public void Rollback(int snapshot)
        {
            if (Parameters.Count > snapshot)
                Parameters.RemoveRange(snapshot, Parameters.Count - snapshot);
        }
    }

    private readonly record struct VisitResult(string? Sql, ConditionNode? Residual);

    private static VisitResult Visit(ConditionNode node, Context c)
    {
        int snap = c.Snapshot();
        var r = VisitCore(node, c);
        // Invariant: if Sql is null and the entire node is residual,
        // discard any parameters we speculatively bound.
        if (r.Sql is null) c.Rollback(snap);
        return r;
    }

    private static VisitResult VisitCore(ConditionNode node, Context c) => node switch
    {
        AndCondition and => VisitAnd(and, c),
        OrCondition or => VisitOr(or, c),
        NotCondition not => VisitNot(not, c),
        CompareCondition cc => VisitCompare(cc, c),
        BetweenCondition bt => VisitBetween(bt, c),
        InCondition inn => VisitIn(inn, c),
        AttributeExistsCondition ae => VisitAttributeExists(ae, c),
        AttributeNotExistsCondition ane => VisitAttributeNotExists(ane, c),
        AttributeTypeCondition at => VisitAttributeType(at, c),
        BeginsWithCondition bw => VisitBeginsWith(bw, c),
        ContainsCondition co => VisitContains(co, c),
        _ => new VisitResult(null, node),
    };

    // ---------------- boolean composition ---------------------------

    private static VisitResult VisitAnd(AndCondition and, Context c)
    {
        var l = Visit(and.Left, c);
        var r = Visit(and.Right, c);

        string? sql = (l.Sql, r.Sql) switch
        {
            (string ls, string rs) => $"({ls} AND {rs})",
            (string ls, null) => ls,
            (null, string rs) => rs,
            _ => null,
        };
        ConditionNode? residual = (l.Residual, r.Residual) switch
        {
            (null, null) => null,
            (ConditionNode lr, null) => lr,
            (null, ConditionNode rr) => rr,
            (ConditionNode lr, ConditionNode rr) => new AndCondition(lr, rr),
        };
        // If we ended up with no SQL at all, the whole AND is residual
        // → use the original node so Sql=null, Residual=and (rolled
        // back upstream).
        if (sql is null) return new VisitResult(null, and);
        return new VisitResult(sql, residual);
    }

    private static VisitResult VisitOr(OrCondition or, Context c)
    {
        // OR is only pushable when BOTH sides are fully pushable.
        // Pushing one side alone over-selects (the residual side could
        // still match items the SQL excluded), so we'd need a join with
        // the residual semantic — not worth the complexity here.
        var l = Visit(or.Left, c);
        if (l.Sql is null || l.Residual is not null) return new VisitResult(null, or);

        var r = Visit(or.Right, c);
        if (r.Sql is null || r.Residual is not null) return new VisitResult(null, or);

        return new VisitResult($"({l.Sql} OR {r.Sql})", null);
    }

    private static VisitResult VisitNot(NotCondition not, Context c)
    {
        var inner = Visit(not.Inner, c);
        // NOT is only pushable when the inner subtree is fully
        // pushable. NOT(pushed AND residual) is not equivalent to
        // NOT(pushed) AND NOT(residual) — De Morgan'd it'd be
        // NOT(pushed) OR NOT(residual), which fails the OR rule above.
        if (inner.Sql is null || inner.Residual is not null) return new VisitResult(null, not);
        return new VisitResult($"NOT({inner.Sql})", null);
    }

    // ---------------- comparisons -----------------------------------

    private static readonly Dictionary<CompareOp, string> SqlForOp = new()
    {
        [CompareOp.Equal] = "=",
        [CompareOp.NotEqual] = "!=",
        [CompareOp.Less] = "<",
        [CompareOp.LessEqual] = "<=",
        [CompareOp.Greater] = ">",
        [CompareOp.GreaterEqual] = ">=",
    };

    private static CompareOp FlipOp(CompareOp op) => op switch
    {
        CompareOp.Less => CompareOp.Greater,
        CompareOp.LessEqual => CompareOp.GreaterEqual,
        CompareOp.Greater => CompareOp.Less,
        CompareOp.GreaterEqual => CompareOp.LessEqual,
        _ => op, // Equal / NotEqual are symmetric
    };

    private static VisitResult VisitCompare(CompareCondition cc, Context c)
    {
        // Push only path-vs-value (or value-vs-path); path-vs-path
        // comparisons require Cosmos to dereference two attributes,
        // which the hybrid number branch makes combinatorial.
        if (cc.Left is ConditionPathOperand lp && cc.Right is ConditionValueOperand rv)
            return TryCompareValue(cc.Op, lp.Path, rv.Value, c, original: cc);
        if (cc.Right is ConditionPathOperand rp && cc.Left is ConditionValueOperand lv)
            return TryCompareValue(FlipOp(cc.Op), rp.Path, lv.Value, c, original: cc);
        return new VisitResult(null, cc);
    }

    private static VisitResult TryCompareValue(
        CompareOp op, DocumentPath path, ValueRefOperand value, Context c, ConditionNode original)
    {
        if (!ParsedAttributeValue.TryParse(value.Value, out var parsed))
            return new VisitResult(null, original);
        if (HasReservedSegment(path))
            return new VisitResult(null, original);

        // DDB `<>` is true when the attribute exists but has a different
        // type than the operand. The shape-specific SQL we emit per type
        // can't model that cross-type semantics safely (envelope-targeted
        // SQL on a row whose attribute is a different type returns
        // Undefined / false, dropping rows DDB would keep). Hand it back
        // to the client-side evaluator.
        if (op == CompareOp.NotEqual)
            return new VisitResult(null, original);

        var sqlOp = SqlForOp[op];
        var pathSql = CosmosPathTranslator.Translate(path, c.RootAlias);
        bool isOrdered = op is CompareOp.Less or CompareOp.LessEqual
            or CompareOp.Greater or CompareOp.GreaterEqual;

        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                return new VisitResult($"{pathSql} {sqlOp} {c.Bind(parsed.Value)}", null);

            case AttributeValueTypes.Bool:
                return new VisitResult($"{pathSql} {sqlOp} {c.Bind(parsed.Value)}", null);

            case AttributeValueTypes.Null:
                // NULL attribute is stored as JSON null in flat Cosmos.
                return new VisitResult($"{pathSql} {sqlOp} {c.Bind(JsonNull())}", null);

            case AttributeValueTypes.Number:
                return BuildNumberCompare(pathSql, sqlOp, parsed.Value.GetString()!, c, original, isOrdered);

            case AttributeValueTypes.Binary:
                // Binary stored as `{ "_a2a:B": "<b64>" }` envelope.
                // Equality on canonical base64 is safe; ordered
                // comparisons would order by base64 lexical bytes
                // rather than by underlying binary bytes — defer to
                // residual evaluation.
                if (isOrdered) return new VisitResult(null, original);
                var envBPath = pathSql + "[\"" + InferredAttributeStorage.EnvelopeTagB + "\"]";
                return new VisitResult($"{envBPath} {sqlOp} {c.Bind(parsed.Value)}", null);

            default:
                // Sets / Map / List comparisons aren't well-defined in
                // DDB for ordered ops anyway → residual.
                return new VisitResult(null, original);
        }
    }

    /// <summary>
    /// Emits the two-branch hybrid number comparison so the same SQL
    /// matches both bare and envelope storage shapes. The parameter
    /// is bound as a JSON number for both branches (Cosmos's
    /// StringToNumber returns a number, so the envelope branch
    /// compares number-to-number too). Returns residual when the
    /// parsed value exceeds IEEE 754 double round-trip safety —
    /// pushing it would compare via doubles and silently misclassify
    /// boundary items.
    ///
    /// <para>The SQL acts as a prefilter only — <c>StringToNumber</c>
    /// rounds the envelope payload to a double, so high-precision
    /// envelope-stored values can be falsely matched. The original
    /// node is preserved as residual so <see cref="ConditionEvaluator"/>
    /// re-evaluates the comparison against the exact canonical string.</para>
    ///
    /// <para>Ordered comparisons (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>,
    /// <c>&gt;=</c>) on the envelope branch must NOT use
    /// <c>StringToNumber</c>: it can produce *false negatives* — e.g.
    /// stored <c>9007199254740995</c> compared with param
    /// <c>9007199254740996</c> using <c>&lt;</c> rounds both to the
    /// same double and yields false, dropping a row DDB would keep.
    /// For ordered ops we widen the envelope branch to
    /// <c>IS_DEFINED(envPath)</c> so every envelope-stored row reaches
    /// the residual evaluator. Equality is safe to push through
    /// <c>StringToNumber</c> because envelope storage by construction
    /// only holds values that do not round-trip as bare JSON numbers,
    /// so an envelope-stored value can never exactly equal a
    /// round-trippable parameter.</para>
    /// </summary>
    private static VisitResult BuildNumberCompare(
        string pathSql, string sqlOp, string nText, Context c, ConditionNode original,
        bool isOrdered)
    {
        if (!TryParameterizeNumber(nText, out var paramElem))
            return new VisitResult(null, original);

        var p = c.Bind(paramElem);
        var envPath = pathSql + "[\"" + InferredAttributeStorage.EnvelopeTagN + "\"]";
        string envBranch = isOrdered
            ? $"IS_DEFINED({envPath})"
            : $"(IS_DEFINED({envPath}) AND StringToNumber({envPath}) {sqlOp} {p})";
        var sql =
            $"((IS_NUMBER({pathSql}) AND {pathSql} {sqlOp} {p})" +
            $" OR {envBranch})";
        return new VisitResult(sql, original);
    }

    private static VisitResult VisitBetween(BetweenCondition bt, Context c)
    {
        if (bt.Value is not ConditionPathOperand pv) return new VisitResult(null, bt);
        if (bt.Lower is not ConditionValueOperand lov) return new VisitResult(null, bt);
        if (bt.Upper is not ConditionValueOperand hiv) return new VisitResult(null, bt);
        if (!ParsedAttributeValue.TryParse(lov.Value.Value, out var lo)
            || !ParsedAttributeValue.TryParse(hiv.Value.Value, out var hi))
            return new VisitResult(null, bt);
        if (lo.TypeTag != hi.TypeTag) return new VisitResult(null, bt);
        if (HasReservedSegment(pv.Path)) return new VisitResult(null, bt);

        var pathSql = CosmosPathTranslator.Translate(pv.Path, c.RootAlias);

        switch (lo.TypeTag)
        {
            case AttributeValueTypes.String:
                var ls = c.Bind(lo.Value);
                var hs = c.Bind(hi.Value);
                return new VisitResult($"({pathSql} >= {ls} AND {pathSql} <= {hs})", null);

            case AttributeValueTypes.Number:
                if (!TryParameterizeNumber(lo.Value.GetString()!, out var loElem)
                    || !TryParameterizeNumber(hi.Value.GetString()!, out var hiElem))
                    return new VisitResult(null, bt);
                var lp = c.Bind(loElem);
                var hp = c.Bind(hiElem);
                var envPath = pathSql + "[\"" + InferredAttributeStorage.EnvelopeTagN + "\"]";
                // BETWEEN is inherently ordered. The envelope branch
                // must be a broad prefilter — StringToNumber rounds
                // through a double and can drop envelope rows that DDB
                // would keep (e.g. stored 9007199254740995 vs bound
                // 9007199254740996). Let every envelope row through;
                // the residual evaluator (preserved below) does the
                // exact canonical-string comparison.
                var sql =
                    $"((IS_NUMBER({pathSql}) AND {pathSql} >= {lp} AND {pathSql} <= {hp})" +
                    $" OR IS_DEFINED({envPath}))";
                return new VisitResult(sql, bt);

            case AttributeValueTypes.Binary:
                // BETWEEN on B would compare base64 strings lexically,
                // which is not equivalent to comparing the underlying
                // byte sequences. Leave it residual.
                return new VisitResult(null, bt);

            default:
                return new VisitResult(null, bt);
        }
    }

    private static VisitResult VisitIn(InCondition inn, Context c)
    {
        if (inn.Value is not ConditionPathOperand pv) return new VisitResult(null, inn);
        if (inn.Set.Count == 0) return new VisitResult(null, inn);
        if (HasReservedSegment(pv.Path)) return new VisitResult(null, inn);

        // All set members must be values and share one type tag.
        var parsedSet = new List<ParsedAttributeValue>(inn.Set.Count);
        string? expectedTag = null;
        foreach (var op in inn.Set)
        {
            if (op is not ConditionValueOperand vop) return new VisitResult(null, inn);
            if (!ParsedAttributeValue.TryParse(vop.Value.Value, out var parsed)) return new VisitResult(null, inn);
            expectedTag ??= parsed.TypeTag;
            if (parsed.TypeTag != expectedTag) return new VisitResult(null, inn);
            parsedSet.Add(parsed);
        }

        var pathSql = CosmosPathTranslator.Translate(pv.Path, c.RootAlias);

        switch (expectedTag)
        {
            case AttributeValueTypes.String:
            case AttributeValueTypes.Bool:
                {
                    var names = new List<string>(parsedSet.Count);
                    foreach (var p in parsedSet) names.Add(c.Bind(p.Value));
                    return new VisitResult($"{pathSql} IN ({string.Join(", ", names)})", null);
                }
            case AttributeValueTypes.Number:
                {
                    var names = new List<string>(parsedSet.Count);
                    foreach (var p in parsedSet)
                    {
                        if (!TryParameterizeNumber(p.Value.GetString()!, out var elem))
                            return new VisitResult(null, inn);
                        names.Add(c.Bind(elem));
                    }
                    var joined = string.Join(", ", names);
                    var envPath = pathSql + "[\"" + InferredAttributeStorage.EnvelopeTagN + "\"]";
                    // Prefilter only: hybrid envelope comparison rounds
                    // through a double via StringToNumber. Residual
                    // re-evaluates against the exact canonical string.
                    return new VisitResult(
                        $"((IS_NUMBER({pathSql}) AND {pathSql} IN ({joined}))" +
                        $" OR (IS_DEFINED({envPath}) AND StringToNumber({envPath}) IN ({joined})))",
                        inn);
                }
            default:
                return new VisitResult(null, inn);
        }
    }

    // ---------------- functions -------------------------------------

    private static VisitResult VisitAttributeExists(AttributeExistsCondition node, Context c)
    {
        if (HasReservedSegment(node.Path)) return new VisitResult(null, node);
        var pathSql = CosmosPathTranslator.Translate(node.Path, c.RootAlias);
        return new VisitResult($"IS_DEFINED({pathSql})", null);
    }

    private static VisitResult VisitAttributeNotExists(AttributeNotExistsCondition node, Context c)
    {
        if (HasReservedSegment(node.Path)) return new VisitResult(null, node);
        var pathSql = CosmosPathTranslator.Translate(node.Path, c.RootAlias);
        return new VisitResult($"NOT IS_DEFINED({pathSql})", null);
    }

    private static VisitResult VisitAttributeType(AttributeTypeCondition node, Context c)
    {
        if (!ParsedAttributeValue.TryParse(node.TypeTag.Value, out var tagVal)
            || tagVal.TypeTag != AttributeValueTypes.String)
            return new VisitResult(null, node);
        if (HasReservedSegment(node.Path)) return new VisitResult(null, node);

        var requested = tagVal.Value.GetString();
        var pathSql = CosmosPathTranslator.Translate(node.Path, c.RootAlias);

        // For types that are stored bare and whose Cosmos JSON kind is
        // unambiguous, we have a clean push. For the M case we'd need
        // to also exclude every envelope shape (otherwise SS/NS/BS/B
        // envelopes — which ARE Cosmos objects — would be matched as
        // M), so we leave M as a residual to keep the SQL small.
        string sql = requested switch
        {
            AttributeValueTypes.String => $"IS_STRING({pathSql})",
            AttributeValueTypes.Bool => $"IS_BOOL({pathSql})",
            AttributeValueTypes.Null => $"IS_NULL({pathSql})",
            AttributeValueTypes.List => $"IS_ARRAY({pathSql})",
            AttributeValueTypes.Number =>
                $"(IS_NUMBER({pathSql}) OR IS_DEFINED({pathSql}[\"{InferredAttributeStorage.EnvelopeTagN}\"]))",
            AttributeValueTypes.Binary =>
                $"IS_DEFINED({pathSql}[\"{InferredAttributeStorage.EnvelopeTagB}\"])",
            AttributeValueTypes.StringSet =>
                $"IS_DEFINED({pathSql}[\"{InferredAttributeStorage.EnvelopeTagSS}\"])",
            AttributeValueTypes.NumberSet =>
                $"IS_DEFINED({pathSql}[\"{InferredAttributeStorage.EnvelopeTagNS}\"])",
            AttributeValueTypes.BinarySet =>
                $"IS_DEFINED({pathSql}[\"{InferredAttributeStorage.EnvelopeTagBS}\"])",
            _ => "",
        };
        if (sql.Length == 0) return new VisitResult(null, node);
        return new VisitResult(sql, null);
    }

    private static VisitResult VisitBeginsWith(BeginsWithCondition node, Context c)
    {
        if (node.Path is not ConditionPathOperand pv) return new VisitResult(null, node);
        if (node.Prefix is not ConditionValueOperand pref) return new VisitResult(null, node);
        if (!ParsedAttributeValue.TryParse(pref.Value.Value, out var parsed)) return new VisitResult(null, node);
        if (HasReservedSegment(pv.Path)) return new VisitResult(null, node);

        var pathSql = CosmosPathTranslator.Translate(pv.Path, c.RootAlias);

        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                // false = case-sensitive, matching DDB semantics.
                return new VisitResult($"STARTSWITH({pathSql}, {c.Bind(parsed.Value)}, false)", null);

            case AttributeValueTypes.Binary:
                // Binary stored as `_a2a:B` (base64). For a single
                // base64 char run the substring prefix is meaningful;
                // for the general case base64-prefix != byte-prefix.
                // Residual is the safe choice.
                return new VisitResult(null, node);

            default:
                return new VisitResult(null, node);
        }
    }

    private static VisitResult VisitContains(ContainsCondition node, Context c)
    {
        if (node.Container is not ConditionPathOperand pv) return new VisitResult(null, node);
        if (node.Item is not ConditionValueOperand item) return new VisitResult(null, node);
        if (!ParsedAttributeValue.TryParse(item.Value.Value, out var parsed)) return new VisitResult(null, node);
        if (HasReservedSegment(pv.Path)) return new VisitResult(null, node);

        var pathSql = CosmosPathTranslator.Translate(pv.Path, c.RootAlias);

        // DDB `contains(container, :v)` also matches lists (type L)
        // whose members equal :v. Lists are stored in flat Cosmos as
        // bare JSON arrays whose elements are recursively encoded
        // (envelope-wrapped for N/B/sets, bare for S). To preserve
        // candidate rows whose container is a list we widen the SQL
        // with an IS_ARRAY(path) prefilter branch and fall back to
        // residual evaluation, which understands the typed element
        // semantics.
        switch (parsed.TypeTag)
        {
            case AttributeValueTypes.String:
                {
                    // String argument: contains can match an S
                    // attribute (substring), an SS attribute
                    // (membership) or an L containing the string.
                    var pName = c.Bind(parsed.Value);
                    var ssPath = pathSql + "[\"" + InferredAttributeStorage.EnvelopeTagSS + "\"]";
                    var sql =
                        $"((IS_STRING({pathSql}) AND CONTAINS({pathSql}, {pName}, false))" +
                        $" OR (IS_ARRAY({ssPath}) AND ARRAY_CONTAINS({ssPath}, {pName}))" +
                        $" OR IS_ARRAY({pathSql}))";
                    return new VisitResult(sql, node);
                }
            case AttributeValueTypes.Number:
                {
                    // Numeric argument: matches an NS attribute
                    // (members stored as canonical strings) or an L
                    // containing the same number. Falls back to
                    // residual if the numeric input is malformed.
                    var nsPath = pathSql + "[\"" + InferredAttributeStorage.EnvelopeTagNS + "\"]";
                    if (!InferredAttributeStorage.TryNormalizeDdbNumber(
                        parsed.Value.GetString() ?? string.Empty,
                        out var canonical, out _, out _))
                    {
                        return new VisitResult(null, node);
                    }
                    var pName = c.Bind(JsonString(canonical));
                    return new VisitResult(
                        $"((IS_ARRAY({nsPath}) AND ARRAY_CONTAINS({nsPath}, {pName}))" +
                        $" OR IS_ARRAY({pathSql}))", node);
                }
            case AttributeValueTypes.Binary:
                {
                    // Binary argument: matches a BS attribute (members
                    // stored as base64 strings) or an L containing the
                    // same binary value.
                    var bsPath = pathSql + "[\"" + InferredAttributeStorage.EnvelopeTagBS + "\"]";
                    var pName = c.Bind(parsed.Value);
                    return new VisitResult(
                        $"((IS_ARRAY({bsPath}) AND ARRAY_CONTAINS({bsPath}, {pName}))" +
                        $" OR IS_ARRAY({pathSql}))", node);
                }
            default:
                return new VisitResult(null, node);
        }
    }

    // ---------------- value helpers ---------------------------------

    /// <summary>
    /// Rejects paths that address any reserved <c>_a2a:</c> envelope
    /// key (e.g. a user-supplied <c>#tag</c> aliased to
    /// <c>"_a2a:N"</c>). The flat-Cosmos rewrite reserves the prefix
    /// for storage internals; routing user-controlled paths into
    /// envelope keys would let callers query metadata that DDB never
    /// exposes. This must be checked at the visitor — pushing such a
    /// path would emit SQL that addresses storage internals; without
    /// this guard, the only protection is the encoder rejecting the
    /// name on write, which doesn't help on reads.
    /// </summary>
    private static bool HasReservedSegment(DocumentPath path)
    {
        foreach (var seg in path.Segments)
        {
            if (seg is AttributePathSegment a
                && a.Name.StartsWith(InferredAttributeStorage.EnvelopeTagPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tries to bind a DDB N-string to a Cosmos JSON-number parameter.
    /// Returns false when the input is malformed (rejected at parse
    /// time anyway by the encoder) or when the canonical form exceeds
    /// IEEE 754 double round-trip safety — in that case pushing the
    /// comparison would silently round in the parameter binding, and
    /// we defer to client-side evaluation against the (preserved)
    /// envelope string.
    /// </summary>
    private static bool TryParameterizeNumber(string nText, out JsonElement element)
    {
        element = default;
        if (!InferredAttributeStorage.TryNormalizeDdbNumber(
            nText, out var canonical, out _, out _))
        {
            return false;
        }
        if (!InferredAttributeStorage.CanRoundTripAsBareJsonNumber(canonical))
        {
            // High-precision / large-magnitude — would lose precision
            // crossing the JSON number parameter boundary. Leave it
            // for the client-side evaluator (which has the exact
            // canonical string).
            return false;
        }
        element = ParseJsonValue(canonical);
        return true;
    }

    private static JsonElement ParseJsonValue(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement JsonNull() => ParseJsonValue("null");

    private static JsonElement JsonString(string s)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStringValue(s);
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }
}
