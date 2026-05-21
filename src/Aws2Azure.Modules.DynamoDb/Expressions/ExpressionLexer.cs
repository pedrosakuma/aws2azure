using System;
using System.Collections.Generic;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// Tokens produced by <see cref="ExpressionLexer"/>. Covers the full
/// DynamoDB expression grammar so the same lexer can power
/// UpdateExpression (this slice) and the boolean Condition / Filter /
/// KeyCondition grammars in later slices. <see cref="Comma"/>,
/// <see cref="LParen"/>, <see cref="RParen"/>, <see cref="LBracket"/>,
/// <see cref="RBracket"/> and <see cref="Dot"/> drive path / argument
/// structure; comparison and boolean operators are tokenised here even
/// though UpdateExpression only consumes <see cref="Equals"/>,
/// <see cref="Plus"/>, and <see cref="Minus"/>.
/// </summary>
internal enum TokenKind
{
    EndOfInput,

    Identifier,         // bare name
    AttributeNameRef,   // #name
    AttributeValueRef,  // :name
    Number,             // bare integer (only used for [index] segments)

    Comma,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Dot,

    Equals,             // =
    NotEquals,          // <>
    Less,               // <
    LessEquals,         // <=
    Greater,            // >
    GreaterEquals,      // >=
    Plus,
    Minus,

    // Reserved words returned as Identifier — callers compare case-
    // insensitively. This keeps the lexer schema-agnostic.
}

internal readonly record struct ExpressionToken(TokenKind Kind, string Text, int Position);

/// <summary>
/// Tokenises a DynamoDB expression string. Produces a flat list of
/// <see cref="ExpressionToken"/>s including a trailing
/// <see cref="TokenKind.EndOfInput"/>. The lexer is whitespace-skipping,
/// case-preserving (so callers see the original casing of identifiers)
/// and case-sensitive for sigils (<c>#</c>/<c>:</c>). Errors throw
/// <see cref="ExpressionSyntaxException"/> with the offending offset.
/// </summary>
internal static class ExpressionLexer
{
    public static List<ExpressionToken> Tokenise(string expression)
    {
        if (expression is null) throw new ArgumentNullException(nameof(expression));
        var tokens = new List<ExpressionToken>();
        var span = expression.AsSpan();
        int pos = 0;

        while (pos < span.Length)
        {
            char c = span[pos];

            if (char.IsWhiteSpace(c)) { pos++; continue; }

            int start = pos;
            switch (c)
            {
                case ',': tokens.Add(new ExpressionToken(TokenKind.Comma, ",", start)); pos++; continue;
                case '(': tokens.Add(new ExpressionToken(TokenKind.LParen, "(", start)); pos++; continue;
                case ')': tokens.Add(new ExpressionToken(TokenKind.RParen, ")", start)); pos++; continue;
                case '[': tokens.Add(new ExpressionToken(TokenKind.LBracket, "[", start)); pos++; continue;
                case ']': tokens.Add(new ExpressionToken(TokenKind.RBracket, "]", start)); pos++; continue;
                case '.': tokens.Add(new ExpressionToken(TokenKind.Dot, ".", start)); pos++; continue;
                case '+': tokens.Add(new ExpressionToken(TokenKind.Plus, "+", start)); pos++; continue;
                case '-': tokens.Add(new ExpressionToken(TokenKind.Minus, "-", start)); pos++; continue;
                case '=': tokens.Add(new ExpressionToken(TokenKind.Equals, "=", start)); pos++; continue;
                case '<':
                    if (pos + 1 < span.Length && span[pos + 1] == '=')
                    {
                        tokens.Add(new ExpressionToken(TokenKind.LessEquals, "<=", start)); pos += 2;
                    }
                    else if (pos + 1 < span.Length && span[pos + 1] == '>')
                    {
                        tokens.Add(new ExpressionToken(TokenKind.NotEquals, "<>", start)); pos += 2;
                    }
                    else
                    {
                        tokens.Add(new ExpressionToken(TokenKind.Less, "<", start)); pos++;
                    }
                    continue;
                case '>':
                    if (pos + 1 < span.Length && span[pos + 1] == '=')
                    {
                        tokens.Add(new ExpressionToken(TokenKind.GreaterEquals, ">=", start)); pos += 2;
                    }
                    else
                    {
                        tokens.Add(new ExpressionToken(TokenKind.Greater, ">", start)); pos++;
                    }
                    continue;
                case '#':
                {
                    pos++;
                    int nameStart = pos;
                    while (pos < span.Length && IsIdentChar(span[pos])) pos++;
                    if (pos == nameStart)
                        throw new ExpressionSyntaxException(start, "Expected identifier after '#'.");
                    tokens.Add(new ExpressionToken(TokenKind.AttributeNameRef, "#" + span.Slice(nameStart, pos - nameStart).ToString(), start));
                    continue;
                }
                case ':':
                {
                    pos++;
                    int nameStart = pos;
                    while (pos < span.Length && IsIdentChar(span[pos])) pos++;
                    if (pos == nameStart)
                        throw new ExpressionSyntaxException(start, "Expected identifier after ':'.");
                    tokens.Add(new ExpressionToken(TokenKind.AttributeValueRef, ":" + span.Slice(nameStart, pos - nameStart).ToString(), start));
                    continue;
                }
            }

            if (char.IsDigit(c))
            {
                // Integer literals only — used inside list-index segments
                // like [0]. Decimal / signed numbers live in placeholders.
                while (pos < span.Length && char.IsDigit(span[pos])) pos++;
                tokens.Add(new ExpressionToken(TokenKind.Number, span.Slice(start, pos - start).ToString(), start));
                continue;
            }

            if (IsIdentStartChar(c))
            {
                while (pos < span.Length && IsIdentChar(span[pos])) pos++;
                tokens.Add(new ExpressionToken(TokenKind.Identifier, span.Slice(start, pos - start).ToString(), start));
                continue;
            }

            throw new ExpressionSyntaxException(pos,
                $"Unexpected character '{c}' in expression at position {pos}.");
        }

        tokens.Add(new ExpressionToken(TokenKind.EndOfInput, string.Empty, pos));
        return tokens;
    }

    private static bool IsIdentStartChar(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

/// <summary>
/// Thrown by lexer / parser / evaluator on malformed expressions. The
/// outer handler converts this to a DynamoDB
/// <c>ValidationException</c>.
/// </summary>
internal sealed class ExpressionSyntaxException : Exception
{
    public int Position { get; }
    public ExpressionSyntaxException(int position, string message) : base(message)
    {
        Position = position;
    }
}
