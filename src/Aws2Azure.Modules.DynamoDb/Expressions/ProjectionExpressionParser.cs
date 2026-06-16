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

        var tokens = ExpressionLexer.Tokenise(expression);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;
        while (ExpressionPathParser.Peek(tokens, position).Kind != TokenKind.EndOfInput)
        {
            var start = ExpressionPathParser.Peek(tokens, position);
            if (start.Kind == TokenKind.Comma)
                throw new ExpressionSyntaxException(start.Position, "Empty path in ProjectionExpression.");

            var path = ExpressionPathParser.ParsePath(
                tokens,
                ref position,
                names,
                AttributeAliasErrorStyle.Projection);
            if (!path.IsTopLevel)
                throw new ExpressionSyntaxException(start.Position,
                    $"Nested path '{path.Display}' is not supported in this release; use a top-level attribute or a #alias.");

            string resolved = path.Root;
            if (seen.Add(resolved))
            {
                result.Add(resolved);
            }

            var separator = ExpressionPathParser.Peek(tokens, position);
            if (separator.Kind == TokenKind.EndOfInput)
            {
                break;
            }
            if (separator.Kind != TokenKind.Comma)
            {
                throw new ExpressionSyntaxException(separator.Position,
                    "Expected ',' between ProjectionExpression paths.");
            }

            position++;
            if (ExpressionPathParser.Peek(tokens, position).Kind == TokenKind.EndOfInput)
            {
                throw new ExpressionSyntaxException(
                    ExpressionPathParser.Peek(tokens, position).Position,
                    "Empty path in ProjectionExpression.");
            }
        }
        return result;
    }
}
