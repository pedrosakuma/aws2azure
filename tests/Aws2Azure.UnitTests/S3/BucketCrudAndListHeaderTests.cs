using System.Net;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Operations;
using Aws2Azure.TestSupport.Http;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

public sealed class BucketCrudAndListHeaderTests
{
    private const string AccountName = "acct";
    private const string AccountKeyBase64 = "dGVzdC1rZXktQS0xMjM0NTY3ODkwYWJjZGVm";

    [Fact]
    public async Task CreateBucket_does_not_emit_bucket_region_header()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        handler.Enqueue(ContainerHeadWithGeneration());
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Put, path: "/bucket");

        await BucketCrudHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.CreateBucket, "bucket", null, VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-region"));
        Assert.Equal("arn:aws:s3:::bucket", context.Response.Headers["x-amz-bucket-arn"]);
    }

    [Fact]
    public async Task DeleteBucket_does_not_emit_bucket_region_or_arn_headers()
    {
        var handler = new ScriptedHandler();
        // GetContainerPropertiesAsync probe
        handler.Enqueue(ContainerHeadWithGeneration());
        // DeleteContainerAsync response
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Delete, path: "/bucket");

        await BucketCrudHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.DeleteBucket, "bucket", null, VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        // Regression guard for #857 (B4): DeleteBucket 204 must not carry
        // x-amz-bucket-region / x-amz-bucket-arn — those are HeadBucket-scoped
        // on real S3.
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-region"));
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-arn"));
    }

    [Fact]
    public async Task HeadBucket_still_emits_bucket_region_and_arn_headers()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(ContainerHeadWithGeneration());
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Head, path: "/bucket");

        await BucketCrudHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.HeadBucket, "bucket", null, VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("us-east-1", context.Response.Headers["x-amz-bucket-region"]);
        Assert.Equal("arn:aws:s3:::bucket", context.Response.Headers["x-amz-bucket-arn"]);
    }

    [Fact]
    public async Task ListObjectsV2_emits_bucket_region_header_without_bucket_arn()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <?xml version="1.0" encoding="utf-8"?>
                <EnumerationResults>
                  <MaxResults>1000</MaxResults>
                  <Blobs />
                  <NextMarker />
                </EnumerationResults>
                """)
        });
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Get, path: "/bucket", queryString: "?list-type=2");

        await ObjectListHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.ListObjectsV2, "bucket", null, VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("us-east-1", context.Response.Headers["x-amz-bucket-region"]);
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-arn"));
    }

    private static BlobClient NewBlobClient(AzureHttpClient http) =>
        new(http, new BlobCredentials
        {
            AccountName = AccountName,
            AccountKey = AccountKeyBase64,
        });

    private static HttpResponseMessage ContainerHeadWithGeneration()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", "\"container-etag\"");
        response.Headers.TryAddWithoutValidation("x-ms-meta-aws2azuregeneration", "gen-1");
        return response;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responses.Dequeue());
    }
}
