using System.Text;
using System.Xml.Linq;
using Aws2Azure.Modules.S3.Xml;

namespace Aws2Azure.UnitTests.S3;

public class S3ListXmlTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public void ListObjectsV2Result_emits_contents_and_common_prefixes()
    {
        var contents = new[]
        {
            new S3XmlWriter.ListedObject("a/file1.txt",
                DateTimeOffset.Parse("2024-05-01T10:11:12Z"), "\"etag1\"", 42, "STANDARD"),
            new S3XmlWriter.ListedObject("a/file2.txt",
                DateTimeOffset.Parse("2024-05-02T10:11:12Z"), "\"etag2\"", 99, "STANDARD"),
        };
        var prefixes = new[] { "b/", "c/" };

        var xml = RenderListObjectsV2(
            bucket: "my-bucket",
            prefix: "a/",
            delimiter: "/",
            maxKeys: 1000,
            keyCount: 4,
            isTruncated: false,
            continuationToken: null,
            nextContinuationToken: null,
            startAfter: null,
            encodeUrl: false,
            contents: contents,
            commonPrefixes: prefixes);

        var doc = XDocument.Parse(xml);
        Assert.Equal("ListBucketResult", doc.Root!.Name.LocalName);
        Assert.Equal(S3Ns, doc.Root!.Name.Namespace);
        Assert.Equal("my-bucket", doc.Root!.Element(S3Ns + "Name")!.Value);
        Assert.Equal("a/", doc.Root!.Element(S3Ns + "Prefix")!.Value);
        Assert.Equal("/", doc.Root!.Element(S3Ns + "Delimiter")!.Value);
        Assert.Equal("false", doc.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal("4", doc.Root!.Element(S3Ns + "KeyCount")!.Value);
        Assert.Equal("1000", doc.Root!.Element(S3Ns + "MaxKeys")!.Value);

        var keys = doc.Root!.Elements(S3Ns + "Contents")
            .Select(c => c.Element(S3Ns + "Key")!.Value).ToArray();
        Assert.Equal(new[] { "a/file1.txt", "a/file2.txt" }, keys);

        var cps = doc.Root!.Elements(S3Ns + "CommonPrefixes")
            .Select(c => c.Element(S3Ns + "Prefix")!.Value).ToArray();
        Assert.Equal(new[] { "b/", "c/" }, cps);
    }

    [Fact]
    public void ListObjectsV2Result_emits_next_continuation_token_when_truncated()
    {
        var xml = RenderListObjectsV2(
            bucket: "b",
            prefix: null,
            delimiter: null,
            maxKeys: 2,
            keyCount: 2,
            isTruncated: true,
            continuationToken: "incoming-token",
            nextContinuationToken: "next-token",
            startAfter: null,
            encodeUrl: false,
            contents: Array.Empty<S3XmlWriter.ListedObject>(),
            commonPrefixes: Array.Empty<string>());

        var doc = XDocument.Parse(xml);
        Assert.Equal("true", doc.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal("next-token", doc.Root!.Element(S3Ns + "NextContinuationToken")!.Value);
        Assert.Equal("incoming-token", doc.Root!.Element(S3Ns + "ContinuationToken")!.Value);
    }

    [Fact]
    public void ListObjectsV2Result_url_encodes_keys_when_requested()
    {
        var contents = new[]
        {
            new S3XmlWriter.ListedObject("with space/and+plus.txt",
                DateTimeOffset.Parse("2024-05-01T10:11:12Z"), null, 1, "STANDARD"),
        };
        var xml = RenderListObjectsV2(
            bucket: "b",
            prefix: "with space/",
            delimiter: "/",
            maxKeys: 1000,
            keyCount: 1,
            isTruncated: false,
            continuationToken: null,
            nextContinuationToken: null,
            startAfter: null,
            encodeUrl: true,
            contents: contents,
            commonPrefixes: Array.Empty<string>());

        var doc = XDocument.Parse(xml);
        Assert.Equal("url", doc.Root!.Element(S3Ns + "EncodingType")!.Value);
        var key = doc.Root!.Element(S3Ns + "Contents")!.Element(S3Ns + "Key")!.Value;
        Assert.Equal("with%20space%2Fand%2Bplus.txt", key);
        var prefix = doc.Root!.Element(S3Ns + "Prefix")!.Value;
        Assert.Equal("with%20space%2F", prefix);
    }

    [Fact]
    public void ListObjectsV2Result_quotes_unquoted_etag_from_azure()
    {
        var contents = new[]
        {
            // Azure surfaces ETag unquoted (e.g. 0x8CBFF…) — must be S3-quoted on output.
            new S3XmlWriter.ListedObject("k.txt",
                DateTimeOffset.Parse("2024-05-01T10:11:12Z"), "0x8CBFF45D8A29A19", 1, "STANDARD"),
            new S3XmlWriter.ListedObject("already-quoted.txt",
                DateTimeOffset.Parse("2024-05-01T10:11:12Z"), "\"abc\"", 1, "STANDARD"),
        };
        var xml = RenderListObjectsV2(
            bucket: "b", prefix: null, delimiter: null, maxKeys: 1000,
            keyCount: 2, isTruncated: false,
            continuationToken: null, nextContinuationToken: null, startAfter: null,
            encodeUrl: false, contents: contents, commonPrefixes: Array.Empty<string>());

        var doc = XDocument.Parse(xml);
        var etags = doc.Root!.Elements(S3Ns + "Contents")
            .Select(c => c.Element(S3Ns + "ETag")!.Value).ToArray();
        Assert.Equal("\"0x8CBFF45D8A29A19\"", etags[0]);
        Assert.Equal("\"abc\"", etags[1]);
    }

    [Fact]
    public void ListBucketResult_v1_emits_marker_and_next_marker_when_truncated_with_delimiter()
    {
        var xml = RenderListBucketV1(
            bucket: "b",
            prefix: null,
            delimiter: "/",
            maxKeys: 100,
            isTruncated: true,
            marker: "incoming",
            nextMarker: "stop-here",
            encodeUrl: false,
            contents: Array.Empty<S3XmlWriter.ListedObject>(),
            commonPrefixes: Array.Empty<string>());

        var doc = XDocument.Parse(xml);
        Assert.Equal("incoming", doc.Root!.Element(S3Ns + "Marker")!.Value);
        Assert.Equal("stop-here", doc.Root!.Element(S3Ns + "NextMarker")!.Value);
        Assert.Equal("true", doc.Root!.Element(S3Ns + "IsTruncated")!.Value);
    }

    // Render helpers wrap the async stream overloads (the production API) and
    // return the body as a string so the assertions below can parse it. The
    // module no longer ships string-returning overloads (they were dead outside
    // tests); these helpers keep the test ergonomics without that production
    // surface.
    private static string RenderListObjectsV2(
        string bucket,
        string? prefix,
        string? delimiter,
        int maxKeys,
        int keyCount,
        bool isTruncated,
        string? continuationToken,
        string? nextContinuationToken,
        string? startAfter,
        bool encodeUrl,
        IReadOnlyList<S3XmlWriter.ListedObject> contents,
        IReadOnlyList<string> commonPrefixes)
    {
        using var ms = new MemoryStream();
        S3XmlWriter.WriteListObjectsV2ResultAsync(
            ms, bucket, prefix, delimiter, maxKeys, keyCount, isTruncated,
            continuationToken, nextContinuationToken, startAfter, encodeUrl,
            contents, commonPrefixes).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string RenderListBucketV1(
        string bucket,
        string? prefix,
        string? delimiter,
        int maxKeys,
        bool isTruncated,
        string? marker,
        string? nextMarker,
        bool encodeUrl,
        IReadOnlyList<S3XmlWriter.ListedObject> contents,
        IReadOnlyList<string> commonPrefixes)
    {
        using var ms = new MemoryStream();
        S3XmlWriter.WriteListBucketResultAsync(
            ms, bucket, prefix, delimiter, maxKeys, isTruncated, marker, nextMarker,
            encodeUrl, contents, commonPrefixes).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

public class AzureBlobListReaderTests
{
    [Fact]
    public void ParseBlobListPage_extracts_blobs_and_prefixes_and_marker()
    {
        const string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<EnumerationResults>" +
            "  <Blobs>" +
            "    <Blob>" +
            "      <Name>a/file1.txt</Name>" +
            "      <Properties>" +
            "        <Last-Modified>Tue, 02 Jan 2024 03:04:05 GMT</Last-Modified>" +
            "        <Etag>\"etag1\"</Etag>" +
            "        <Content-Length>42</Content-Length>" +
            "      </Properties>" +
            "    </Blob>" +
            "    <BlobPrefix><Name>b/</Name></BlobPrefix>" +
            "    <Blob>" +
            "      <Name>a/file2.txt</Name>" +
            "      <Properties>" +
            "        <Last-Modified>Wed, 03 Jan 2024 03:04:05 GMT</Last-Modified>" +
            "        <Etag>\"etag2\"</Etag>" +
            "        <Content-Length>99</Content-Length>" +
            "      </Properties>" +
            "    </Blob>" +
            "  </Blobs>" +
            "  <NextMarker>continue-here</NextMarker>" +
            "</EnumerationResults>";

        var page = Aws2Azure.Modules.S3.Xml.AzureBlobXmlReader.ParseBlobListPage(xml);
        Assert.Equal(2, page.Blobs.Count);
        Assert.Equal("a/file1.txt", page.Blobs[0].Name);
        Assert.Equal(42, page.Blobs[0].ContentLength);
        Assert.Equal("\"etag1\"", page.Blobs[0].ETag);
        Assert.Single(page.BlobPrefixes);
        Assert.Equal("b/", page.BlobPrefixes[0]);
        Assert.Equal("continue-here", page.NextMarker);
    }

    [Fact]
    public void ParseBlobListPage_returns_null_marker_when_complete()
    {
        const string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<EnumerationResults><Blobs></Blobs></EnumerationResults>";

        var page = Aws2Azure.Modules.S3.Xml.AzureBlobXmlReader.ParseBlobListPage(xml);
        Assert.Empty(page.Blobs);
        Assert.Empty(page.BlobPrefixes);
        Assert.Null(page.NextMarker);
    }
}
