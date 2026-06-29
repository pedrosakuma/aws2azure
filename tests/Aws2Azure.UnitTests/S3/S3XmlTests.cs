using System.Xml.Linq;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Xml;

namespace Aws2Azure.UnitTests.S3;

public class S3XmlTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Suspended")]
    public void VersioningConfiguration_emits_status(string status)
    {
        var xml = S3XmlWriter.VersioningConfiguration(status);
        var doc = XDocument.Parse(xml);
        Assert.Equal("VersioningConfiguration", doc.Root!.Name.LocalName);
        Assert.Equal(S3Ns, doc.Root!.Name.Namespace);
        Assert.Equal(status, doc.Root!.Element(S3Ns + "Status")!.Value);
    }

    [Fact]
    public void VersioningConfiguration_null_emits_empty_document()
    {
        var doc = XDocument.Parse(S3XmlWriter.VersioningConfiguration(null));
        Assert.Equal("VersioningConfiguration", doc.Root!.Name.LocalName);
        Assert.Null(doc.Root!.Element(S3Ns + "Status"));
    }

    [Fact]
    public void ObjectRetention_emits_mode_and_iso_date()
    {
        var until = DateTimeOffset.Parse("2030-01-02T03:04:05Z");
        var doc = XDocument.Parse(S3XmlWriter.ObjectRetention("COMPLIANCE", until));
        Assert.Equal("Retention", doc.Root!.Name.LocalName);
        Assert.Equal(S3Ns, doc.Root!.Name.Namespace);
        Assert.Equal("COMPLIANCE", doc.Root!.Element(S3Ns + "Mode")!.Value);
        Assert.Equal("2030-01-02T03:04:05.000Z", doc.Root!.Element(S3Ns + "RetainUntilDate")!.Value);
    }

    [Theory]
    [InlineData(true, "ON")]
    [InlineData(false, "OFF")]
    public void ObjectLegalHold_emits_status(bool on, string expected)
    {
        var doc = XDocument.Parse(S3XmlWriter.ObjectLegalHold(on));
        Assert.Equal("LegalHold", doc.Root!.Name.LocalName);
        Assert.Equal(expected, doc.Root!.Element(S3Ns + "Status")!.Value);
    }

    [Fact]
    public void ListAllMyBucketsResult_emits_expected_shape()
    {
        var buckets = new[]
        {
            new S3XmlWriter.BucketInfo("alpha", DateTimeOffset.Parse("2024-01-02T03:04:05Z")),
            new S3XmlWriter.BucketInfo("beta",  DateTimeOffset.Parse("2024-02-03T04:05:06Z")),
        };
        var xml = S3XmlWriter.ListAllMyBucketsResult(
            new S3XmlWriter.OwnerInfo("AKIA-test", "AKIA-test"), buckets);

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";

        Assert.Equal("ListAllMyBucketsResult", doc.Root!.Name.LocalName);
        Assert.Equal(ns, doc.Root!.Name.Namespace);

        var names = doc.Root!.Element(ns + "Buckets")!
            .Elements(ns + "Bucket")
            .Select(b => b.Element(ns + "Name")!.Value)
            .ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, names);

        var owner = doc.Root!.Element(ns + "Owner")!;
        Assert.Equal("AKIA-test", owner.Element(ns + "ID")!.Value);
    }

    [Fact]
    public void ParseContainerList_handles_azurite_shape()
    {
        const string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<EnumerationResults ServiceEndpoint=\"http://127.0.0.1:10000/devstoreaccount1\">" +
            "  <Containers>" +
            "    <Container>" +
            "      <Name>alpha</Name>" +
            "      <Properties>" +
            "        <Last-Modified>Tue, 02 Jan 2024 03:04:05 GMT</Last-Modified>" +
            "      </Properties>" +
            "    </Container>" +
            "    <Container>" +
            "      <Name>beta</Name>" +
            "      <Properties>" +
            "        <Last-Modified>Mon, 04 Mar 2024 05:06:07 GMT</Last-Modified>" +
            "      </Properties>" +
            "    </Container>" +
            "  </Containers>" +
            "</EnumerationResults>";

        var list = AzureBlobXmlReader.ParseContainerList(xml);
        Assert.Equal(2, list.Count);
        Assert.Equal("alpha", list[0].Name);
        Assert.Equal("beta", list[1].Name);
        Assert.Equal(DateTimeOffset.Parse("2024-01-02T03:04:05Z"), list[0].LastModified);
    }

    [Theory]
    [InlineData("ab",        false)] // too short
    [InlineData("abc",       true)]
    [InlineData("a-b-c",     true)]
    [InlineData("Abc",       false)] // upper
    [InlineData("a..b",      false)] // dot
    [InlineData("a--b",      false)] // double hyphen
    [InlineData("-abc",      false)] // leading hyphen
    [InlineData("abc-",      false)] // trailing hyphen
    [InlineData("a23456789012345678901234567890123456789012345678901234567890bcd", true)]
    public void Validates_container_names(string name, bool expected)
    {
        Assert.Equal(expected, BlobClient.IsValidContainerName(name));
    }

    [Fact]
    public void ParseContainerListPage_extracts_next_marker()
    {
        const string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<EnumerationResults>" +
            "  <Containers>" +
            "    <Container><Name>alpha</Name><Properties><Last-Modified>Tue, 02 Jan 2024 03:04:05 GMT</Last-Modified></Properties></Container>" +
            "  </Containers>" +
            "  <NextMarker>cursor-2</NextMarker>" +
            "</EnumerationResults>";

        var page = AzureBlobXmlReader.ParseContainerListPage(xml);
        Assert.Single(page.Containers);
        Assert.Equal("cursor-2", page.NextMarker);
    }

    [Fact]
    public void ParseContainerListPage_returns_null_marker_when_complete()
    {
        const string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<EnumerationResults><Containers></Containers><NextMarker /></EnumerationResults>";

        var page = AzureBlobXmlReader.ParseContainerListPage(xml);
        Assert.Empty(page.Containers);
        Assert.Null(page.NextMarker);
    }
}
