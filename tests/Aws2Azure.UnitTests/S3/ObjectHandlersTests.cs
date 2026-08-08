using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
using Aws2Azure.Modules.S3;
using Aws2Azure.Modules.S3.Internal;
using Aws2Azure.Modules.S3.Operations;
using Aws2Azure.TestSupport.Http;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.S3;

public sealed class ObjectHandlersTests
{
    private const string AccountName = "acct";
    private const string AccountKeyBase64 = "dGVzdC1rZXktQS0xMjM0NTY3ODkwYWJjZGVm";
    private const string AccessKeyId = "AKIA690TEST";
    private const string SecretKey = "secret-690";
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public async Task GetObject_response_content_overrides_replace_headers_without_touching_body()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(GetResponse("hello"u8.ToArray(), contentType: "application/octet-stream"));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket/object.txt",
            queryString: "?response-content-type=text/plain&response-content-disposition=attachment%3B%20filename%3Doverride.txt&response-cache-control=no-cache");

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.GetObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.Headers["Content-Type"].ToString());
        Assert.Equal("attachment; filename=override.txt", context.Response.Headers["Content-Disposition"].ToString());
        Assert.Equal("no-cache", context.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("hello", await TestHttpContext.ReadBodyAsync(context));
    }

    [Fact]
    public async Task PutObject_does_not_emit_bucket_headers_and_keeps_checksum_type()
    {
        var handler = new ScriptedHandler();
        var putResponse = AzureResponse(HttpStatusCode.Created, eTag: "\"0xPUT\"");
        putResponse.Headers.TryAddWithoutValidation("x-ms-version-id", "2026-08-08T12:00:00.0000000Z");
        handler.Enqueue(putResponse);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(body: "hello", method: HttpMethods.Put, path: "/bucket/object.txt");

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.PutObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-region"));
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-arn"));
        Assert.Equal("FULL_OBJECT", context.Response.Headers["x-amz-checksum-type"]);
        Assert.Equal("AES256", context.Response.Headers["x-amz-server-side-encryption"]);
        Assert.Equal(S3VersionIdCodec.Encode("2026-08-08T12:00:00.0000000Z"), context.Response.Headers["x-amz-version-id"]);
    }

    [Fact]
    public async Task GetObject_does_not_emit_bucket_headers_or_checksum_type()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(GetResponse("hello"u8.ToArray(), contentType: "application/octet-stream"));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Get, path: "/bucket/object.txt");

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.GetObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-region"));
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-arn"));
        Assert.False(context.Response.Headers.ContainsKey("x-amz-checksum-type"));
        Assert.Equal("AES256", context.Response.Headers["x-amz-server-side-encryption"]);
    }

    [Fact]
    public async Task DeleteObject_does_not_emit_bucket_headers_or_content_type()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Accepted));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Delete, path: "/bucket/object.txt");

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.DeleteObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-region"));
        Assert.False(context.Response.Headers.ContainsKey("x-amz-bucket-arn"));
        Assert.False(context.Response.Headers.ContainsKey("Content-Type"));
    }

    [Fact]
    public async Task DeleteObject_with_soft_delete_header_emits_delete_marker()
    {
        var handler = new ScriptedHandler();
        var response = AzureResponse(HttpStatusCode.Accepted);
        response.Headers.TryAddWithoutValidation("x-ms-delete-type-permanent", "false");
        handler.Enqueue(response);
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);
        var context = TestHttpContext.CreateContext(method: HttpMethods.Delete, path: "/bucket/object.txt");

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.DeleteObject, "bucket", "object.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal("true", context.Response.Headers["x-amz-delete-marker"]);
    }

    [Fact]
    public async Task CopyObject_with_versioned_source_and_concrete_if_match_heads_source_and_forwards_version()
    {
        var sourceMd5 = Convert.ToBase64String(MD5.HashData("source"u8.ToArray()));
        var handler = new ScriptedHandler();
        handler.Enqueue(HeadResponse(etag: "\"0xSOURCE\"", contentMd5Base64: sourceMd5));
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, eTag: "\"0xCOPY\"", lastModified: DateTimeOffset.Parse("2026-07-28T20:10:00Z")));
        handler.Enqueue(HeadResponse(etag: "\"0xDEST\"", contentMd5Base64: sourceMd5));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var translatedSourceEtag = "\"" + Convert.ToHexString(MD5.HashData("source"u8.ToArray())).ToLowerInvariant() + "\"";
        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt",
            headers:
            [
                new KeyValuePair<string, string>("x-amz-copy-source", "/src-bucket/src.txt?versionId=ver-1"),
                new KeyValuePair<string, string>("x-amz-copy-source-if-match", translatedSourceEtag)
            ]);

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.CopyObject, "dest-bucket", "dest.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.EndsWith("/src-bucket/src.txt?versionid=ver-1", handler.Requests[0].RequestUri!.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("versionid=ver-1", Assert.Single(handler.Requests[1].Headers["x-ms-copy-source"]), StringComparison.Ordinal);
        Assert.DoesNotContain("x-ms-source-if-match", handler.Requests[1].Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("CopyObjectResult", XDocument.Parse(await TestHttpContext.ReadBodyAsync(context)).Root!.Name.LocalName);
    }

    [Fact]
    public async Task CopyObject_with_matching_concrete_if_none_match_returns_precondition_failed_before_copy()
    {
        var sourceMd5 = Convert.ToBase64String(MD5.HashData("source"u8.ToArray()));
        var handler = new ScriptedHandler();
        handler.Enqueue(HeadResponse(etag: "\"0xSOURCE\"", contentMd5Base64: sourceMd5));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var translatedSourceEtag = "\"" + Convert.ToHexString(MD5.HashData("source"u8.ToArray())).ToLowerInvariant() + "\"";
        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt",
            headers:
            [
                new KeyValuePair<string, string>("x-amz-copy-source", "/src-bucket/src.txt"),
                new KeyValuePair<string, string>("x-amz-copy-source-if-none-match", translatedSourceEtag)
            ]);

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.CopyObject, "dest-bucket", "dest.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Contains("PreconditionFailed", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyObject_replace_without_metadata_still_clears_destination_metadata()
    {
        var sourceMd5 = Convert.ToBase64String(MD5.HashData("source"u8.ToArray()));
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, eTag: "\"0xCOPY\"", lastModified: DateTimeOffset.Parse("2026-07-28T20:10:00Z")));
        handler.Enqueue(AzureResponse(HttpStatusCode.OK, eTag: "\"0xPROPS\"", lastModified: DateTimeOffset.Parse("2026-07-28T20:11:00Z")));
        handler.Enqueue(AzureResponse(HttpStatusCode.OK, eTag: "\"0xMETA\"", lastModified: DateTimeOffset.Parse("2026-07-28T20:12:00Z")));
        handler.Enqueue(HeadResponse(etag: "\"0xDEST\"", contentMd5Base64: sourceMd5));
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var blob = NewBlobClient(http);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/dest-bucket/dest.txt",
            headers:
            [
                new KeyValuePair<string, string>("x-amz-copy-source", "/src-bucket/src.txt"),
                new KeyValuePair<string, string>("x-amz-metadata-directive", "REPLACE"),
                new KeyValuePair<string, string>("Content-Type", "text/plain")
            ]);

        await ObjectHandlers.HandleAsync(
            context,
            new S3RouteResult(S3Operation.CopyObject, "dest-bucket", "dest.txt", VirtualHosted: false),
            blob,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(handler.Requests, request => request.RequestUri!.Query == "?comp=metadata");
    }

    [Fact]
    public async Task PresignedPost_uploads_file_with_valid_policy_and_metadata()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(AzureResponse(HttpStatusCode.Created, eTag: "\"0xPOST\""));
        using var http = new AzureHttpClient(handler, ownsHandler: false);

        var (bodyBytes, contentType) = BuildPresignedPostRequest(
            "bucket",
            "upload.txt",
            "hello post"u8.ToArray(),
            additionalFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "text/plain",
                ["x-amz-meta-color"] = "blue"
            });

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Post,
            path: "/bucket",
            contentType: contentType);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        await ObjectHandlers.HandlePostObjectAsync(
            context,
            new S3RouteResult(S3Operation.PostObject, "bucket", null, VirtualHosted: false),
            http,
            Resolver(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/bucket/upload.txt", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("text/plain", Assert.Single(request.ContentHeaders["Content-Type"]));
        Assert.Equal("blue", Assert.Single(request.Headers["x-ms-meta-color"]));
        Assert.Equal("hello post", request.Body);
    }

    [Fact]
    public async Task PresignedPost_with_bad_signature_is_rejected_without_backend_call()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var (bodyBytes, contentType) = BuildPresignedPostRequest("bucket", "upload.txt", "hello post"u8.ToArray(), tamperSignature: true);

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Post,
            path: "/bucket",
            contentType: contentType);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        await ObjectHandlers.HandlePostObjectAsync(
            context,
            new S3RouteResult(S3Operation.PostObject, "bucket", null, VirtualHosted: false),
            http,
            Resolver(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("SignatureDoesNotMatch", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresignedPost_with_success_action_status_201_is_rejected_without_backend_call()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var (bodyBytes, contentType) = BuildPresignedPostRequest(
            "bucket",
            "upload.txt",
            "hello post"u8.ToArray(),
            additionalFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["success_action_status"] = "201",
            });

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Post,
            path: "/bucket",
            contentType: contentType);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        await ObjectHandlers.HandlePostObjectAsync(
            context,
            new S3RouteResult(S3Operation.PostObject, "bucket", null, VirtualHosted: false),
            http,
            Resolver(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("InvalidArgument", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresignedPost_with_unsigned_extra_field_is_rejected_without_backend_call()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var (bodyBytes, contentType) = BuildPresignedPostRequest(
            "bucket",
            "upload.txt",
            "hello post"u8.ToArray(),
            additionalFields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-amz-meta-color"] = "blue",
            },
            unsignedFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "x-amz-meta-color",
            });

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Post,
            path: "/bucket",
            contentType: contentType);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        await ObjectHandlers.HandlePostObjectAsync(
            context,
            new S3RouteResult(S3Operation.PostObject, "bucket", null, VirtualHosted: false),
            http,
            Resolver(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("SignatureDoesNotMatch", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresignedPost_rejects_internal_state_container_bucket()
    {
        var handler = new ScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: false);
        var internalBucket = NewBlobClient(http).MultipartStateContainerName;
        var (bodyBytes, contentType) = BuildPresignedPostRequest(internalBucket, "upload.txt", "hello post"u8.ToArray());

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Post,
            path: "/" + internalBucket,
            contentType: contentType);
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;

        await ObjectHandlers.HandlePostObjectAsync(
            context,
            new S3RouteResult(S3Operation.PostObject, internalBucket, null, VirtualHosted: false),
            http,
            Resolver(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("NoSuchBucket", await TestHttpContext.ReadBodyAsync(context), StringComparison.Ordinal);
    }

    private static BlobClient NewBlobClient(AzureHttpClient http) =>
        new(http, new BlobCredentials
        {
            AccountName = AccountName,
            AccountKey = AccountKeyBase64,
        });

    private static ICredentialResolver Resolver() =>
        new StaticCredentialResolver(new ProxyConfig
        {
            Credentials =
            {
                new CredentialEntry
                {
                    AwsAccessKeyId = AccessKeyId,
                    AwsSecretAccessKey = SecretKey,
                    Azure = new AzureCredentials
                    {
                        Blob = new BlobCredentials
                        {
                            AccountName = AccountName,
                            AccountKey = AccountKeyBase64,
                        }
                    }
                }
            }
        });

    private static HttpResponseMessage AzureResponse(HttpStatusCode statusCode, string? eTag = null, DateTimeOffset? lastModified = null)
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

    private static HttpResponseMessage GetResponse(byte[] body, string? contentType = null)
    {
        var response = AzureResponse(HttpStatusCode.OK, eTag: "\"0xGET\"");
        response.Content = new ByteArrayContent(body);
        response.Content.Headers.ContentLength = body.Length;
        if (contentType is not null)
        {
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }
        return response;
    }

    private static HttpResponseMessage HeadResponse(string etag, string? contentMd5Base64 = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        if (!string.IsNullOrEmpty(contentMd5Base64))
        {
            response.Content.Headers.TryAddWithoutValidation("Content-MD5", contentMd5Base64);
        }
        return response;
    }

    private static (byte[] Body, string ContentType) BuildPresignedPostRequest(
        string bucket,
        string key,
        byte[] fileBytes,
        IReadOnlyDictionary<string, string>? additionalFields = null,
        IReadOnlySet<string>? unsignedFields = null,
        bool tamperSignature = false)
    {
        const string boundary = "----aws2azure-unit-boundary";
        const string date = "20260728";
        const string amzDate = "20260728T200000Z";
        const string region = "us-east-1";
        var credential = $"{AccessKeyId}/{date}/{region}/s3/aws4_request";

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["key"] = key,
            ["x-amz-algorithm"] = SigV4Constants.Algorithm,
            ["x-amz-credential"] = credential,
            ["x-amz-date"] = amzDate,
        };
        if (additionalFields is not null)
        {
            foreach (var entry in additionalFields)
            {
                fields[entry.Key] = entry.Value;
            }
        }

        var expiration = "2099-01-01T00:00:00Z";
        var conditions = new StringBuilder();
        conditions.Append("{\"bucket\":\"").Append(bucket).Append("\"},");
        foreach (var field in fields)
        {
            if (unsignedFields?.Contains(field.Key) == true)
            {
                continue;
            }

            conditions.Append("{\"").Append(field.Key).Append("\":\"").Append(field.Value).Append("\"},");
        }
        if (conditions[^1] == ',')
        {
            conditions.Length--;
        }

        var policyJson = "{\"expiration\":\"" + expiration + "\",\"conditions\":[" + conditions + "]}";
        var policyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(policyJson));
        var signingKey = SigningKey.Derive(SecretKey, date, region, "s3");
        var signature = SigningKey.ToLowerHex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(policyBase64)));
        if (tamperSignature)
        {
            signature = signature[..63] + (signature[63] == 'a' ? 'b' : 'a');
        }

        fields["policy"] = policyBase64;
        fields["x-amz-signature"] = signature;

        using var ms = new MemoryStream();
        foreach (var field in fields)
        {
            WriteFormField(ms, boundary, field.Key, field.Value);
        }
        WriteFileField(ms, boundary, "file", "upload.txt", fileBytes);
        var trailer = Encoding.UTF8.GetBytes($"--{boundary}--\r\n");
        ms.Write(trailer, 0, trailer.Length);
        return (ms.ToArray(), $"multipart/form-data; boundary={boundary}");
    }

    private static void WriteFormField(Stream stream, string boundary, string name, string value)
    {
        var payload = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        stream.Write(payload, 0, payload.Length);
    }

    private static void WriteFileField(Stream stream, string boundary, string name, string fileName, byte[] body)
    {
        var header = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\nContent-Type: text/plain\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
        var newline = Encoding.UTF8.GetBytes("\r\n");
        stream.Write(newline, 0, newline.Length);
    }
}
