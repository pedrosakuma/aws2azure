using System;
using System.Collections.Generic;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// Parses a DynamoDB <c>ProjectionExpression</c> into a list of
/// top-level attribute names. This slice supports the common subset
/// only: a comma-separated list of attribute names or <c>#alias</c>
/// references. Nested paths (<c>a.b</c>, <c>a[0]</c>) are deferred.
/// </summary>
internal static class ProjectionExpressionParser
{
    public static IReadOnlyList<string> Parse(
        string expression, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ExpressionSyntaxException(0, "ProjectionExpression cannot be empty.");

        var parts = expression.Split(',');
        var result = new List<string>(parts.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int offset = 0;
        foreach (var rawPart in parts)
        {
            var trimmed = rawPart.Trim();
            if (trimmed.Length == 0)
                throw new ExpressionSyntaxException(offset, "Empty path in ProjectionExpression.");

            string resolved;
            if (trimmed[0] == '#')
            {
                if (names is null || !names.TryGetValue(trimmed, out var alias))
                    throw new ExpressionSyntaxException(offset,
                        $"ExpressionAttributeNames is missing alias '{trimmed}'.");
                resolved = alias;
            }
            else
            {
                foreach (var ch in trimmed)
                {
                    if (ch == '.' || ch == '[' || ch == ']')
                        throw new ExpressionSyntaxException(offset,
                            $"Nested path '{trimmed}' is not supported in this release; use a top-level attribute or a #alias.");
                }
                resolved = trimmed;
            }
            if (seen.Add(resolved))
            {
                result.Add(resolved);
            }
            offset += rawPart.Length + 1;
        }
        return result;
    }
}
