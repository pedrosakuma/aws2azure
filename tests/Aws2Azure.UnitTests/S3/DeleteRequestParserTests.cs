using System.Text;
using Aws2Azure.Modules.S3.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.S3;

public class DeleteRequestParserTests
{
    private static DeleteRequestParser.ParseResult Parse(string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        using var ms = new MemoryStream(bytes);
        return DeleteRequestParser.Parse(ms);
    }

    [Fact]
    public void Parses_single_object_non_quiet()
    {
        var r = Parse("<Delete><Object><Key>a/b.txt</Key></Object></Delete>");
        Assert.True(r.Success, r.Error);
        Assert.False(r.Quiet);
        Assert.Single(r.Objects);
        Assert.Equal("a/b.txt", r.Objects[0].Key);
    }

    [Fact]
    public void Parses_multiple_objects_with_quiet_true()
    {
        var xml = "<Delete>"
                + "<Object><Key>k1</Key></Object>"
                + "<Object><Key>k2</Key></Object>"
                + "<Object><Key>k3</Key></Object>"
                + "<Quiet>true</Quiet>"
                + "</Delete>";
        var r = Parse(xml);
        Assert.True(r.Success, r.Error);
        Assert.True(r.Quiet);
        Assert.Equal(new[] { "k1", "k2", "k3" }, r.Objects.Select(o => o.Key));
    }

    [Fact]
    public void Rejects_versionId_on_object()
    {
        var r = Parse("<Delete><Object><Key>k</Key><VersionId>v1</VersionId></Object></Delete>");
        Assert.False(r.Success);
        Assert.Contains("versionId", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_empty_root()
    {
        var r = Parse("<Delete></Delete>");
        Assert.False(r.Success);
        Assert.Contains("at least one", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_wrong_root()
    {
        var r = Parse("<NotDelete><Object><Key>k</Key></Object></NotDelete>");
        Assert.False(r.Success);
    }

    [Fact]
    public void Rejects_object_missing_key()
    {
        var r = Parse("<Delete><Object></Object></Delete>");
        Assert.False(r.Success);
    }

    [Fact]
    public void Rejects_malformed_xml()
    {
        var r = Parse("<Delete><Object><Key>k</Key>");
        Assert.False(r.Success);
        Assert.StartsWith("MalformedXML", r.Error);
    }

    [Fact]
    public void Skips_unknown_siblings()
    {
        var r = Parse("<Delete><Foo>bar</Foo><Object><Key>k</Key></Object></Delete>");
        Assert.True(r.Success, r.Error);
        Assert.Single(r.Objects);
    }

    [Fact]
    public void Enforces_max_1000_keys()
    {
        var sb = new StringBuilder("<Delete>");
        for (var i = 0; i <= DeleteRequestParser.MaxObjects; i++)
        {
            sb.Append("<Object><Key>k").Append(i).Append("</Key></Object>");
        }
        sb.Append("</Delete>");
        var r = Parse(sb.ToString());
        Assert.False(r.Success);
        Assert.Contains("1000", r.Error!);
    }
}
