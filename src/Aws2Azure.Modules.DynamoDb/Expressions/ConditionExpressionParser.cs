using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Expressions;

/// <summary>
/// Parses the full DynamoDB ConditionExpression / FilterExpression
/// grammar. Reuses <see cref="ExpressionLexer"/> and the path /
/// value-ref resolution conventions established by
/// <see cref="UpdateExpressionParser"/>.
///
/// <code>
/// condition   := or
/// or          := and ('OR' and)*
/// and         := not ('AND' not)*
/// not         := 'NOT' not | primary
/// primary     := '(' condition ')' | function | comparison
/// function    := 'attribute_exists' '(' path ')'
///              | 'attribute_not_exists' '(' path ')'
///              | 'attribute_type' '(' path ',' valueRef ')'
///              | 'begins_with' '(' operand ',' operand ')'
///              | 'contains'   '(' operand ',' operand ')'
/// comparison  := operand compareOp operand
///              | operand 'BETWEEN' operand 'AND' operand
///              | operand 'IN' '(' operand (',' operand)* ')'
/// operand     := 'size' '(' path ')' | path | valueRef
/// compareOp   := '=' | '&lt;&gt;' | '&lt;' | '&lt;=' | '&gt;' | '&gt;='
/// </code>
///
/// Comparison/function/operand sub-grammars match the published AWS
/// reference; precedence is <c>NOT</c> &gt; <c>AND</c> &gt; <c>OR</c>.
/// Reserved keywords (<c>AND</c>, <c>OR</c>, <c>NOT</c>,
/// <c>BETWEEN</c>, <c>IN</c>) are matched case-insensitively against
/// bare identifiers; identifiers can still be used as attribute names
/// when they appear in non-keyword positions (DynamoDB also allows this
/// — applications using reserved attribute names must alias via
/// <c>#name</c>).
/// </summary>
internal sealed class ConditionExpressionParser
{
    internal const int MaxInOperands = 100;
    internal const int MaxExpressionUtf8Bytes =
        ExpressionParameterLimits.MaxExpressionUtf8Bytes;
    internal const int MaxPlaceholderUtf8Bytes =
        ExpressionParameterLimits.MaxPlaceholderUtf8Bytes;
    internal const int MaxOperators = 300;
    internal const int MaxAstDepth = MaxOperators;
    internal const int MaxParserNestingDepth = 64;

    private readonly List<ExpressionToken> _tokens;
    private readonly IReadOnlyDictionary<string, string>? _names;
    private readonly IReadOnlyDictionary<string, JsonElement>? _values;
    private readonly HashSet<string> _consumedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumedValues = new(StringComparer.Ordinal);
    private int _pos;
    private int _groupDepth;

    private readonly record struct ParsedCondition(
        ConditionNode Node,
        int Depth);

    private ConditionExpressionParser(
        List<ExpressionToken> tokens,
        IReadOnlyDictionary<string, string>? names,
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        _tokens = tokens;
        _names = names;
        _values = values;
    }

    public static ConditionNode Parse(
        string expression,
        IReadOnlyDictionary<string, string>? expressionAttributeNames,
        IReadOnlyDictionary<string, JsonElement>? expressionAttributeValues)
        => ParseWithUsage(
            expression,
            expressionAttributeNames,
            expressionAttributeValues).Node;

    public static ConditionExpressionParseResult ParseWithUsage(
        string expression,
        IReadOnlyDictionary<string, string>? expressionAttributeNames,
        IReadOnlyDictionary<string, JsonElement>? expressionAttributeValues)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ExpressionSyntaxException(0, "ConditionExpression cannot be empty.");

        ValidateEncodedLength(expression);
        ExpressionParameterLimits.ValidateAttributeNamePlaceholders(
            expressionAttributeNames);
        var tokens = ExpressionLexer.Tokenise(expression);
        ValidateOperatorCount(tokens);
        var parser = new ConditionExpressionParser(tokens, expressionAttributeNames, expressionAttributeValues);
        var root = parser.ParseOr();
        if (parser.Peek().Kind != TokenKind.EndOfInput)
            throw parser.Error("Unexpected trailing tokens in ConditionExpression.");
        return new ConditionExpressionParseResult(
            root.Node,
            parser._consumedNames,
            parser._consumedValues);
    }

    internal static bool TryValidatePlaceholderLength(
        string placeholder,
        out string error)
        => ExpressionParameterLimits.TryValidatePlaceholderLength(
            placeholder,
            out error);

    // ----- precedence climb ------------------------------------------

    private ParsedCondition ParseOr()
    {
        var left = ParseAnd();
        while (TryConsumeKeyword("OR"))
        {
            var right = ParseAnd();
            left = CreateBinary(
                new OrCondition(left.Node, right.Node),
                left.Depth,
                right.Depth);
        }
        return left;
    }

    private ParsedCondition ParseAnd()
    {
        var left = ParseNot();
        while (TryConsumeKeyword("AND"))
        {
            var right = ParseNot();
            left = CreateBinary(
                new AndCondition(left.Node, right.Node),
                left.Depth,
                right.Depth);
        }
        return left;
    }

    private ParsedCondition ParseNot()
    {
        var notCount = 0;
        while (TryConsumeKeyword("NOT"))
        {
            notCount++;
        }

        var parsed = ParseUnit();
        while (notCount-- > 0)
        {
            parsed = CreateUnary(
                new NotCondition(parsed.Node),
                parsed.Depth);
        }
        return parsed;
    }

    private ParsedCondition ParseUnit()
    {
        var t = Peek();
        if (t.Kind == TokenKind.LParen)
        {
            _pos++;
            _groupDepth++;
            if (_groupDepth > MaxParserNestingDepth)
            {
                throw Error(
                    $"ConditionExpression exceeds the maximum nesting depth of {MaxParserNestingDepth}.");
            }

            try
            {
                var inner = ParseOr();
                if (Peek().Kind != TokenKind.RParen)
                    throw Error("Expected ')' to close grouped condition.");
                _pos++;
                return inner;
            }
            finally
            {
                _groupDepth--;
            }
        }

        // Function or comparison — function names are recognised by
        // identifier-followed-by-LParen.
        if (t.Kind == TokenKind.Identifier
            && _pos + 1 < _tokens.Count
            && _tokens[_pos + 1].Kind == TokenKind.LParen
            && IsBooleanFunctionName(t.Text))
        {
            return ParseFunctionCall(t.Text);
        }

        return ParseComparison();
    }

    private static bool IsBooleanFunctionName(string name) =>
        string.Equals(name, "attribute_exists", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "attribute_not_exists", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "attribute_type", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "begins_with", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "contains", StringComparison.OrdinalIgnoreCase);

    private ParsedCondition ParseFunctionCall(string name)
    {
        _pos++; // consume identifier
        if (Peek().Kind != TokenKind.LParen) throw Error($"Expected '(' after function '{name}'.");
        _pos++;

        ConditionNode node;
        if (string.Equals(name, "attribute_exists", StringComparison.OrdinalIgnoreCase))
        {
            var path = ParsePath();
            node = new AttributeExistsCondition(path);
        }
        else if (string.Equals(name, "attribute_not_exists", StringComparison.OrdinalIgnoreCase))
        {
            var path = ParsePath();
            node = new AttributeNotExistsCondition(path);
        }
        else if (string.Equals(name, "attribute_type", StringComparison.OrdinalIgnoreCase))
        {
            var path = ParsePath();
            if (Peek().Kind != TokenKind.Comma) throw Error("Expected ',' inside attribute_type().");
            _pos++;
            var typeTok = Peek();
            if (typeTok.Kind != TokenKind.AttributeValueRef)
                throw Error("attribute_type's second argument must be an :value reference to a string type tag.");
            _pos++;
            var v = ResolveValueRef(typeTok);
            node = new AttributeTypeCondition(path, v);
        }
        else if (string.Equals(name, "begins_with", StringComparison.OrdinalIgnoreCase))
        {
            var left = ParseOperand(allowSize: false);
            if (Peek().Kind != TokenKind.Comma) throw Error("Expected ',' inside begins_with().");
            _pos++;
            var right = ParseOperand(allowSize: false);
            node = new BeginsWithCondition(left, right);
        }
        else if (string.Equals(name, "contains", StringComparison.OrdinalIgnoreCase))
        {
            var left = ParseOperand(allowSize: false);
            if (Peek().Kind != TokenKind.Comma) throw Error("Expected ',' inside contains().");
            _pos++;
            var right = ParseOperand(allowSize: false);
            node = new ContainsCondition(left, right);
        }
        else
        {
            throw Error($"Unknown boolean function '{name}'.");
        }

        if (Peek().Kind != TokenKind.RParen) throw Error($"Expected ')' to close {name}().");
        _pos++;
        return new ParsedCondition(node, 1);
    }

    private ParsedCondition ParseComparison()
    {
        var left = ParseOperand(allowSize: true);
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.Equals:
            case TokenKind.NotEquals:
            case TokenKind.Less:
            case TokenKind.LessEquals:
            case TokenKind.Greater:
            case TokenKind.GreaterEquals:
            {
                var op = t.Kind switch
                {
                    TokenKind.Equals => CompareOp.Equal,
                    TokenKind.NotEquals => CompareOp.NotEqual,
                    TokenKind.Less => CompareOp.Less,
                    TokenKind.LessEquals => CompareOp.LessEqual,
                    TokenKind.Greater => CompareOp.Greater,
                    TokenKind.GreaterEquals => CompareOp.GreaterEqual,
                    _ => throw Error("Unreachable."),
                };
                _pos++;
                var right = ParseOperand(allowSize: true);
                return new ParsedCondition(
                    new CompareCondition(op, left, right),
                    1);
            }
            case TokenKind.Identifier
                when string.Equals(t.Text, "BETWEEN", StringComparison.OrdinalIgnoreCase):
            {
                _pos++;
                var lower = ParseOperand(allowSize: true);
                if (!TryConsumeKeyword("AND"))
                    throw Error("Expected 'AND' between BETWEEN bounds.");
                var upper = ParseOperand(allowSize: true);
                return new ParsedCondition(
                    new BetweenCondition(left, lower, upper),
                    1);
            }
            case TokenKind.Identifier
                when string.Equals(t.Text, "IN", StringComparison.OrdinalIgnoreCase):
            {
                _pos++;
                if (Peek().Kind != TokenKind.LParen)
                    throw Error("Expected '(' after IN.");
                _pos++;
                var operands = new List<ConditionOperand> { ParseOperand(allowSize: true) };
                while (Peek().Kind == TokenKind.Comma)
                {
                    _pos++;
                    if (operands.Count >= MaxInOperands)
                    {
                        throw Error(
                            $"The IN operator supports at most {MaxInOperands} operands.");
                    }
                    operands.Add(ParseOperand(allowSize: true));
                }
                if (Peek().Kind != TokenKind.RParen)
                    throw Error("Expected ')' to close IN list.");
                _pos++;
                return new ParsedCondition(
                    new InCondition(left, operands),
                    1);
            }
            default:
                throw Error("Expected a comparison operator, BETWEEN, or IN.");
        }
    }

    private ConditionOperand ParseOperand(bool allowSize)
    {
        var t = Peek();
        if (allowSize
            && t.Kind == TokenKind.Identifier
            && string.Equals(t.Text, "size", StringComparison.OrdinalIgnoreCase)
            && _pos + 1 < _tokens.Count
            && _tokens[_pos + 1].Kind == TokenKind.LParen)
        {
            _pos += 2;
            var path = ParsePath();
            if (Peek().Kind != TokenKind.RParen) throw Error("Expected ')' to close size().");
            _pos++;
            return new SizeOperand(path);
        }
        if (t.Kind == TokenKind.AttributeValueRef)
        {
            _pos++;
            return new ConditionValueOperand(ResolveValueRef(t));
        }
        return new ConditionPathOperand(ParsePath());
    }

    // ----- shared parser helpers -------------------------------------

    private DocumentPath ParsePath()
        => ExpressionPathParser.ParsePath(
            _tokens,
            ref _pos,
            _names,
            consumedNames: _consumedNames);

    private bool TryConsumeKeyword(string keyword)
    {
        var t = Peek();
        if (t.Kind == TokenKind.Identifier
            && string.Equals(t.Text, keyword, StringComparison.OrdinalIgnoreCase))
        {
            _pos++;
            return true;
        }
        return false;
    }

    private ValueRefOperand ResolveValueRef(ExpressionToken token)
        => ExpressionPathParser.ResolveValueRef(
            token,
            _values,
            _consumedValues);

    private ExpressionToken Peek() => _tokens[_pos];

    private ExpressionSyntaxException Error(string message)
        => ExpressionPathParser.Error(_tokens, _pos, message);

    private ParsedCondition CreateBinary(
        ConditionNode node,
        int leftDepth,
        int rightDepth)
        => CreateWithDepth(node, checked(1 + Math.Max(leftDepth, rightDepth)));

    private ParsedCondition CreateUnary(
        ConditionNode node,
        int operandDepth)
        => CreateWithDepth(node, checked(1 + operandDepth));

    private ParsedCondition CreateWithDepth(ConditionNode node, int depth)
    {
        if (depth > MaxAstDepth)
        {
            throw Error(
                $"ConditionExpression exceeds the maximum AST depth of {MaxAstDepth}.");
        }
        return new ParsedCondition(node, depth);
    }

    private static void ValidateEncodedLength(string expression)
        => ExpressionParameterLimits.ValidateEncodedLength(
            expression,
            "ConditionExpression");

    private static void ValidateOperatorCount(
        IReadOnlyList<ExpressionToken> tokens)
    {
        var count = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind is TokenKind.Equals
                or TokenKind.NotEquals
                or TokenKind.Less
                or TokenKind.LessEquals
                or TokenKind.Greater
                or TokenKind.GreaterEquals)
            {
                count++;
            }
            else if (token.Kind == TokenKind.Identifier)
            {
                var isKeyword =
                    string.Equals(
                        token.Text,
                        "AND",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        token.Text,
                        "OR",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        token.Text,
                        "NOT",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        token.Text,
                        "BETWEEN",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        token.Text,
                        "IN",
                        StringComparison.OrdinalIgnoreCase);
                var isFunction =
                    index + 1 < tokens.Count
                    && tokens[index + 1].Kind == TokenKind.LParen
                    && (IsBooleanFunctionName(token.Text)
                        || string.Equals(
                            token.Text,
                            "size",
                            StringComparison.OrdinalIgnoreCase));
                if (isKeyword || isFunction)
                {
                    count++;
                }
            }

            if (count > MaxOperators)
            {
                throw new ExpressionSyntaxException(
                    token.Position,
                    $"ConditionExpression exceeds the maximum complexity of {MaxOperators} operators.");
            }
        }
    }
}

internal sealed record ConditionExpressionParseResult(
    ConditionNode Node,
    IReadOnlySet<string> ConsumedNames,
    IReadOnlySet<string> ConsumedValues);
