using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class TaggingHeaderParserTests
{
    [Fact]
    public void Parse_returns_empty_tags_for_missing_header()
    {
        var (tags, error) = TaggingHeaderParser.Parse(null);

        Assert.NotNull(tags);
        Assert.Empty(tags!);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_returns_empty_tags_for_empty_header()
    {
        var (tags, error) = TaggingHeaderParser.Parse(string.Empty);

        Assert.NotNull(tags);
        Assert.Empty(tags!);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_decodes_url_encoded_key_value_pairs()
    {
        var (tags, error) = TaggingHeaderParser.Parse("Project=Blue+Team&Owner=jane%40example.com");

        Assert.Null(error);
        Assert.NotNull(tags);
        Assert.Equal(2, tags!.Count);
        Assert.Contains(tags, t => t.Key == "Project" && t.Value == "Blue Team");
        Assert.Contains(tags, t => t.Key == "Owner" && t.Value == "jane@example.com");
    }

    [Fact]
    public void Parse_treats_key_without_equals_as_empty_value()
    {
        var (tags, error) = TaggingHeaderParser.Parse("standalone");

        Assert.Null(error);
        Assert.NotNull(tags);
        var tag = Assert.Single(tags!);
        Assert.Equal("standalone", tag.Key);
        Assert.Equal(string.Empty, tag.Value);
    }

    [Fact]
    public void Parse_rejects_more_than_ten_tags()
    {
        var header = string.Join('&', Enumerable.Range(0, TaggingHeaderParser.MaxTags + 1).Select(i => $"k{i}=v{i}"));

        var (tags, error) = TaggingHeaderParser.Parse(header);

        Assert.Null(tags);
        Assert.NotNull(error);
        Assert.Equal("InvalidArgument", error!.Value.Code);
    }

    [Fact]
    public void Parse_rejects_duplicate_keys()
    {
        var (tags, error) = TaggingHeaderParser.Parse("a=1&a=2");

        Assert.Null(tags);
        Assert.NotNull(error);
        Assert.Equal("InvalidArgument", error!.Value.Code);
    }

    [Fact]
    public void Parse_treats_keys_differing_only_by_case_as_distinct_tags()
    {
        // S3 tag keys are case-sensitive: "Env" and "ENV" are two different
        // tags, not a duplicate.
        var (tags, error) = TaggingHeaderParser.Parse("Env=prod&ENV=staging");

        Assert.Null(error);
        Assert.NotNull(tags);
        Assert.Equal(2, tags!.Count);
        Assert.Contains(tags, t => t.Key == "Env" && t.Value == "prod");
        Assert.Contains(tags, t => t.Key == "ENV" && t.Value == "staging");
    }

    [Fact]
    public void Parse_rejects_key_exceeding_max_length()
    {
        var longKey = new string('k', TaggingHeaderParser.MaxTagKeyLength + 1);

        var (tags, error) = TaggingHeaderParser.Parse($"{longKey}=v");

        Assert.Null(tags);
        Assert.NotNull(error);
        Assert.Equal("InvalidArgument", error!.Value.Code);
    }

    [Fact]
    public void Parse_rejects_value_exceeding_max_length()
    {
        var longValue = new string('v', TaggingHeaderParser.MaxTagValueLength + 1);

        var (tags, error) = TaggingHeaderParser.Parse($"k={longValue}");

        Assert.Null(tags);
        Assert.NotNull(error);
        Assert.Equal("InvalidArgument", error!.Value.Code);
    }

    [Fact]
    public void Parse_accepts_exactly_ten_tags()
    {
        var header = string.Join('&', Enumerable.Range(0, TaggingHeaderParser.MaxTags).Select(i => $"k{i}=v{i}"));

        var (tags, error) = TaggingHeaderParser.Parse(header);

        Assert.Null(error);
        Assert.NotNull(tags);
        Assert.Equal(TaggingHeaderParser.MaxTags, tags!.Count);
    }
}
