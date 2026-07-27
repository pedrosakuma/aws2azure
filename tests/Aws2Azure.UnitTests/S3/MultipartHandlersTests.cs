using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
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
    private const string Generation = "\"gen-1\"";
    private const string RecreatedGeneration = "\"gen-2\"";
    private const string LeaseId = "lease-123";

    [Fact]
    public async Task Create_upload_single_part_and_complete_returns_multipart_etag()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var upload = await InitiateAsync(handler, blob, "bucket", "object.txt");

        handler.Enqueue(StateHead(upload));
        handler.Enqueue(ContainerHead());
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));

        var uploadPart = TestHttpContext.CreateContext(
            body: "hello multipart",
            method: HttpMethods.Put,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(upload.UploadId) + "&partNumber=1");
        await MultipartHandlers.HandleAsync(uploadPart, Route(S3Operation.UploadPart, "bucket", "object.txt"), blob, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, uploadPart.Response.StatusCode);
        var partEtag = uploadPart.Response.Headers.ETag.ToString();
        Assert.Equal(QuotedMd5("hello multipart"), partEtag);

        handler.Enqueue(StateGet(upload));
        handler.Enqueue(ContainerHead());
        handler.Enqueue(LeaseAcquired());
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, eTag: "\"0xABCD\""));
        handler.Enqueue(StateDeleted());

        var complete = TestHttpContext.CreateContext(
            body: $$"""
                   <CompleteMultipartUpload>
                     <Part><PartNumber>1</PartNumber><ETag>{{partEtag}}</ETag></Part>
                   </CompleteMultipartUpload>
                   """,
            method: HttpMethods.Post,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(upload.UploadId));
        await MultipartHandlers.HandleAsync(complete, Route(S3Operation.CompleteMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, complete.Response.StatusCode);
        Assert.Equal("\"abcd0000000000000000000000000000-1\"", ElementValue(await TestHttpContext.ReadBodyAsync(complete), "ETag"));

        Assert.Equal(13, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[3].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[10].Method);
        Assert.Equal(HttpMethod.Delete, handler.Requests[12].Method);
        Assert.EndsWith("/bucket/object.txt?comp=blocklist", handler.Requests[11].RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.Equal("application/xml", Assert.Single(handler.Requests[11].ContentHeaders["Content-Type"]));
        Assert.Equal(
            [UploadIdCodec.BlockId(upload.Token.NonceHex, 1)],
            XDocument.Parse(handler.Requests[11].Body!).Root!.Elements("Latest").Select(static e => e.Value).ToArray());
    }

    [Fact]
    public async Task Upload_part_copy_forwards_range_and_returns_synthetic_etag_when_azure_omits_md5()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var upload = await InitiateAsync(handler, blob, "dest-bucket", "dest.txt");

        handler.Enqueue(StateHead(upload));
        handler.Enqueue(ContainerHead());
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, lastModified: new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero)));

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(upload.UploadId) + "&partNumber=3",
            headers:
            [
                new KeyValuePair<string, string>("x-amz-copy-source", "/source-bucket/source.txt"),
                new KeyValuePair<string, string>("x-amz-copy-source-range", "bytes=1-3")
            ]);

        await MultipartHandlers.HandleAsync(context, Route(S3Operation.UploadPartCopy, "dest-bucket", "dest.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var blockId = UploadIdCodec.BlockId(upload.Token.NonceHex, 3);
        Assert.Equal("\"" + Md5Hex(blockId) + "\"", ElementValue(await TestHttpContext.ReadBodyAsync(context), "ETag"));

        var request = handler.Requests[^1];
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("bytes=1-3", Assert.Single(request.Headers["x-ms-source-range"]));
        Assert.StartsWith("https://acct.blob.core.windows.net/source-bucket/source.txt?sv=", Assert.Single(request.Headers["x-ms-copy-source"]), StringComparison.Ordinal);
        Assert.EndsWith("/dest-bucket/dest.txt?comp=block&blockid=" + Uri.EscapeDataString(blockId), request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_part_copy_with_invalid_range_returns_invalid_argument_without_calling_azure()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var token = UploadIdCodec.Issue(AccountName, "dest-bucket", "dest.txt", AccountKeyBytes);

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
        Assert.Empty(handler.Requests);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abort_multipart_upload_deletes_state_and_future_use_returns_no_such_upload()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var upload = await InitiateAsync(handler, blob, "bucket", "object.txt");

        handler.Enqueue(StateGet(upload));
        handler.Enqueue(ContainerHead());
        handler.Enqueue(LeaseAcquired());
        handler.Enqueue(StateDeleted());

        var abort = TestHttpContext.CreateContext(
            method: HttpMethods.Delete,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(upload.UploadId));
        await MultipartHandlers.HandleAsync(abort, Route(S3Operation.AbortMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, abort.Response.StatusCode);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);

        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        var reuse = TestHttpContext.CreateContext(
            body: "stale",
            method: HttpMethods.Put,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(upload.UploadId) + "&partNumber=1");
        await MultipartHandlers.HandleAsync(reuse, Route(S3Operation.UploadPart, "bucket", "object.txt"), blob, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, reuse.Response.StatusCode);
        Assert.Contains("NoSuchUpload", await TestHttpContext.ReadBodyAsync(reuse), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_parts_applies_marker_truncation_and_synthetic_etags()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var upload = await InitiateAsync(handler, blob, "bucket", "object.txt");
        var block2 = UploadIdCodec.BlockId(upload.Token.NonceHex, 2);

        handler.Enqueue(StateHead(upload));
        handler.Enqueue(ContainerHead());
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($$"""
                <?xml version="1.0" encoding="utf-8"?>
                <BlockList>
                  <UncommittedBlocks>
                    <Block><Name>{{UploadIdCodec.BlockId(upload.Token.NonceHex, 1)}}</Name><Size>11</Size></Block>
                    <Block><Name>{{UploadIdCodec.BlockId("1111111111111111", 9)}}</Name><Size>99</Size></Block>
                    <Block><Name>{{block2}}</Name><Size>12</Size></Block>
                    <Block><Name>{{UploadIdCodec.BlockId(upload.Token.NonceHex, 3)}}</Name><Size>13</Size></Block>
                  </UncommittedBlocks>
                </BlockList>
                """))
        });

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(upload.UploadId) + "&part-number-marker=1&max-parts=1");

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
    public async Task List_multipart_uploads_orders_by_utf8_key_and_paginates_by_real_markers()
    {
        var a = await CaptureUploadAsync("bucket", "a");
        var accent = await CaptureUploadAsync("bucket", "é.txt");
        var emoji = await CaptureUploadAsync("bucket", "😀.txt");

        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        handler.Enqueue(ContainerHead());
        handler.Enqueue(BlobList(OldStateBlob("bucket", "expired-1", DateTimeOffset.UtcNow - UploadIdCodec.MaxAge - TimeSpan.FromMinutes(1))));
        handler.Enqueue(StateDeleted());
        handler.Enqueue(ContainerHead());
        handler.Enqueue(BlobList(StateBlob(a), StateBlob(emoji), StateBlob(accent)));

        var page1 = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?uploads&max-uploads=2");
        await MultipartHandlers.HandleAsync(page1, Route(S3Operation.ListMultipartUploads, "bucket", null), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, page1.Response.StatusCode);
        var xml1 = await TestHttpContext.ReadBodyAsync(page1);
        var doc1 = XDocument.Parse(xml1);
        Assert.Equal(new[] { "a", "é.txt" }, doc1.Root!.Elements(S3Ns + "Upload").Select(u => u.Element(S3Ns + "Key")!.Value).ToArray());
        Assert.Equal("true", ElementValue(xml1, "IsTruncated"));
        Assert.Equal("é.txt", ElementValue(xml1, "NextKeyMarker"));
        Assert.Equal(accent.UploadId, ElementValue(xml1, "NextUploadIdMarker"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath.Contains("expired-1", StringComparison.Ordinal));

        handler.Enqueue(ContainerHead());
        handler.Enqueue(BlobList());
        handler.Enqueue(ContainerHead());
        handler.Enqueue(BlobList(StateBlob(a), StateBlob(emoji), StateBlob(accent)));

        var page2 = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?uploads&max-uploads=2&key-marker=" + Uri.EscapeDataString("é.txt") + "&upload-id-marker=" + Uri.EscapeDataString(accent.UploadId));
        await MultipartHandlers.HandleAsync(page2, Route(S3Operation.ListMultipartUploads, "bucket", null), blob, CancellationToken.None);

        var xml2 = await TestHttpContext.ReadBodyAsync(page2);
        Assert.Equal(new[] { "😀.txt" }, XDocument.Parse(xml2).Root!.Elements(S3Ns + "Upload").Select(u => u.Element(S3Ns + "Key")!.Value).ToArray());
        Assert.Equal("false", ElementValue(xml2, "IsTruncated"));
    }

    [Fact]
    public async Task List_multipart_uploads_with_max_uploads_zero_returns_invalid_argument()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(ContainerHead());
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(method: HttpMethods.Get, path: "/bucket", queryString: "?uploads&max-uploads=0");
        await MultipartHandlers.HandleAsync(context, Route(S3Operation.ListMultipartUploads, "bucket", null), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_rejects_bucket_generation_race_and_cleans_written_state()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(ContainerHead(Generation));
        handler.Enqueue(StateContainerCreated());
        handler.Enqueue(BlobList());
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        handler.Enqueue(ContainerHead(RecreatedGeneration));
        handler.Enqueue(StateDeleted());

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Post, path: "/bucket/object.txt", queryString: "?uploads");

        await MultipartHandlers.HandleAsync(context, Route(S3Operation.CreateMultipartUpload, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Contains("OperationAborted", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Fact]
    public async Task Stale_upload_state_from_recreated_bucket_returns_no_such_upload()
    {
        var upload = await CaptureUploadAsync("bucket", "object.txt");
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        handler.Enqueue(StateHead(upload, containerGeneration: Generation));
        handler.Enqueue(ContainerHead(RecreatedGeneration));

        var context = TestHttpContext.CreateContext(
            body: "data",
            method: HttpMethods.Put,
            path: "/bucket/object.txt",
            queryString: "?uploadId=" + Uri.EscapeDataString(upload.UploadId) + "&partNumber=1");
        await MultipartHandlers.HandleAsync(context, Route(S3Operation.UploadPart, "bucket", "object.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains("NoSuchUpload", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    private static BlobClient NewBlobClient(AzureHttpClient http) =>
        new(http, new BlobCredentials
        {
            AccountName = AccountName,
            AccountKey = AccountKeyBase64,
        });

    private static S3RouteResult Route(S3Operation operation, string? bucket, string? key) =>
        new(operation, bucket, key, VirtualHosted: false);

    private static async Task<CapturedUpload> InitiateAsync(ScriptedHandler handler, BlobClient blob, string bucket, string key)
    {
        handler.Enqueue(ContainerHead());
        handler.Enqueue(StateContainerCreated());
        handler.Enqueue(BlobList());
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        handler.Enqueue(ContainerHead());

        var create = TestHttpContext.CreateContext(
            method: HttpMethods.Post,
            path: "/" + bucket + "/" + key,
            queryString: "?uploads",
            headers:
            [
                new KeyValuePair<string, string>("Content-Type", "application/octet-stream"),
                new KeyValuePair<string, string>("x-amz-meta-owner", "pedro")
            ]);
        await MultipartHandlers.HandleAsync(create, Route(S3Operation.CreateMultipartUpload, bucket, key), blob, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, create.Response.StatusCode);
        var uploadId = ElementValue(await TestHttpContext.ReadBodyAsync(create), "UploadId");
        var token = UploadIdCodec.TryDecode(uploadId, AccountName, bucket, key, AccountKeyBytes)
            ?? throw new Xunit.Sdk.XunitException("UploadId should round-trip in tests.");
        return new CapturedUpload(
            bucket,
            key,
            uploadId,
            token,
            blob.MultipartStateContainerName);
    }

    private static async Task<CapturedUpload> CaptureUploadAsync(string bucket, string key)
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        return await InitiateAsync(handler, blob, bucket, key);
    }

    private static HttpResponseMessage ContainerHead(string eTag = Generation)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", eTag);
        return response;
    }

    private static HttpResponseMessage StateContainerCreated() => new(HttpStatusCode.Created);

    private static HttpResponseMessage LeaseAcquired()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created);
        response.Headers.TryAddWithoutValidation("x-ms-lease-id", LeaseId);
        return response;
    }

    private static HttpResponseMessage StateDeleted() => new(HttpStatusCode.Accepted);

    private static HttpResponseMessage StateHead(CapturedUpload upload, string? containerGeneration = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var entry in BuildStateMetadata(upload, containerGeneration))
        {
            response.Headers.TryAddWithoutValidation("x-ms-meta-" + entry.Key, entry.Value);
        }
        return response;
    }

    private static HttpResponseMessage StateGet(CapturedUpload upload, string? containerGeneration = null)
    {
        var response = StateHead(upload, containerGeneration);
        response.Content = new ByteArrayContent(SerializeStateBody());
        return response;
    }

    private static HttpResponseMessage BlobList(params BlobListEntry[] entries)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults><Blobs>");
        foreach (var entry in entries)
        {
            xml.Append("<Blob><Name>").Append(SecurityElementEscape(entry.Name)).Append("</Name><Metadata>");
            foreach (var metadata in entry.Metadata)
            {
                xml.Append('<').Append(metadata.Key).Append('>')
                    .Append(SecurityElementEscape(metadata.Value))
                    .Append("</").Append(metadata.Key).Append('>');
            }
            xml.Append("</Metadata></Blob>");
        }
        xml.Append("</Blobs><NextMarker /></EnumerationResults>");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml.ToString()))
        };
    }

    private static BlobListEntry StateBlob(CapturedUpload upload, string? generation = null) =>
        new(upload.RecordName, BuildStateMetadata(upload, generation));

    private static BlobListEntry OldStateBlob(string bucket, string uploadId, DateTimeOffset createdAt)
    {
        var name = $"{bucket}/{createdAt.ToUnixTimeMilliseconds():D13}-{uploadId}";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws2azurekey"] = UploadIdCodec.Base64Url.Encode(Encoding.UTF8.GetBytes("expired.txt")),
            ["aws2azureuploadid"] = uploadId,
            ["aws2azureinitiatedms"] = createdAt.ToUnixTimeMilliseconds().ToString(),
            ["aws2azurecontaineretag"] = Generation,
        };
        return new BlobListEntry(name, metadata);
    }

    private static Dictionary<string, string> BuildStateMetadata(CapturedUpload upload, string? generation = null) =>
        new(StringComparer.Ordinal)
        {
            ["aws2azurekey"] = UploadIdCodec.Base64Url.Encode(Encoding.UTF8.GetBytes(upload.Key)),
            ["aws2azureuploadid"] = upload.UploadId,
            ["aws2azureinitiatedms"] = upload.Token.CreatedAt.ToUnixTimeMilliseconds().ToString(),
            ["aws2azurecontaineretag"] = generation ?? Generation,
        };

    private static byte[] SerializeStateBody()
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("A2MP1"));
        stream.WriteByte(1); // Content-Type present
        WriteUtf8String(stream, "application/octet-stream");
        stream.WriteByte(0);
        stream.WriteByte(1); // metadata count = 1 (big-endian ushort)
        WriteUtf8String(stream, "owner");
        WriteUtf8String(stream, "pedro");
        return stream.ToArray();
    }

    private static void WriteUtf8String(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte((byte)(bytes.Length >> 8));
        stream.WriteByte((byte)bytes.Length);
        stream.Write(bytes);
    }

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

    private static string SecurityElementEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private readonly record struct CapturedUpload(
        string Bucket,
        string Key,
        string UploadId,
        UploadIdCodec.UploadToken Token,
        string StateContainerName)
    {
        public string RecordName => $"{Bucket}/{Token.CreatedAt.ToUnixTimeMilliseconds():D13}-{UploadId}";
    }

    private readonly record struct BlobListEntry(string Name, IReadOnlyDictionary<string, string> Metadata);
}
