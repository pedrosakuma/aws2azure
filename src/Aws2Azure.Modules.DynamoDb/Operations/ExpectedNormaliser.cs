using System;
using System.Collections.Generic;
using System.Text.Json;
using Aws2Azure.Modules.DynamoDb.Expressions;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Translates DynamoDB's legacy <c>Expected</c> / <c>ConditionalOperator</c>
/// shape into a modern <see cref="ConditionNode"/> AST so the rest of
/// the proxy can speak a single language. Supports the comparison
/// operators that legacy callers actually emit; unsupported operators
/// fail loud with <see cref="ExpressionSyntaxException"/> which the
/// outer handler maps to <c>ValidationException</c>.
///
/// <para>The legacy shape is:</para>
/// <code>
/// "Expected": {
///   "attrName": {
///     "Exists": true|false,
///     "Value": { "S": "..." },                  // implicit EQ
///     "ComparisonOperator": "EQ|NE|LT|LE|GT|GE|...",
///     "AttributeValueList": [ { ... } ]
///   }
/// }
/// </code>
/// </summary>
internal static class ExpectedNormaliser
{
    public static ConditionNode? Build(JsonElement? expected, string? conditionalOperator)
    {
        if (expected is null || expected.Value.ValueKind != JsonValueKind.Object) return null;

        var clauses = new List<ConditionNode>();
        int placeholder = 0;
        string Next() => $":__exp{placeholder++}";

        foreach (var prop in expected.Value.EnumerateObject())
        {
            clauses.Add(BuildEntry(prop.Name, prop.Value, Next));
        }

        if (clauses.Count == 0) return null;

        var combine = (conditionalOperator ?? "AND").ToUpperInvariant();
        if (combine != "AND" && combine != "OR")
            throw new ExpressionSyntaxException(0,
                $"ConditionalOperator must be AND or OR; got '{conditionalOperator}'.");

        var result = clauses[0];
        for (int i = 1; i < clauses.Count; i++)
        {
            result = combine == "AND"
                ? new AndCondition(result, clauses[i])
                : new OrCondition(result, clauses[i]);
        }
        return result;
    }

    private static ConditionNode BuildEntry(string attrName, JsonElement spec, Func<string> nextPlaceholder)
    {
        var path = new DocumentPath(new[] { (PathSegment)new AttributePathSegment(attrName) });

        // Exists-only branch
        if (spec.TryGetProperty("Exists", out var existsElement)
            && existsElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && !spec.TryGetProperty("Value", out _)
            && !spec.TryGetProperty("ComparisonOperator", out _)
            && !spec.TryGetProperty("AttributeValueList", out _))
        {
            return existsElement.GetBoolean()
                ? new AttributeExistsCondition(path)
                : new AttributeNotExistsCondition(path);
        }

        var compareOp = spec.TryGetProperty("ComparisonOperator", out var coElem) && coElem.ValueKind == JsonValueKind.String
            ? coElem.GetString()!.ToUpperInvariant()
            : "EQ";

        // Collect operand list. Legacy supports either AttributeValueList
        // (preferred) or a single Value (implicit EQ).
        var values = new List<JsonElement>();
        if (spec.TryGetProperty("AttributeValueList", out var avl) && avl.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in avl.EnumerateArray()) values.Add(v);
        }
        else if (spec.TryGetProperty("Value", out var v))
        {
            values.Add(v);
        }

        ConditionOperand PathOperand() => new ConditionPathOperand(path);
        ConditionOperand ValueOperand(int i) =>
            new ConditionValueOperand(new ValueRefOperand(nextPlaceholder(), values[i]));

        switch (compareOp)
        {
            case "EQ":
                Require(1);
                return new CompareCondition(CompareOp.Equal, PathOperand(), ValueOperand(0));
            case "NE":
                Require(1);
                return new CompareCondition(CompareOp.NotEqual, PathOperand(), ValueOperand(0));
            case "LT":
                Require(1);
                return new CompareCondition(CompareOp.Less, PathOperand(), ValueOperand(0));
            case "LE":
                Require(1);
                return new CompareCondition(CompareOp.LessEqual, PathOperand(), ValueOperand(0));
            case "GT":
                Require(1);
                return new CompareCondition(CompareOp.Greater, PathOperand(), ValueOperand(0));
            case "GE":
                Require(1);
                return new CompareCondition(CompareOp.GreaterEqual, PathOperand(), ValueOperand(0));
            case "BETWEEN":
                Require(2);
                return new BetweenCondition(PathOperand(), ValueOperand(0), ValueOperand(1));
            case "IN":
            {
                if (values.Count == 0)
                    throw new ExpressionSyntaxException(0, $"Expected['{attrName}'].IN requires at least one value.");
                var set = new List<ConditionOperand>(values.Count);
                for (int i = 0; i < values.Count; i++) set.Add(ValueOperand(i));
                return new InCondition(PathOperand(), set);
            }
            case "BEGINS_WITH":
                Require(1);
                return new BeginsWithCondition(PathOperand(), ValueOperand(0));
            case "CONTAINS":
                Require(1);
                return new ContainsCondition(PathOperand(), ValueOperand(0));
            case "NOT_CONTAINS":
                Require(1);
                return new NotCondition(new ContainsCondition(PathOperand(), ValueOperand(0)));
            case "NULL":
                return new AttributeNotExistsCondition(path);
            case "NOT_NULL":
                return new AttributeExistsCondition(path);
            default:
                throw new ExpressionSyntaxException(0,
                    $"Unsupported legacy ComparisonOperator '{compareOp}' on attribute '{attrName}'.");
        }

        void Require(int count)
        {
            if (values.Count != count)
                throw new ExpressionSyntaxException(0,
                    $"Expected['{attrName}'].{compareOp} requires exactly {count} value(s); got {values.Count}.");
        }
    }
}
