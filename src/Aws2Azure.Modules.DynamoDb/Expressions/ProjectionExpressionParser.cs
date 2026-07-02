using System;
using System.Collections.Generic;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// Parses a DynamoDB <c>ProjectionExpression</c> into a compiled
/// <see cref="Projection"/>. Supports a comma-separated list of document paths:
/// top-level attribute names, <c>#alias</c> references, nested map members
/// (<c>a.b</c>) and list indices (<c>a[0]</c>). Overlapping paths are rejected.
/// </summary>
internal static class ProjectionExpressionParser
{
    public static Projection Parse(
        string expression, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ExpressionSyntaxException(0, "ProjectionExpression cannot be empty.");

        var tokens = ExpressionLexer.Tokenise(expression);
        var paths = new List<DocumentPath>();
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
            paths.Add(path);

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

        return Projection.FromDocumentPaths(paths);
    }
}
