using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Operations;
using Aws2Azure.TestSupport.Http;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

public sealed class MultipartHandlersTests
{
    private const string AccountName = "acct";
    private const string AccountKeyBase64 = "dGVzdC1rZXktQS0xMjM0NTY3ODkwYWJjZGVm";
    private static readonly byte[] AccountKeyBytes = Convert.FromBase64String(AccountKeyBase64);
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public async Task Create_upload_single_part_and_complete_returns_multipart_etag()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, eTag: "\"0xABCD\""));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var create = TestHttpContext.CreateContext(method: HttpMethods.Post, path: "/bucket/object.txt", queryString: "?uploads");
        await MultipartHandlers.HandleAsync(create, Route(S3Operation.CreateMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);

        var uploadId = ElementValue(await TestHttpContext.ReadBodyAsync(create), "UploadId");

        var upload = TestHttpContext.CreateContext(
            body: "hello multipart",
            method: HttpMethods.Put,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(uploadId) + "&partNumber=1");
        await MultipartHandlers.HandleAsync(upload, Route(S3Operation.UploadPart, "bucket", "object.txt"), blob, CancellationToken.None);

        var partEtag = upload.Response.Headers.ETag.ToString();
        Assert.Equal(QuotedMd5("hello multipart"), partEtag);

        var complete = TestHttpContext.CreateContext(
            body: $$"""
                   <CompleteMultipartUpload>
                     <Part><PartNumber>1</PartNumber><ETag>{{partEtag}}</ETag></Part>
                   </CompleteMultipartUpload>
                   """,
            method: HttpMethods.Post,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(uploadId));
        await MultipartHandlers.HandleAsync(complete, Route(S3Operation.CompleteMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, complete.Response.StatusCode);
        Assert.Equal("\"abcd0000000000000000000000000000-1\"", ElementValue(await TestHttpContext.ReadBodyAsync(complete), "ETag"));

        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("/bucket/object.txt?comp=blocklist", handler.Requests[2].RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.Equal(
            [UploadIdCodec.BlockId(DecodeUploadId(uploadId).NonceHex, 1)],
            XDocument.Parse(handler.Requests[2].Body!).Root!.Elements("Latest").Select(static e => e.Value).ToArray());
    }

    [Fact]
    public async Task Create_upload_multiple_parts_and_complete_commits_parts_in_order()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, eTag: "\"0xABCDEF\""));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var create = TestHttpContext.CreateContext(method: HttpMethods.Post, path: "/bucket/object.txt", queryString: "?uploads");
        await MultipartHandlers.HandleAsync(create, Route(S3Operation.CreateMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);
        var uploadId = ElementValue(await TestHttpContext.ReadBodyAsync(create), "UploadId");
        var token = DecodeUploadId(uploadId);

        var upload1 = TestHttpContext.CreateContext(
            body: "hello ",
            method: HttpMethods.Put,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(uploadId) + "&partNumber=1");
        await MultipartHandlers.HandleAsync(upload1, Route(S3Operation.UploadPart, "bucket", "object.txt"), blob, CancellationToken.None);

        var upload2 = TestHttpContext.CreateContext(
            body: "world",
            method: HttpMethods.Put,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(uploadId) + "&partNumber=2");
        await MultipartHandlers.HandleAsync(upload2, Route(S3Operation.UploadPart, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(QuotedMd5("hello "), upload1.Response.Headers.ETag.ToString());
        Assert.Equal(QuotedMd5("world"), upload2.Response.Headers.ETag.ToString());

        var complete = TestHttpContext.CreateContext(
            body: $$"""
                   <CompleteMultipartUpload>
                     <Part><PartNumber>1</PartNumber><ETag>{{upload1.Response.Headers.ETag}}</ETag></Part>
                     <Part><PartNumber>2</PartNumber><ETag>{{upload2.Response.Headers.ETag}}</ETag></Part>
                   </CompleteMultipartUpload>
                   """,
            method: HttpMethods.Post,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(uploadId));
        await MultipartHandlers.HandleAsync(complete, Route(S3Operation.CompleteMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, complete.Response.StatusCode);
        Assert.Equal("\"abcdef00000000000000000000000000-2\"", ElementValue(await TestHttpContext.ReadBodyAsync(complete), "ETag"));

        Assert.Collection(
            handler.Requests.Skip(1).Select(r => r.RequestUri!.PathAndQuery),
            path => Assert.EndsWith("/bucket/object.txt?comp=block&blockid=" + Uri.EscapeDataString(UploadIdCodec.BlockId(token.NonceHex, 1)), path, StringComparison.Ordinal),
            path => Assert.EndsWith("/bucket/object.txt?comp=block&blockid=" + Uri.EscapeDataString(UploadIdCodec.BlockId(token.NonceHex, 2)), path, StringComparison.Ordinal),
            path => Assert.EndsWith("/bucket/object.txt?comp=blocklist", path, StringComparison.Ordinal));

        Assert.Equal(
            [UploadIdCodec.BlockId(token.NonceHex, 1), UploadIdCodec.BlockId(token.NonceHex, 2)],
            XDocument.Parse(handler.Requests[3].Body!).Root!.Elements("Latest").Select(static e => e.Value).ToArray());
    }

    [Fact]
    public async Task Upload_part_copy_forwards_range_and_returns_synthetic_etag_when_azure_omits_md5()
    {
        var token = UploadIdCodec.Issue(AccountName, "dest-bucket", "dest.txt", AccountKeyBytes);
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, lastModified: new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(token.Encoded) + "&partNumber=3",
            headers:
            [
                new KeyValuePair<string, string>("x-amz-copy-source", "/source-bucket/source.txt"),
                new KeyValuePair<string, string>("x-amz-copy-source-range", "bytes=1-3")
            ]);

        await MultipartHandlers.HandleAsync(context, Route(S3Operation.UploadPartCopy, "dest-bucket", "dest.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        var blockId = UploadIdCodec.BlockId(token.NonceHex, 3);
        Assert.Equal("\"" + Md5Hex(blockId) + "\"", ElementValue(await TestHttpContext.ReadBodyAsync(context), "ETag"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("bytes=1-3", Assert.Single(request.Headers["x-ms-source-range"]));
        Assert.StartsWith("https://acct.blob.core.windows.net/source-bucket/source.txt?sv=", Assert.Single(request.Headers["x-ms-copy-source"]), StringComparison.Ordinal);
        Assert.Equal(string.Empty, request.Body);
        Assert.EndsWith("/dest-bucket/dest.txt?comp=block&blockid=" + Uri.EscapeDataString(blockId), request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_part_copy_with_invalid_range_returns_invalid_argument_without_calling_azure()
    {
        var token = UploadIdCodec.Issue(AccountName, "dest-bucket", "dest.txt", AccountKeyBytes);
        var handler = new ScriptedHandler();

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(token.Encoded) + "&partNumber=1",
            headers:
            [
                new KeyValuePair<string, string>("x-amz-copy-source", "/source-bucket/source.txt"),
                new KeyValuePair<string, string>("x-amz-copy-source-range", "bytes=9-3")
            ]);

        await MultipartHandlers.HandleAsync(context, Route(S3Operation.UploadPartCopy, "dest-bucket", "dest.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abort_multipart_upload_probes_bucket_and_returns_no_content()
    {
        var token = UploadIdCodec.Issue(AccountName, "bucket", "object.txt", AccountKeyBytes);
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Delete,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(token.Encoded));

        await MultipartHandlers.HandleAsync(context, Route(S3Operation.AbortMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, request.Method);
        Assert.EndsWith("/bucket?restype=container", request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_parts_applies_marker_truncation_and_synthetic_etags()
    {
        var token = UploadIdCodec.Issue(AccountName, "bucket", "object.txt", AccountKeyBytes);
        var block2 = UploadIdCodec.BlockId(token.NonceHex, 2);
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($$"""
                <?xml version="1.0" encoding="utf-8"?>
                <BlockList>
                  <UncommittedBlocks>
                    <Block><Name>{{UploadIdCodec.BlockId(token.NonceHex, 1)}}</Name><Size>11</Size></Block>
                    <Block><Name>{{UploadIdCodec.BlockId("1111111111111111", 9)}}</Name><Size>99</Size></Block>
                    <Block><Name>{{block2}}</Name><Size>12</Size></Block>
                    <Block><Name>{{UploadIdCodec.BlockId(token.NonceHex, 3)}}</Name><Size>13</Size></Block>
                  </UncommittedBlocks>
                </BlockList>
                """))
        });

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(token.Encoded) + "&part-number-marker=1&max-parts=1");

        await MultipartHandlers.HandleAsync(context, Route(S3Operation.ListParts, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var xml = await TestHttpContext.ReadBodyAsync(context);
        Assert.Equal("1", ElementValue(xml, "PartNumberMarker"));
        Assert.Equal("2", ElementValue(xml, "NextPartNumberMarker"));
        Assert.Equal("1", ElementValue(xml, "MaxParts"));
        Assert.Equal("true", ElementValue(xml, "IsTruncated"));

        var doc = XDocument.Parse(xml);
        var part = Assert.Single(doc.Root!.Elements(S3Ns + "Part"));
        Assert.Equal("2", part.Element(S3Ns + "PartNumber")!.Value);
        Assert.Equal("\"" + Md5Hex(block2) + "\"", part.Element(S3Ns + "ETag")!.Value);
        Assert.Equal("12", part.Element(S3Ns + "Size")!.Value);
    }

    [Fact]
    public async Task List_multipart_uploads_returns_empty_page_with_requested_prefix_and_delimiter()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?uploads&prefix=logs/&delimiter=/");

        await MultipartHandlers.HandleAsync(context, Route(S3Operation.ListMultipartUploads, "bucket", null), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var xml = await TestHttpContext.ReadBodyAsync(context);
        Assert.Equal("bucket", ElementValue(xml, "Bucket"));
        Assert.Equal("logs/", ElementValue(xml, "Prefix"));
        Assert.Equal("/", ElementValue(xml, "Delimiter"));
        Assert.Equal("1000", ElementValue(xml, "MaxUploads"));
        Assert.Equal("false", ElementValue(xml, "IsTruncated"));
        Assert.Empty(XDocument.Parse(xml).Root!.Elements(S3Ns + "Upload"));
    }

    private static BlobClient NewBlobClient(AzureHttpClient http) =>
        new(http, new BlobCredentials
        {
            AccountName = AccountName,
            AccountKey = AccountKeyBase64,
        });

    private static S3RouteResult Route(S3Operation operation, string? bucket, string? key) =>
        new(operation, bucket, key, VirtualHosted: false);

    private static UploadIdCodec.UploadToken DecodeUploadId(string uploadId) =>
        UploadIdCodec.TryDecode(uploadId, AccountName, "bucket", "object.txt", AccountKeyBytes)
        ?? throw new Xunit.Sdk.XunitException("UploadId should round-trip in tests.");

    private static HttpResponseMessage AzureResponse(
        HttpStatusCode statusCode,
        string? eTag = null,
        DateTimeOffset? lastModified = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        if (eTag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", eTag);
        }
        if (lastModified is not null)
        {
            response.Content.Headers.LastModified = lastModified;
        }
        return response;
    }

    private static string ElementValue(string xml, string localName) =>
        XDocument.Parse(xml).Root!.Element(S3Ns + localName)!.Value;

    private static string QuotedMd5(string value) => "\"" + Md5Hex(value) + "\"";

    private static string Md5Hex(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
