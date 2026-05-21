using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// Parses the full DynamoDB UpdateExpression grammar:
///
/// <code>
/// updateExpression := clause (clause)*
/// clause           := setClause | removeClause | addClause | deleteClause
/// setClause        := 'SET' setAction (',' setAction)*
/// setAction        := path '=' operand
/// operand          := arithmeticOperand
/// arithmeticOperand := unary (('+'|'-') unary)?       -- DDB allows ONE +/- per assignment
/// unary            := primary
/// primary          := functionCall | path | valueRef
/// functionCall     := 'if_not_exists' '(' path ',' operand ')'
///                   | 'list_append'   '(' operand ',' operand ')'
/// removeClause     := 'REMOVE' path (',' path)*
/// addClause        := 'ADD' path valueRef (',' path valueRef)*
/// deleteClause     := 'DELETE' path valueRef (',' path valueRef)*
/// path             := pathSegment ('.' pathSegment | '[' INT ']')*
/// pathSegment      := IDENT | '#' IDENT
/// valueRef         := ':' IDENT
/// </code>
///
/// <para>Resolves <c>#name</c> ↔ <c>ExpressionAttributeNames</c> and
/// <c>:value</c> ↔ <c>ExpressionAttributeValues</c> during parsing so
/// every node in the AST is fully bound. Unused / undeclared
/// placeholders raise <see cref="ExpressionSyntaxException"/> which the
/// handler converts to <c>ValidationException</c>.</para>
///
/// <para>Arithmetic is restricted to a single <c>+</c> or <c>-</c> per
/// SET right-hand side per the DynamoDB grammar — <c>a = b + c + d</c>
/// is rejected with the same error message AWS emits.</para>
/// </summary>
internal sealed class UpdateExpressionParser
{
    private readonly List<ExpressionToken> _tokens;
    private readonly IReadOnlyDictionary<string, string>? _names;
    private readonly IReadOnlyDictionary<string, JsonElement>? _values;
    private int _pos;

    private UpdateExpressionParser(
        List<ExpressionToken> tokens,
        IReadOnlyDictionary<string, string>? names,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        _tokens = tokens;
        _names = names;
        _values = values;
    }

    public static UpdateExpressionAst Parse(
        string expression,
        IReadOnlyDictionary<string, string>? expressionAttributeNames,
        IReadOnlyDictionary<string, JsonElement>? expressionAttributeValues)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ExpressionSyntaxException(0, "UpdateExpression cannot be empty.");

        var tokens = ExpressionLexer.Tokenise(expression);
        var parser = new UpdateExpressionParser(tokens, expressionAttributeNames, expressionAttributeValues);
        return parser.ParseRoot();
    }

    private UpdateExpressionAst ParseRoot()
    {
        SetClause? setClause = null;
        RemoveClause? removeClause = null;
        AddClause? addClause = null;
        DeleteClause? deleteClause = null;

        while (Peek().Kind != TokenKind.EndOfInput)
        {
            var keyword = ExpectClauseKeyword();
            switch (keyword)
            {
                case "SET":
                    if (setClause is not null) throw Error("The UpdateExpression has more than one SET clause.");
                    setClause = ParseSetClause();
                    break;
                case "REMOVE":
                    if (removeClause is not null) throw Error("The UpdateExpression has more than one REMOVE clause.");
                    removeClause = ParseRemoveClause();
                    break;
                case "ADD":
                    if (addClause is not null) throw Error("The UpdateExpression has more than one ADD clause.");
                    addClause = ParseAddClause();
                    break;
                case "DELETE":
                    if (deleteClause is not null) throw Error("The UpdateExpression has more than one DELETE clause.");
                    deleteClause = ParseDeleteClause();
                    break;
                default:
                    throw Error($"Unknown UpdateExpression clause: {keyword}. Expected SET, REMOVE, ADD, or DELETE.");
            }
        }

        if (setClause is null && removeClause is null && addClause is null && deleteClause is null)
            throw new ExpressionSyntaxException(0, "UpdateExpression must contain at least one SET, REMOVE, ADD, or DELETE clause.");

        return new UpdateExpressionAst(setClause, removeClause, addClause, deleteClause);
    }

    private string ExpectClauseKeyword()
    {
        var t = Peek();
        if (t.Kind != TokenKind.Identifier)
            throw Error("Expected SET, REMOVE, ADD, or DELETE.");
        var upper = t.Text.ToUpperInvariant();
        if (upper is "SET" or "REMOVE" or "ADD" or "DELETE")
        {
            _pos++;
            return upper;
        }
        throw Error($"Expected SET, REMOVE, ADD, or DELETE; got '{t.Text}'.");
    }

    private SetClause ParseSetClause()
    {
        var actions = new List<SetAction>();
        while (true)
        {
            var path = ParsePath();
            if (Peek().Kind != TokenKind.Equals)
                throw Error($"Expected '=' after path '{path.Display}'.");
            _pos++; // consume '='
            var value = ParseOperand(allowArithmetic: true);
            actions.Add(new SetAction(path, value));
            if (Peek().Kind == TokenKind.Comma) { _pos++; continue; }
            break;
        }
        return new SetClause(actions);
    }

    private RemoveClause ParseRemoveClause()
    {
        var paths = new List<DocumentPath>();
        while (true)
        {
            paths.Add(ParsePath());
            if (Peek().Kind == TokenKind.Comma) { _pos++; continue; }
            break;
        }
        return new RemoveClause(paths);
    }

    private AddClause ParseAddClause()
    {
        var actions = new List<AddAction>();
        while (true)
        {
            var path = ParsePath();
            if (Peek().Kind != TokenKind.AttributeValueRef)
                throw Error($"ADD action for '{path.Display}' requires a :value reference.");
            var v = ResolveValueRef(Peek());
            _pos++;
            actions.Add(new AddAction(path, v));
            if (Peek().Kind == TokenKind.Comma) { _pos++; continue; }
            break;
        }
        return new AddClause(actions);
    }

    private DeleteClause ParseDeleteClause()
    {
        var actions = new List<DeleteAction>();
        while (true)
        {
            var path = ParsePath();
            if (Peek().Kind != TokenKind.AttributeValueRef)
                throw Error($"DELETE action for '{path.Display}' requires a :value reference.");
            var v = ResolveValueRef(Peek());
            _pos++;
            actions.Add(new DeleteAction(path, v));
            if (Peek().Kind == TokenKind.Comma) { _pos++; continue; }
            break;
        }
        return new DeleteClause(actions);
    }

    // SET RHS. DynamoDB allows exactly one '+' or '-' between two
    // operands; chains like 'a + b + c' are rejected by AWS.
    private ValueOperand ParseOperand(bool allowArithmetic)
    {
        var left = ParsePrimary();
        if (!allowArithmetic) return left;
        var k = Peek().Kind;
        if (k == TokenKind.Plus || k == TokenKind.Minus)
        {
            var op = k == TokenKind.Plus ? ArithmeticOp.Add : ArithmeticOp.Subtract;
            _pos++;
            var right = ParsePrimary();
            // Reject chains like a + b + c.
            var next = Peek().Kind;
            if (next == TokenKind.Plus || next == TokenKind.Minus)
                throw Error("Only one '+' or '-' allowed per SET assignment.");
            return new ArithmeticOperand(op, left, right);
        }
        return left;
    }

    private ValueOperand ParsePrimary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.AttributeValueRef:
            {
                var resolved = ResolveValueRef(t);
                _pos++;
                return resolved;
            }
            case TokenKind.Identifier:
            {
                // Could be a function call (if_not_exists / list_append)
                // or a bare path (e.g. 'attr.sub').
                if (IsFunctionCall(t.Text))
                {
                    return ParseFunctionCall(t.Text);
                }
                return new PathOperand(ParsePath());
            }
            case TokenKind.AttributeNameRef:
                return new PathOperand(ParsePath());
            default:
                throw Error($"Unexpected token '{t.Text}' at position {t.Position} in SET assignment value.");
        }
    }

    private static bool IsFunctionCall(string identifier)
        => identifier.Equals("if_not_exists", StringComparison.OrdinalIgnoreCase)
        || identifier.Equals("list_append", StringComparison.OrdinalIgnoreCase);

    private ValueOperand ParseFunctionCall(string functionIdentifier)
    {
        // Token currently at the function name.
        _pos++;
        if (Peek().Kind != TokenKind.LParen)
            throw Error($"Expected '(' after function '{functionIdentifier}'.");
        _pos++;

        if (functionIdentifier.Equals("if_not_exists", StringComparison.OrdinalIgnoreCase))
        {
            // if_not_exists(path, operand)
            var path = ParsePath();
            if (Peek().Kind != TokenKind.Comma)
                throw Error("if_not_exists requires two arguments: a path and a fallback operand.");
            _pos++;
            var fallback = ParseOperand(allowArithmetic: false);
            if (Peek().Kind != TokenKind.RParen)
                throw Error("Expected ')' to close if_not_exists.");
            _pos++;
            return new IfNotExistsOperand(path, fallback);
        }
        else
        {
            // list_append(operand, operand) — either arg may be a path or :value
            var left = ParseOperand(allowArithmetic: false);
            if (Peek().Kind != TokenKind.Comma)
                throw Error("list_append requires two operand arguments.");
            _pos++;
            var right = ParseOperand(allowArithmetic: false);
            if (Peek().Kind != TokenKind.RParen)
                throw Error("Expected ')' to close list_append.");
            _pos++;
            return new ListAppendOperand(left, right);
        }
    }

    private DocumentPath ParsePath()
    {
        var segments = new List<PathSegment>();
        var first = Peek();
        if (first.Kind == TokenKind.Identifier)
        {
            // Reserved-word guard: AWS would reject e.g. SET status = :v
            // ("Attribute name is a reserved keyword"). The full list is
            // large; defer per-keyword checking until Slice 4 where it
            // matters more (FilterExpression).
            segments.Add(new AttributePathSegment(first.Text));
            _pos++;
        }
        else if (first.Kind == TokenKind.AttributeNameRef)
        {
            segments.Add(new AttributePathSegment(ResolveAttributeName(first)));
            _pos++;
        }
        else
        {
            throw Error("Expected an attribute name or '#alias' to start a document path.");
        }

        while (true)
        {
            var t = Peek();
            if (t.Kind == TokenKind.Dot)
            {
                _pos++;
                var seg = Peek();
                if (seg.Kind == TokenKind.Identifier)
                {
                    segments.Add(new AttributePathSegment(seg.Text));
                    _pos++;
                }
                else if (seg.Kind == TokenKind.AttributeNameRef)
                {
                    segments.Add(new AttributePathSegment(ResolveAttributeName(seg)));
                    _pos++;
                }
                else
                {
                    throw Error("Expected an attribute name or '#alias' after '.'.");
                }
            }
            else if (t.Kind == TokenKind.LBracket)
            {
                _pos++;
                var numTok = Peek();
                if (numTok.Kind != TokenKind.Number)
                    throw Error("Expected a non-negative integer index inside '[ ]'.");
                if (!int.TryParse(numTok.Text, out var idx) || idx < 0)
                    throw Error($"Invalid list index '{numTok.Text}'.");
                _pos++;
                if (Peek().Kind != TokenKind.RBracket)
                    throw Error("Expected ']' to close list index.");
                _pos++;
                segments.Add(new IndexPathSegment(idx));
            }
            else
            {
                break;
            }
        }

        return new DocumentPath(segments);
    }

    private string ResolveAttributeName(ExpressionToken token)
    {
        if (_names is null || !_names.TryGetValue(token.Text, out var resolved))
            throw new ExpressionSyntaxException(token.Position,
                $"An expression attribute name used in expression is not defined; attribute name: {token.Text}");
        return resolved;
    }

    private ValueRefOperand ResolveValueRef(ExpressionToken token)
    {
        if (_values is null || !_values.TryGetValue(token.Text, out var resolved))
            throw new ExpressionSyntaxException(token.Position,
                $"An expression attribute value used in expression is not defined; attribute value: {token.Text}");
        return new ValueRefOperand(token.Text, resolved);
    }

    private ExpressionToken Peek() => _tokens[_pos];

    private ExpressionSyntaxException Error(string message)
    {
        var t = _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
        return new ExpressionSyntaxException(t.Position, message);
    }
}
