using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 slice-4 coverage: server-side CopyObject (same Azure account).
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3CopyObjectTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly S3IntegrationFixture _fx;
    public S3CopyObjectTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task CopyObject_round_trip_preserves_bytes_and_metadata()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var srcBucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var dstBucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(srcBucket);
        await PutBucket(dstBucket);

        var body = Encoding.UTF8.GetBytes("payload-for-copy");
        await PutObject(srcBucket, "src.txt", body, contentType: "text/plain",
            extra: ("x-amz-meta-flavor", "vanilla"));

        using (var resp = await Copy(srcBucket, "src.txt", dstBucket, "dst.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var xml0 = await resp.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml0);
            Assert.Equal("CopyObjectResult", doc.Root!.Name.LocalName);
            Assert.NotNull(doc.Root!.Element(S3Ns + "ETag"));
            Assert.NotNull(doc.Root!.Element(S3Ns + "LastModified"));
            var etag = doc.Root!.Element(S3Ns + "ETag")!.Value;
            Assert.StartsWith("\"", etag);
            Assert.EndsWith("\"", etag);
        }

        // Verify dest body equals source body.
        using (var resp = await SendAsync(HttpMethod.Get, $"/{dstBucket}/dst.txt", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(body, await resp.Content.ReadAsByteArrayAsync());
        }

        // Default metadata-directive is COPY → source metadata preserved.
        using (var resp = await SendAsync(HttpMethod.Head, $"/{dstBucket}/dst.txt", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(resp.Headers.TryGetValues("x-amz-meta-flavor", out var values));
            Assert.Equal("vanilla", values!.Single());
        }
    }

    [SkippableFact]
    public async Task CopyObject_with_REPLACE_directive_overrides_metadata()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        await PutObject(bucket, "src.txt", Encoding.UTF8.GetBytes("body"), contentType: "text/plain",
            extra: ("x-amz-meta-flavor", "vanilla"));

        using (var resp = await Copy(bucket, "src.txt", bucket, "dst.txt",
                   metadataDirective: "REPLACE",
                   extraHeaders: new[]
                   {
                       ("x-amz-meta-flavor",  "chocolate"),
                       ("Content-Type",       "application/json"),
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        using (var resp = await SendAsync(HttpMethod.Head, $"/{bucket}/dst.txt", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
            Assert.Equal("chocolate", resp.Headers.GetValues("x-amz-meta-flavor").Single());
        }
    }

    [SkippableFact]
    public async Task CopyObject_missing_source_returns_NoSuchKey()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var srcBucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var dstBucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(srcBucket);
        await PutBucket(dstBucket);

        using var resp = await Copy(srcBucket, "missing.txt", dstBucket, "dst.txt");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>NoSuchKey</Code>", xml);
    }

    [SkippableFact]
    public async Task CopyObject_malformed_copy_source_returns_InvalidArgument()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await CopyRaw(copySourceRaw: "/onlybucket", dstBucket: bucket, dstKey: "dst.txt");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>InvalidArgument</Code>", xml);
    }

    [SkippableFact]
    public async Task CopyObject_versionId_qualifier_returns_InvalidArgument()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "src.txt", Encoding.UTF8.GetBytes("x"));

        using var resp = await CopyRaw(copySourceRaw: $"/{bucket}/src.txt?versionId=abc",
            dstBucket: bucket, dstKey: "dst.txt");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>InvalidArgument</Code>", xml);
        Assert.Contains("versionId", xml);
    }

    [SkippableFact]
    public async Task CopyObject_self_copy_without_REPLACE_returns_InvalidRequest()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "k.txt", Encoding.UTF8.GetBytes("xx"), contentType: "text/plain");

        using var resp = await Copy(bucket, "k.txt", bucket, "k.txt");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>InvalidRequest</Code>", xml);
    }

    [SkippableFact]
    public async Task CopyObject_self_copy_with_REPLACE_succeeds()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "k.txt", Encoding.UTF8.GetBytes("xx"), contentType: "text/plain",
            extra: ("x-amz-meta-old", "1"));

        using var resp = await Copy(bucket, "k.txt", bucket, "k.txt",
            metadataDirective: "REPLACE",
            extraHeaders: new[] { ("x-amz-meta-new", "2"), ("Content-Type", "text/markdown") });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var head = await SendAsync(HttpMethod.Head, $"/{bucket}/k.txt", Array.Empty<byte>());
        Assert.Equal("text/markdown", head.Content.Headers.ContentType?.MediaType);
        Assert.Equal("2", head.Headers.GetValues("x-amz-meta-new").Single());
        Assert.False(head.Headers.Contains("x-amz-meta-old"),
            "REPLACE directive should evict the source metadata.");
    }

    // ---- helpers ----

    private async Task PutBucket(string bucket)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", Array.Empty<byte>());
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket} → {(int)resp.StatusCode}");
    }

    private async Task PutObject(string bucket, string key, byte[] body,
        string? contentType = null, (string Name, string Value)? extra = null)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/{key}", body,
            contentType: contentType, extraHeaders: extra is { } e ? new[] { e } : null);
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket}/{key} → {(int)resp.StatusCode}");
    }

    private Task<HttpResponseMessage> Copy(string srcBucket, string srcKey, string dstBucket, string dstKey,
        string? metadataDirective = null,
        (string Name, string Value)[]? extraHeaders = null)
        => CopyRaw(copySourceRaw: "/" + srcBucket + "/" + Uri.EscapeDataString(srcKey).Replace("%2F", "/"),
                dstBucket: dstBucket, dstKey: dstKey,
                metadataDirective: metadataDirective, extraHeaders: extraHeaders);

    private async Task<HttpResponseMessage> CopyRaw(string copySourceRaw, string dstBucket, string dstKey,
        string? metadataDirective = null,
        (string Name, string Value)[]? extraHeaders = null)
    {
        var headers = new List<(string, string)>
        {
            ("x-amz-copy-source", copySourceRaw),
        };
        if (!string.IsNullOrEmpty(metadataDirective))
        {
            headers.Add(("x-amz-metadata-directive", metadataDirective));
        }
        if (extraHeaders is not null)
        {
            headers.AddRange(extraHeaders);
        }
        return await SendAsync(HttpMethod.Put, $"/{dstBucket}/{dstKey}", Array.Empty<byte>(),
            extraHeaders: headers.ToArray());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        byte[] body,
        string? contentType = null,
        (string Name, string Value)[]? extraHeaders = null)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, pathAndQuery);
        var req = new HttpRequestMessage(method, absolute);
        if (body.Length > 0 || method == HttpMethod.Put || method == HttpMethod.Post)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentLength = body.Length;
            if (contentType is not null)
            {
                req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
        }
        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase) && req.Content is not null)
                {
                    req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                }
                else
                {
                    req.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
