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

public sealed class SubresourceHandlersTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public async Task HandleAsync_bucket_tagging_roundtrips_through_container_metadata_and_preserves_unrelated_entries()
    {
        var backend = new FakeBlobBackend();
        backend.AddContainer("bucket", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["existing"] = "keep-me"
        });

        var putContext = TestHttpContext.CreateContext(
            body: BucketTaggingXml(("env", "prod"), ("owner", "team-a")),
            method: HttpMethods.Put,
            path: "/bucket",
            queryString: "?tagging");

        await SubresourceHandlers.HandleAsync(
            putContext,
            Route(S3Operation.PutBucketTagging, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, putContext.Response.StatusCode);
        Assert.Equal("keep-me", backend.ContainerMetadata["bucket"]["existing"]);
        Assert.True(backend.ContainerMetadata["bucket"].TryGetValue("aws2azurebuckettags", out var encoded));
        AssertTaggingXml(
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded)),
            ("env", "prod"),
            ("owner", "team-a"));

        var metadataPut = Assert.Single(backend.Requests, IsContainerMetadataPut);
        AssertHeader(metadataPut.Headers, "x-ms-meta-existing", "keep-me");
        Assert.True(metadataPut.Headers.ContainsKey("x-ms-meta-aws2azurebuckettags"));

        var getContext = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?tagging");

        await SubresourceHandlers.HandleAsync(
            getContext,
            Route(S3Operation.GetBucketTagging, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, getContext.Response.StatusCode);
        AssertTaggingXml(TestHttpContext.ReadBody(getContext), ("env", "prod"), ("owner", "team-a"));

        var deleteContext = TestHttpContext.CreateContext(
            method: HttpMethods.Delete,
            path: "/bucket",
            queryString: "?tagging");

        await SubresourceHandlers.HandleAsync(
            deleteContext,
            Route(S3Operation.DeleteBucketTagging, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, deleteContext.Response.StatusCode);
        Assert.Equal("keep-me", backend.ContainerMetadata["bucket"]["existing"]);
        Assert.False(backend.ContainerMetadata["bucket"].ContainsKey("aws2azurebuckettags"));
    }

    [Fact]
    public async Task HandleAsync_object_tagging_put_get_delete_roundtrips_through_blob_tags()
    {
        var backend = new FakeBlobBackend();
        backend.AddBlob("bucket", "key.txt");

        var putContext = TestHttpContext.CreateContext(
            body: BucketTaggingXml(("project", "aws2azure"), ("tier", "tests")),
            method: HttpMethods.Put,
            path: "/bucket/key.txt",
            queryString: "?tagging");

        await SubresourceHandlers.HandleAsync(
            putContext,
            Route(S3Operation.PutObjectTagging, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, putContext.Response.StatusCode);
        Assert.Contains("<Tags>", backend.Blobs[("bucket", "key.txt")].TagsXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Tagging", backend.Blobs[("bucket", "key.txt")].TagsXml, StringComparison.Ordinal);

        var getContext = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket/key.txt",
            queryString: "?tagging");

        await SubresourceHandlers.HandleAsync(
            getContext,
            Route(S3Operation.GetObjectTagging, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, getContext.Response.StatusCode);
        AssertTaggingXml(TestHttpContext.ReadBody(getContext), ("project", "aws2azure"), ("tier", "tests"));

        var deleteContext = TestHttpContext.CreateContext(
            method: HttpMethods.Delete,
            path: "/bucket/key.txt",
            queryString: "?tagging");

        await SubresourceHandlers.HandleAsync(
            deleteContext,
            Route(S3Operation.DeleteObjectTagging, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status204NoContent, deleteContext.Response.StatusCode);
        Assert.Empty(ParseAzureTags(backend.Blobs[("bucket", "key.txt")].TagsXml));

        var getAfterDeleteContext = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket/key.txt",
            queryString: "?tagging");

        await SubresourceHandlers.HandleAsync(
            getAfterDeleteContext,
            Route(S3Operation.GetObjectTagging, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, getAfterDeleteContext.Response.StatusCode);
        AssertTaggingXml(TestHttpContext.ReadBody(getAfterDeleteContext));
    }

    [Fact]
    public async Task HandleAsync_bucket_versioning_transitions_between_enabled_and_suspended_and_preserves_metadata()
    {
        var backend = new FakeBlobBackend();
        backend.AddContainer("bucket", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["existing"] = "keep-me",
            ["aws2azurebuckettags"] = "persist-me"
        });

        var enableContext = TestHttpContext.CreateContext(
            body: VersioningXml("Enabled"),
            method: HttpMethods.Put,
            path: "/bucket",
            queryString: "?versioning");

        await SubresourceHandlers.HandleAsync(
            enableContext,
            Route(S3Operation.PutBucketVersioning, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, enableContext.Response.StatusCode);
        Assert.Equal("Enabled", backend.ContainerMetadata["bucket"]["aws2azureversioning"]);
        Assert.Equal("keep-me", backend.ContainerMetadata["bucket"]["existing"]);
        Assert.Equal("persist-me", backend.ContainerMetadata["bucket"]["aws2azurebuckettags"]);

        var enabledGetContext = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?versioning");

        await SubresourceHandlers.HandleAsync(
            enabledGetContext,
            Route(S3Operation.GetBucketVersioning, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, enabledGetContext.Response.StatusCode);
        Assert.Equal("Enabled", ParseS3Element(TestHttpContext.ReadBody(enabledGetContext), "Status"));

        var suspendContext = TestHttpContext.CreateContext(
            body: VersioningXml("Suspended"),
            method: HttpMethods.Put,
            path: "/bucket",
            queryString: "?versioning");

        await SubresourceHandlers.HandleAsync(
            suspendContext,
            Route(S3Operation.PutBucketVersioning, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, suspendContext.Response.StatusCode);
        Assert.Equal("Suspended", backend.ContainerMetadata["bucket"]["aws2azureversioning"]);

        var suspendedGetContext = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?versioning");

        await SubresourceHandlers.HandleAsync(
            suspendedGetContext,
            Route(S3Operation.GetBucketVersioning, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, suspendedGetContext.Response.StatusCode);
        Assert.Equal("Suspended", ParseS3Element(TestHttpContext.ReadBody(suspendedGetContext), "Status"));

        var metadataPuts = backend.Requests.Where(IsContainerMetadataPut).ToList();
        Assert.Equal(2, metadataPuts.Count);
        Assert.All(metadataPuts, request => AssertHeader(request.Headers, "x-ms-meta-existing", "keep-me"));
    }

    [Fact]
    public async Task HandleAsync_object_retention_put_get_roundtrips_and_forwards_version_id()
    {
        var backend = new FakeBlobBackend();
        backend.AddBlob("bucket", "key.txt");

        var putContext = TestHttpContext.CreateContext(
            body: RetentionXml("COMPLIANCE", "2030-01-02T03:04:05Z"),
            method: HttpMethods.Put,
            path: "/bucket/key.txt",
            queryString: "?retention&versionId=ver-1");

        await SubresourceHandlers.HandleAsync(
            putContext,
            Route(S3Operation.PutObjectRetention, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, putContext.Response.StatusCode);
        Assert.Equal("Locked", backend.Blobs[("bucket", "key.txt")].ImmutabilityMode);
        Assert.Equal("Wed, 02 Jan 2030 03:04:05 GMT", backend.Blobs[("bucket", "key.txt")].ImmutabilityUntilRfc1123);

        var putRequest = backend.Requests.Last(request =>
            request.Method == HttpMethod.Put &&
            request.RequestUri!.Query.Contains("comp=immutabilityPolicies", StringComparison.Ordinal));
        Assert.Contains("versionid=ver-1", putRequest.RequestUri!.Query, StringComparison.Ordinal);
        AssertHeader(putRequest.Headers, "x-ms-immutability-policy-mode", "Locked");
        AssertHeader(putRequest.Headers, "x-ms-immutability-policy-until-date", "Wed, 02 Jan 2030 03:04:05 GMT");

        var getContext = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket/key.txt",
            queryString: "?retention&versionId=ver-1");

        await SubresourceHandlers.HandleAsync(
            getContext,
            Route(S3Operation.GetObjectRetention, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, getContext.Response.StatusCode);
        Assert.Equal("COMPLIANCE", ParseS3Element(TestHttpContext.ReadBody(getContext), "Mode"));
        Assert.Equal("2030-01-02T03:04:05.000Z", ParseS3Element(TestHttpContext.ReadBody(getContext), "RetainUntilDate"));

        var getRequest = backend.Requests.Last(request =>
            request.Method == HttpMethod.Head &&
            request.RequestUri!.Query.Contains("versionid=ver-1", StringComparison.Ordinal));
        Assert.Equal("?versionid=ver-1", getRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task HandleAsync_object_legal_hold_put_get_roundtrips_and_forwards_version_id()
    {
        var backend = new FakeBlobBackend();
        backend.AddBlob("bucket", "key.txt");

        var putContext = TestHttpContext.CreateContext(
            body: LegalHoldXml("ON"),
            method: HttpMethods.Put,
            path: "/bucket/key.txt",
            queryString: "?legal-hold&versionId=ver-2");

        await SubresourceHandlers.HandleAsync(
            putContext,
            Route(S3Operation.PutObjectLegalHold, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, putContext.Response.StatusCode);
        Assert.True(backend.Blobs[("bucket", "key.txt")].LegalHold);

        var putRequest = backend.Requests.Last(request =>
            request.Method == HttpMethod.Put &&
            request.RequestUri!.Query.Contains("comp=legalhold", StringComparison.Ordinal));
        Assert.Contains("versionid=ver-2", putRequest.RequestUri!.Query, StringComparison.Ordinal);
        AssertHeader(putRequest.Headers, "x-ms-legal-hold", "true");

        var getContext = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket/key.txt",
            queryString: "?legal-hold&versionId=ver-2");

        await SubresourceHandlers.HandleAsync(
            getContext,
            Route(S3Operation.GetObjectLegalHold, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, getContext.Response.StatusCode);
        Assert.Equal("ON", ParseS3Element(TestHttpContext.ReadBody(getContext), "Status"));

        var getRequest = backend.Requests.Last(request =>
            request.Method == HttpMethod.Head &&
            request.RequestUri!.Query.Contains("versionid=ver-2", StringComparison.Ordinal));
        Assert.Equal("?versionid=ver-2", getRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task HandleAsync_get_bucket_acl_returns_owner_full_control_policy()
    {
        var backend = new FakeBlobBackend(accountName: "testaccount");
        backend.AddContainer("bucket");

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Get,
            path: "/bucket",
            queryString: "?acl");

        await SubresourceHandlers.HandleAsync(
            context,
            Route(S3Operation.GetBucketAcl, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var xml = XDocument.Parse(TestHttpContext.ReadBody(context));
        Assert.Equal("AccessControlPolicy", xml.Root!.Name.LocalName);
        Assert.Equal(ComputeOwnerId("testaccount"), xml.Root!.Element(S3Ns + "Owner")!.Element(S3Ns + "ID")!.Value);
        Assert.Equal("testaccount", xml.Root!.Element(S3Ns + "Owner")!.Element(S3Ns + "DisplayName")!.Value);
        Assert.Equal("FULL_CONTROL", xml.Root!.Element(S3Ns + "AccessControlList")!.Element(S3Ns + "Grant")!.Element(S3Ns + "Permission")!.Value);
    }

    [Fact]
    public async Task HandleAsync_put_bucket_acl_accepts_private_canned_acl()
    {
        var backend = new FakeBlobBackend();
        backend.AddContainer("bucket");

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/bucket",
            queryString: "?acl",
            headers: [new KeyValuePair<string, string>("x-amz-acl", "private")]);

        await SubresourceHandlers.HandleAsync(
            context,
            Route(S3Operation.PutBucketAcl, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_put_object_acl_rejects_explicit_grant_headers()
    {
        var backend = new FakeBlobBackend();
        backend.AddBlob("bucket", "key.txt");

        var context = TestHttpContext.CreateContext(
            method: HttpMethods.Put,
            path: "/bucket/key.txt",
            queryString: "?acl",
            headers: [new KeyValuePair<string, string>("x-amz-grant-read", "uri=\"http://acs.amazonaws.com/groups/global/AllUsers\"")]);

        await SubresourceHandlers.HandleAsync(
            context,
            Route(S3Operation.PutObjectAcl, "bucket", "key.txt"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("AccessControlListNotSupported", ParseErrorCode(TestHttpContext.ReadBody(context)));
    }

    [Theory]
    [InlineData(S3Operation.GetBucketLogging, "GET", "?logging", 200, "BucketLoggingStatus", null)]
    [InlineData(S3Operation.GetObjectLockConfiguration, "GET", "?object-lock", 404, null, "ObjectLockConfigurationNotFoundError")]
    [InlineData(S3Operation.PutBucketCors, "PUT", "?cors", 501, null, "NotImplemented")]
    [InlineData(S3Operation.DeleteBucketPolicy, "DELETE", "?policy", 204, null, null)]
    public async Task HandleAsync_config_stub_operations_return_expected_shapes(
        S3Operation operation,
        string method,
        string queryString,
        int expectedStatusCode,
        string? expectedRootElement,
        string? expectedErrorCode)
    {
        var backend = new FakeBlobBackend();
        backend.AddContainer("bucket");
        var context = TestHttpContext.CreateContext(method: method, path: "/bucket", queryString: queryString);

        await SubresourceHandlers.HandleAsync(
            context,
            Route(operation, "bucket"),
            backend.Client,
            CancellationToken.None);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);

        var body = TestHttpContext.ReadBody(context);
        if (expectedRootElement is not null)
        {
            Assert.Equal(expectedRootElement, XDocument.Parse(body).Root!.Name.LocalName);
        }

        if (expectedErrorCode is not null)
        {
            Assert.Equal(expectedErrorCode, ParseErrorCode(body));
        }

        if (expectedStatusCode == StatusCodes.Status204NoContent)
        {
            Assert.Equal(string.Empty, body);
        }
    }

    private static S3RouteResult Route(S3Operation operation, string bucket, string? key = null)
        => new(operation, bucket, key, false);

    private static string BucketTaggingXml(params (string Key, string Value)[] tags)
    {
        var tagXml = string.Join(string.Empty, tags.Select(tag =>
            $"<Tag><Key>{tag.Key}</Key><Value>{tag.Value}</Value></Tag>"));
        return $"<Tagging xmlns=\"{S3Ns}\"><TagSet>{tagXml}</TagSet></Tagging>";
    }

    private static string VersioningXml(string status)
        => $"<VersioningConfiguration xmlns=\"{S3Ns}\"><Status>{status}</Status></VersioningConfiguration>";

    private static string RetentionXml(string mode, string retainUntilDate)
        => $"<Retention xmlns=\"{S3Ns}\"><Mode>{mode}</Mode><RetainUntilDate>{retainUntilDate}</RetainUntilDate></Retention>";

    private static string LegalHoldXml(string status)
        => $"<LegalHold xmlns=\"{S3Ns}\"><Status>{status}</Status></LegalHold>";

    private static void AssertTaggingXml(string xml, params (string Key, string Value)[] expectedTags)
    {
        var tags = ParseS3Tags(xml);
        Assert.Equal(expectedTags.Length, tags.Count);
        Assert.Equal(expectedTags, tags);
    }

    private static List<(string Key, string Value)> ParseS3Tags(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Root!
            .Element(S3Ns + "TagSet")!
            .Elements(S3Ns + "Tag")
            .Select(tag => (
                tag.Element(S3Ns + "Key")!.Value,
                tag.Element(S3Ns + "Value")!.Value))
            .ToList();
    }

    private static List<(string Key, string Value)> ParseAzureTags(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Root!
            .Element("TagSet")!
            .Elements("Tag")
            .Select(tag => (
                tag.Element("Key")!.Value,
                tag.Element("Value")!.Value))
            .ToList();
    }

    private static string ParseS3Element(string xml, string localName)
        => XDocument.Parse(xml).Root!.Element(S3Ns + localName)!.Value;

    private static string ParseErrorCode(string xml)
        => XDocument.Parse(xml).Root!.Element("Code")!.Value;

    private static void AssertHeader(IReadOnlyDictionary<string, string[]> headers, string key, string expectedValue)
        => Assert.Equal(expectedValue, Assert.Single(headers[key]));

    private static bool IsContainerMetadataPut(CapturedHttpRequest request)
        => request.Method == HttpMethod.Put
            && request.RequestUri is not null
            && string.Equals(request.RequestUri.AbsolutePath, "/bucket", StringComparison.Ordinal)
            && string.Equals(request.RequestUri.Query, "?restype=container&comp=metadata", StringComparison.Ordinal);

    private static string ComputeOwnerId(string accountName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accountName));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private sealed class FakeBlobBackend
    {
        private readonly ScriptedHandler _handler;

        public FakeBlobBackend(string accountName = "acct")
        {
            AccountName = accountName;
            _handler = new ScriptedHandler(RespondAsync);
            var http = new AzureHttpClient(_handler, ownsHandler: false, new AzureHttpClientOptions { MaxAttempts = 1 });
            Client = new BlobClient(http, new BlobCredentials
            {
                AccountName = accountName,
                AccountKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
                ServiceEndpoint = "https://blob.example.test/"
            });
        }

        public string AccountName { get; }

        public BlobClient Client { get; }

        public Dictionary<string, Dictionary<string, string>> ContainerMetadata { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Bucket, string Key), BlobState> Blobs { get; } = new();

        public IReadOnlyList<CapturedHttpRequest> Requests => _handler.Requests;

        public void AddContainer(string bucket, Dictionary<string, string>? metadata = null)
            => ContainerMetadata[bucket] = metadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        public void AddBlob(string bucket, string key)
        {
            if (!ContainerMetadata.ContainsKey(bucket))
            {
                AddContainer(bucket);
            }

            Blobs[(bucket, key)] = new BlobState
            {
                TagsXml = "<Tags><TagSet /></Tags>"
            };
        }

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            var slash = path.IndexOf('/');
            var bucket = slash >= 0 ? path[..slash] : path;
            var key = slash >= 0 ? path[(slash + 1)..] : null;
            var query = request.RequestUri.Query;

            if (string.Equals(query, "?restype=container", StringComparison.Ordinal))
            {
                return ContainerMetadata.TryGetValue(bucket, out var metadata)
                    ? ContainerResponse(HttpStatusCode.OK, metadata)
                    : ErrorResponse(HttpStatusCode.NotFound, "ContainerNotFound");
            }

            if (string.Equals(query, "?restype=container&comp=metadata", StringComparison.Ordinal))
            {
                if (!ContainerMetadata.TryGetValue(bucket, out var metadata))
                {
                    return ErrorResponse(HttpStatusCode.NotFound, "ContainerNotFound");
                }

                if (request.Method == HttpMethod.Get)
                {
                    return ContainerResponse(HttpStatusCode.OK, metadata);
                }

                if (request.Method == HttpMethod.Put)
                {
                    ContainerMetadata[bucket] = request.Headers
                        .Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(
                            header => header.Key["x-ms-meta-".Length..].ToLowerInvariant(),
                            header => header.Value.First(),
                            StringComparer.Ordinal);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            if (string.Equals(query, "?comp=tags", StringComparison.Ordinal) && key is not null)
            {
                if (!Blobs.TryGetValue((bucket, key), out var blob))
                {
                    return ErrorResponse(HttpStatusCode.NotFound, "BlobNotFound");
                }

                if (request.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(blob.TagsXml, Encoding.UTF8, "application/xml")
                    };
                }

                if (request.Method == HttpMethod.Put)
                {
                    blob.TagsXml = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }

            if (key is not null && request.Method == HttpMethod.Head)
            {
                return Blobs.TryGetValue((bucket, key), out var blob)
                    ? BlobHeadResponse(blob)
                    : ErrorResponse(HttpStatusCode.NotFound, "BlobNotFound");
            }

            if (key is not null
                && request.Method == HttpMethod.Put
                && query.StartsWith("?comp=immutabilityPolicies", StringComparison.Ordinal))
            {
                if (!Blobs.TryGetValue((bucket, key), out var blob))
                {
                    return ErrorResponse(HttpStatusCode.NotFound, "BlobNotFound");
                }

                blob.ImmutabilityUntilRfc1123 = request.Headers.GetValues("x-ms-immutability-policy-until-date").Single();
                blob.ImmutabilityMode = request.Headers.GetValues("x-ms-immutability-policy-mode").Single();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (key is not null
                && request.Method == HttpMethod.Put
                && query.StartsWith("?comp=legalhold", StringComparison.Ordinal))
            {
                if (!Blobs.TryGetValue((bucket, key), out var blob))
                {
                    return ErrorResponse(HttpStatusCode.NotFound, "BlobNotFound");
                }

                blob.LegalHold = string.Equals(
                    request.Headers.GetValues("x-ms-legal-hold").Single(),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            throw new InvalidOperationException($"Unhandled fake Blob request: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage ContainerResponse(HttpStatusCode statusCode, IReadOnlyDictionary<string, string> metadata)
        {
            var response = new HttpResponseMessage(statusCode);
            foreach (var (key, value) in metadata)
            {
                response.Headers.TryAddWithoutValidation("x-ms-meta-" + key, value);
            }

            return response;
        }

        private static HttpResponseMessage BlobHeadResponse(BlobState blob)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (blob.ImmutabilityUntilRfc1123 is not null)
            {
                response.Headers.TryAddWithoutValidation("x-ms-immutability-policy-until-date", blob.ImmutabilityUntilRfc1123);
            }

            if (blob.ImmutabilityMode is not null)
            {
                response.Headers.TryAddWithoutValidation("x-ms-immutability-policy-mode", blob.ImmutabilityMode);
            }

            response.Headers.TryAddWithoutValidation("x-ms-legal-hold", blob.LegalHold ? "true" : "false");
            return response;
        }

        private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string azureErrorCode)
        {
            var response = new HttpResponseMessage(statusCode);
            response.Headers.TryAddWithoutValidation("x-ms-error-code", azureErrorCode);
            return response;
        }
    }

    private sealed class BlobState
    {
        public string TagsXml { get; set; } = "<Tags><TagSet /></Tags>";

        public string? ImmutabilityUntilRfc1123 { get; set; }

        public string? ImmutabilityMode { get; set; }

        public bool LegalHold { get; set; }
    }
}
