using System.Net;
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

public sealed class CopyObjectHandlersTests
{
    private const string AccountName = "acct";
    private const string AccountKeyBase64 = "dGVzdC1rZXktQS0xMjM0NTY3ODkwYWJjZGVm";
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public async Task Copy_object_without_copy_source_returns_invalid_argument()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt");

        await ObjectHandlers.HandleAsync(context, Route(S3Operation.CopyObject, "dest-bucket", "dest.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Copy_object_success_returns_copy_result_body_and_version_header()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.OK, eTag: "\"0xabc\"", versionId: "v-copy", lastModified: new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero)));
        handler.Enqueue(AzureResponse(HttpStatusCode.OK, eTag: "\"0xabc\"", versionId: "v-copy", lastModified: new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero)));

        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt",
            headers:
            [
                new KeyValuePair<string, string>("x-amz-copy-source", "/source-bucket/source.txt")
            ]);

        await ObjectHandlers.HandleAsync(context, Route(S3Operation.CopyObject, "dest-bucket", "dest.txt"), blob, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var xml = await TestHttpContext.ReadBodyAsync(context);
        var doc = XDocument.Parse(xml);
        Assert.Equal("CopyObjectResult", doc.Root!.Name.LocalName);
        Assert.False(string.IsNullOrEmpty(doc.Root!.Element(S3Ns + "ETag")?.Value));
        Assert.False(string.IsNullOrEmpty(doc.Root!.Element(S3Ns + "LastModified")?.Value));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static BlobClient NewBlobClient(AzureHttpClient http) =>
        new(http, new BlobCredentials
        {
            AccountName = AccountName,
            AccountKey = AccountKeyBase64,
        });

    private static S3RouteResult Route(S3Operation operation, string? bucket, string? key) =>
        new(operation, bucket, key, VirtualHosted: false);

    private static HttpResponseMessage AzureResponse(
        HttpStatusCode statusCode,
        string? eTag = null,
        string? versionId = null,
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
        if (versionId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ms-version-id", versionId);
        }
        if (lastModified is not null)
        {
            response.Content.Headers.LastModified = lastModified;
        }
        return response;
    }
}
