using System;
using System.Collections.Generic;
using System.Text;
using Aws2Azure.Modules.DynamoDb.Expressions;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class QueryHandler
{
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
}
