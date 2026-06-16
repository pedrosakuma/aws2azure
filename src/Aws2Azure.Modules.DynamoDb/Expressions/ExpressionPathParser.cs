using System.Collections.Generic;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

internal enum AttributeAliasErrorStyle
{
    Expression,
    Projection,
}

internal static class ExpressionPathParser
{
    public static DocumentPath ParsePath(
        IReadOnlyList<ExpressionToken> tokens,
        ref int position,
        IReadOnlyDictionary<string, string>? names,
        AttributeAliasErrorStyle aliasErrorStyle = AttributeAliasErrorStyle.Expression)
    {
        var segments = new List<PathSegment>();
        var first = Peek(tokens, position);
        if (first.Kind == TokenKind.Identifier)
        {
            segments.Add(new AttributePathSegment(first.Text));
            position++;
        }
        else if (first.Kind == TokenKind.AttributeNameRef)
        {
            segments.Add(new AttributePathSegment(ResolveAttributeName(first, names, aliasErrorStyle)));
            position++;
        }
        else
        {
            throw Error(tokens, position, "Expected an attribute name or '#alias' to start a document path.");
        }

        while (true)
        {
            var t = Peek(tokens, position);
            if (t.Kind == TokenKind.Dot)
            {
                position++;
                var seg = Peek(tokens, position);
                if (seg.Kind == TokenKind.Identifier)
                {
                    segments.Add(new AttributePathSegment(seg.Text));
                    position++;
                }
                else if (seg.Kind == TokenKind.AttributeNameRef)
                {
                    segments.Add(new AttributePathSegment(ResolveAttributeName(seg, names, aliasErrorStyle)));
                    position++;
                }
                else
                {
                    throw Error(tokens, position, "Expected an attribute name or '#alias' after '.'.");
                }
            }
            else if (t.Kind == TokenKind.LBracket)
            {
                position++;
                var numTok = Peek(tokens, position);
                if (numTok.Kind != TokenKind.Number)
                {
                    throw Error(tokens, position, "Expected a non-negative integer index inside '[ ]'.");
                }

                if (!int.TryParse(numTok.Text, out var idx) || idx < 0)
                {
                    throw Error(tokens, position, $"Invalid list index '{numTok.Text}'.");
                }

                position++;
                if (Peek(tokens, position).Kind != TokenKind.RBracket)
                {
                    throw Error(tokens, position, "Expected ']' to close list index.");
                }

                position++;
                segments.Add(new IndexPathSegment(idx));
            }
            else
            {
                break;
            }
        }

        return new DocumentPath(segments);
    }

    public static string ResolveAttributeName(
        ExpressionToken token,
        IReadOnlyDictionary<string, string>? names,
        AttributeAliasErrorStyle aliasErrorStyle = AttributeAliasErrorStyle.Expression)
    {
        if (names is not null && names.TryGetValue(token.Text, out var resolved))
        {
            return resolved;
        }

        string message = aliasErrorStyle == AttributeAliasErrorStyle.Projection
            ? $"ExpressionAttributeNames is missing alias '{token.Text}'."
            : $"An expression attribute name used in expression is not defined; attribute name: {token.Text}";
        throw new ExpressionSyntaxException(token.Position, message);
    }

    public static ValueRefOperand ResolveValueRef(
        ExpressionToken token,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null || !values.TryGetValue(token.Text, out var resolved))
        {
            throw new ExpressionSyntaxException(token.Position,
                $"An expression attribute value used in expression is not defined; attribute value: {token.Text}");
        }

        return new ValueRefOperand(token.Text, resolved);
    }

    public static ExpressionToken Peek(IReadOnlyList<ExpressionToken> tokens, int position)
        => tokens[position];

    public static ExpressionSyntaxException Error(
        IReadOnlyList<ExpressionToken> tokens,
        int position,
        string message)
    {
        var token = position < tokens.Count ? tokens[position] : tokens[^1];
        return new ExpressionSyntaxException(token.Position, message);
    }
}
