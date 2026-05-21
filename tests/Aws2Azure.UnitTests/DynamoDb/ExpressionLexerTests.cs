using System.Linq;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

/// <summary>
/// Token-level smoke tests for the shared expression lexer used by
/// UpdateExpression (this slice) and the boolean grammars in later
/// slices. The parser tests exercise the lexer indirectly; these cases
/// pin the harder invariants (whitespace skipping, sigil parsing,
/// multi-character operators, error positions).
/// </summary>
public class ExpressionLexerTests
{
    [Fact]
    public void Tokenises_set_with_value_ref_and_arithmetic()
    {
        var tokens = ExpressionLexer.Tokenise("SET a = b + :inc");
        Assert.Equal(
            new[]
            {
                TokenKind.Identifier, TokenKind.Identifier, TokenKind.Equals,
                TokenKind.Identifier, TokenKind.Plus, TokenKind.AttributeValueRef,
                TokenKind.EndOfInput,
            },
            tokens.Select(t => t.Kind).ToArray());
        Assert.Equal(":inc", tokens[5].Text);
    }

    [Fact]
    public void Tokenises_nested_path_with_index_and_alias()
    {
        var tokens = ExpressionLexer.Tokenise("#a.b[12]");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            new[]
            {
                TokenKind.AttributeNameRef, TokenKind.Dot, TokenKind.Identifier,
                TokenKind.LBracket, TokenKind.Number, TokenKind.RBracket,
                TokenKind.EndOfInput,
            },
            kinds);
        Assert.Equal("12", tokens[4].Text);
    }

    [Theory]
    [InlineData("<", "Less")]
    [InlineData("<=", "LessEquals")]
    [InlineData(">", "Greater")]
    [InlineData(">=", "GreaterEquals")]
    [InlineData("<>", "NotEquals")]
    public void Tokenises_comparison_operators(string op, string expected)
    {
        var tokens = ExpressionLexer.Tokenise("a " + op + " :v");
        Assert.Equal(expected, tokens[1].Kind.ToString());
    }

    [Fact]
    public void Throws_with_offset_on_unexpected_character()
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() => ExpressionLexer.Tokenise("SET a @ :v"));
        Assert.Equal(6, ex.Position);
    }

    [Fact]
    public void Throws_when_sigil_has_no_identifier()
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() => ExpressionLexer.Tokenise("SET a = : "));
        Assert.Equal(8, ex.Position);
    }
}
