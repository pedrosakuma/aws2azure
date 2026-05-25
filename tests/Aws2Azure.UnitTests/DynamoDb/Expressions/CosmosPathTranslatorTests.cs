using Aws2Azure.Modules.DynamoDb.Expressions;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb.Expressions;

public sealed class CosmosPathTranslatorTests
{
    private static DocumentPath Path(params PathSegment[] segments) => new(segments);
    private static AttributePathSegment A(string name) => new(name);
    private static IndexPathSegment I(int index) => new(index);

    [Fact]
    public void Top_level_attribute_is_bracketed()
    {
        Assert.Equal("c[\"name\"]", CosmosPathTranslator.Translate(Path(A("name"))));
    }

    [Fact]
    public void Nested_attributes_chain_brackets()
    {
        Assert.Equal(
            "c[\"a\"][\"b\"][\"c\"]",
            CosmosPathTranslator.Translate(Path(A("a"), A("b"), A("c"))));
    }

    [Fact]
    public void List_index_emits_bracket_int()
    {
        Assert.Equal(
            "c[\"list\"][0]",
            CosmosPathTranslator.Translate(Path(A("list"), I(0))));
    }

    [Fact]
    public void Mixed_path_with_index_in_middle()
    {
        Assert.Equal(
            "c[\"a\"][\"b\"][2][\"c\"]",
            CosmosPathTranslator.Translate(Path(A("a"), A("b"), I(2), A("c"))));
    }

    [Theory]
    [InlineData("plain", "c[\"plain\"]")]
    // Names DDB allows but require careful escaping in any string-quoted form.
    [InlineData("with.dot", "c[\"with.dot\"]")]
    [InlineData("with-dash", "c[\"with-dash\"]")]
    [InlineData("with space", "c[\"with space\"]")]
    [InlineData("with\"quote", "c[\"with\\\"quote\"]")]
    [InlineData("back\\slash", "c[\"back\\\\slash\"]")]
    [InlineData("tab\tchar", "c[\"tab\\tchar\"]")]
    [InlineData("new\nline", "c[\"new\\nline\"]")]
    [InlineData("uni\u00e9code", "c[\"uni\u00e9code\"]")]
    public void Special_characters_in_attribute_names_are_escaped(string name, string expected)
    {
        Assert.Equal(expected, CosmosPathTranslator.Translate(Path(A(name))));
    }

    [Fact]
    public void Control_characters_below_space_use_unicode_escape()
    {
        Assert.Equal(
            "c[\"x\\u0001y\"]",
            CosmosPathTranslator.Translate(Path(A("x\u0001y"))));
    }

    [Fact]
    public void Custom_root_alias_is_used()
    {
        Assert.Equal(
            "doc[\"name\"]",
            CosmosPathTranslator.Translate(Path(A("name")), rootAlias: "doc"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1bad")]   // starts with digit
    [InlineData("a b")]    // contains space
    [InlineData("a.b")]    // contains dot
    [InlineData("a\"b")]   // contains quote
    public void Invalid_root_alias_is_rejected(string alias)
    {
        Assert.Throws<System.ArgumentException>(
            () => CosmosPathTranslator.Translate(Path(A("name")), rootAlias: alias));
    }

    [Fact]
    public void Null_path_throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => CosmosPathTranslator.Translate(null!));
    }
}
