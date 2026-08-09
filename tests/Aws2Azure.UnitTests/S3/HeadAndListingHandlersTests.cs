using System.Net;
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

public sealed class HeadAndListingHandlersTests
{
    private const string AccountName = "acct";
    private const string AccountKeyBase64 = "dGVzdC1rZXktQS0xMjM0NTY3ODkwYWJjZGVm";
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public async Task HeadBucket_success_is_bodyless_and_has_no_error_header()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Head, path: "/bucket");

        await BucketCrudHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.HeadBucket, "bucket", null, VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.ContentLength);
        Assert.False(context.Response.Headers.ContainsKey("x-amz-error-code"));
        Assert.Equal(string.Empty, await TestHttpContext.ReadBodyAsync(context));
    }

    [Fact]
    public async Task HeadObject_success_is_bodyless_and_copies_metadata_headers()
    {
        var handler = new ScriptedHandler();
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", "\"azure-etag\"");
        response.Content = new ByteArrayContent(Array.Empty<byte>());
        response.Content.Headers.ContentLength = 7;
        response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-08-09T01:02:03Z");
        handler.Enqueue(response);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Head, path: "/bucket/object.txt");

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.HeadObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("\"1fea515e84c3e975ea288c1fac2d916f\"", context.Response.Headers.ETag.ToString());
        Assert.Equal("7", context.Response.Headers.ContentLength.ToString());
        Assert.True(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.Equal(string.Empty, await TestHttpContext.ReadBodyAsync(context));
    }

    [Fact]
    public async Task HeadObject_precondition_failed_clears_success_metadata_and_sets_error_header()
    {
        var handler = new ScriptedHandler();
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", "\"etag-1\"");
        response.Content = new ByteArrayContent(Array.Empty<byte>());
        response.Content.Headers.ContentLength = 99;
        response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-08-09T01:02:03Z");
        handler.Enqueue(response);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Head,
            path: "/bucket/object.txt",
            headers: [new KeyValuePair<string, string>("If-Match", "\"different\"")]);

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.HeadObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        Assert.Equal("PreconditionFailed", context.Response.Headers["x-amz-error-code"]);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.Equal("0", context.Response.Headers.ContentLength.ToString());
        Assert.Equal(string.Empty, await TestHttpContext.ReadBodyAsync(context));
    }

    [Fact]
    public async Task ListBuckets_includes_owner_and_bucket_creation_dates()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <?xml version="1.0" encoding="utf-8"?>
                <EnumerationResults>
                  <Containers>
                    <Container>
                      <Name>alpha</Name>
                      <Properties>
                        <Last-Modified>Tue, 02 Jan 2024 03:04:05 GMT</Last-Modified>
                      </Properties>
                    </Container>
                    <Container>
                      <Name>beta</Name>
                      <Properties>
                        <Last-Modified>Wed, 03 Jan 2024 04:05:06 GMT</Last-Modified>
                      </Properties>
                    </Container>
                  </Containers>
                  <NextMarker />
                </EnumerationResults>
                """)
        });
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Get, path: "/");
        context.Items["aws2azure.accessKeyId"] = "AKIA-test";

        await BucketCrudHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.ListBuckets, null, null, VirtualHosted: false),
            blob,
            CancellationToken.None);

        var doc = XDocument.Parse(await TestHttpContext.ReadBodyAsync(context));
        Assert.Equal("AKIA-test", doc.Root!.Element(S3Ns + "Owner")!.Element(S3Ns + "ID")!.Value);
        var buckets = doc.Root!.Element(S3Ns + "Buckets")!.Elements(S3Ns + "Bucket").ToArray();
        Assert.Equal(["alpha", "beta"], buckets.Select(b => b.Element(S3Ns + "Name")!.Value).ToArray());
        Assert.All(buckets, bucket => Assert.False(string.IsNullOrEmpty(bucket.Element(S3Ns + "CreationDate")!.Value)));
    }

    [Fact]
    public async Task ListObjects_v1_with_delimiter_emits_next_marker_only_when_truncated()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <?xml version="1.0" encoding="utf-8"?>
                <EnumerationResults>
                  <Blobs>
                    <BlobPrefix><Name>page/a/</Name></BlobPrefix>
                  </Blobs>
                  <NextMarker>page/b/</NextMarker>
                </EnumerationResults>
                """)
        });
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?delimiter=/&max-keys=1&prefix=page/");

        await ObjectListHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.ListObjects, "bucket", null, VirtualHosted: false),
            blob,
            CancellationToken.None);

        var doc = XDocument.Parse(await TestHttpContext.ReadBodyAsync(context));
        Assert.Equal("true", doc.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal("page/a/", doc.Root!.Element(S3Ns + "CommonPrefixes")!.Element(S3Ns + "Prefix")!.Value);
        Assert.Equal("page/b/", doc.Root!.Element(S3Ns + "NextMarker")!.Value);
    }

    private static BlobClient NewBlobClient(AzureHttpClient http) =>
        new(http, new BlobCredentials
        {
            AccountName = AccountName,
            AccountKey = AccountKeyBase64,
        });

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responses.Dequeue());
    }
}
