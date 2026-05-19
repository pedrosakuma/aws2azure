using Aws2Azure.Modules.S3.Xml;

namespace Aws2Azure.UnitTests.S3;

public class TagSetParserTests
{
    [Fact]
    public void Parses_s3_tagging_root_with_multiple_tags()
    {
        var xml = "<Tagging xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><TagSet>" +
                  "<Tag><Key>env</Key><Value>prod</Value></Tag>" +
                  "<Tag><Key>owner</Key><Value>team-a</Value></Tag>" +
                  "</TagSet></Tagging>";
        var tags = AzureBlobXmlReader.ParseTagSet(xml)!;
        Assert.Equal(2, tags.Count);
        Assert.Equal("env", tags[0].Key);
        Assert.Equal("prod", tags[0].Value);
        Assert.Equal("owner", tags[1].Key);
        Assert.Equal("team-a", tags[1].Value);
    }

    [Fact]
    public void Parses_azure_tags_root()
    {
        var xml = "<Tags><TagSet><Tag><Key>k</Key><Value>v</Value></Tag></TagSet></Tags>";
        var tags = AzureBlobXmlReader.ParseTagSet(xml)!;
        Assert.Single(tags);
        Assert.Equal("k", tags[0].Key);
        Assert.Equal("v", tags[0].Value);
    }

    [Fact]
    public void Empty_tag_set_returns_empty_list()
    {
        var xml = "<Tagging><TagSet></TagSet></Tagging>";
        var tags = AzureBlobXmlReader.ParseTagSet(xml)!;
        Assert.Empty(tags);
    }

    [Fact]
    public void Missing_TagSet_returns_null()
    {
        var xml = "<Tagging></Tagging>";
        var tags = AzureBlobXmlReader.ParseTagSet(xml);
        Assert.Null(tags);
    }

    [Fact]
    public void Roundtrips_through_writer()
    {
        var input = new[] { new S3XmlWriter.Tag("a", "1"), new S3XmlWriter.Tag("b", "2") };
        var xml = S3XmlWriter.Tagging(input);
        var parsed = AzureBlobXmlReader.ParseTagSet(xml)!;
        Assert.Equal(2, parsed.Count);
        Assert.Equal("a", parsed[0].Key);
        Assert.Equal("1", parsed[0].Value);
        Assert.Equal("b", parsed[1].Key);
        Assert.Equal("2", parsed[1].Value);
    }
}
