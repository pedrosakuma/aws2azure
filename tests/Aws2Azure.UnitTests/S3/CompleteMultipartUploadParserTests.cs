using System.Text;
using Aws2Azure.Modules.S3.Internal;

namespace Aws2Azure.UnitTests.S3;

public class CompleteMultipartUploadParserTests
{
    [Fact]
    public void Parses_ascending_parts()
    {
        var xml = """
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>"a"</ETag></Part>
              <Part><PartNumber>2</PartNumber><ETag>"b"</ETag></Part>
              <Part><PartNumber>5</PartNumber><ETag>"e"</ETag></Part>
            </CompleteMultipartUpload>
            """;
        var r = CompleteMultipartUploadParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        Assert.True(r.Success);
        Assert.Equal(3, r.Parts.Count);
        Assert.Equal(1, r.Parts[0].PartNumber);
        Assert.Equal(5, r.Parts[2].PartNumber);
        Assert.Equal("\"a\"", r.Parts[0].ETag);
    }

    [Fact]
    public void Rejects_out_of_order_parts()
    {
        var xml = """
            <CompleteMultipartUpload>
              <Part><PartNumber>2</PartNumber><ETag>"b"</ETag></Part>
              <Part><PartNumber>1</PartNumber><ETag>"a"</ETag></Part>
            </CompleteMultipartUpload>
            """;
        var r = CompleteMultipartUploadParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        Assert.False(r.Success);
        Assert.Contains("ascending", r.Error!);
    }

    [Fact]
    public void Rejects_duplicate_part_numbers()
    {
        var xml = """
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>"a"</ETag></Part>
              <Part><PartNumber>1</PartNumber><ETag>"b"</ETag></Part>
            </CompleteMultipartUpload>
            """;
        Assert.False(CompleteMultipartUploadParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Success);
    }

    [Fact]
    public void Rejects_empty_body()
    {
        Assert.False(CompleteMultipartUploadParser.Parse(new MemoryStream()).Success);
    }

    [Fact]
    public void Rejects_missing_part_number()
    {
        var xml = """
            <CompleteMultipartUpload>
              <Part><ETag>"a"</ETag></Part>
            </CompleteMultipartUpload>
            """;
        Assert.False(CompleteMultipartUploadParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Success);
    }

    [Fact]
    public void Rejects_part_number_out_of_range()
    {
        var xml = """
            <CompleteMultipartUpload>
              <Part><PartNumber>0</PartNumber></Part>
            </CompleteMultipartUpload>
            """;
        Assert.False(CompleteMultipartUploadParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Success);
    }

    [Fact]
    public void Rejects_dtd_xxe_attempt()
    {
        // DtdProcessing.Prohibit must short-circuit doctype declarations.
        var xml = "<!DOCTYPE root [<!ENTITY x \"y\">]><CompleteMultipartUpload><Part><PartNumber>1</PartNumber></Part></CompleteMultipartUpload>";
        Assert.False(CompleteMultipartUploadParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Success);
    }

    [Fact]
    public void Rejects_missing_root()
    {
        var xml = "<NotIt/>";
        Assert.False(CompleteMultipartUploadParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Success);
    }
}
