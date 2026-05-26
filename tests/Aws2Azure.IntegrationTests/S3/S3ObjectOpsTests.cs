using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 slice-2 coverage: object basics (Put/Get/Head/Delete) including
/// Range requests, conditional GETs, and binary round-trip equivalence.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3ObjectOpsTests
{
    private readonly S3IntegrationFixture _fx;
    public S3ObjectOpsTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Object_round_trip_put_head_get_delete()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var key = "folder one/sub/файл с пробелом.txt"; // exercises encoding + unicode
        var bodyText = "hello aws2azure 👋";
        var body = Encoding.UTF8.GetBytes(bodyText);

        await PutBucket(bucket);

        // PUT object
        using (var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/{key}", body, contentType: "text/plain; charset=utf-8"))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"PUT object → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
            Assert.NotNull(resp.Headers.ETag);
        }

        string? etag;
        // HEAD object — verify Content-Length, ETag, Content-Type
        using (var resp = await SendAsync(HttpMethod.Head, $"/{bucket}/{key}", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(body.Length, resp.Content.Headers.ContentLength);
            Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
            etag = resp.Headers.ETag?.ToString();
            Assert.False(string.IsNullOrEmpty(etag));
        }

        // GET object — full body
        using (var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(body, bytes);
        }

        // GET object — Range (first 5 bytes)
        using (var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}", Array.Empty<byte>(),
                   extraHeaders: ("Range", "bytes=0-4")))
        {
            Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(body[..5], bytes);
        }

        // Conditional GET — If-None-Match with current ETag → 304
        using (var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}", Array.Empty<byte>(),
                   extraHeaders: ("If-None-Match", etag!)))
        {
            Assert.Equal(HttpStatusCode.NotModified, resp.StatusCode);
        }

        // DELETE object → 204
        using (var resp = await SendAsync(HttpMethod.Delete, $"/{bucket}/{key}", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }

        // HEAD object after delete → 404 + x-amz-error-code: NoSuchKey
        using (var resp = await SendAsync(HttpMethod.Head, $"/{bucket}/{key}", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("NoSuchKey", resp.Headers.GetValues("x-amz-error-code").Single());
        }

        // DELETE is idempotent → 204 for non-existent object
        using (var resp = await SendAsync(HttpMethod.Delete, $"/{bucket}/{key}", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Get_missing_object_returns_NoSuchKey_xml()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/missing.txt", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>NoSuchKey</Code>", xml);
    }

    [SkippableFact]
    public async Task Put_with_concrete_if_match_returns_501_not_implemented()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        // Concrete-ETag preconditions on writes would need a HEAD-then-PUT
        // cycle in the proxy to honor optimistic concurrency against Azure
        // Blob (the proxy translates ETags, so the value the client sends
        // back is no longer recognized by Azure). Until that's wired up
        // we reject loudly instead of silently dropping the precondition.
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/obj.txt",
            Encoding.UTF8.GetBytes("x"),
            extraHeaders: ("If-Match", "\"d41d8cd98f00b204e9800998ecf8427e\""));
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>NotImplemented</Code>", xml);

        // The "*" sentinel must still be honored — it maps 1:1 to Azure.
        using var ok = await SendAsync(HttpMethod.Put, $"/{bucket}/obj-new.txt",
            Encoding.UTF8.GetBytes("y"),
            extraHeaders: ("If-None-Match", "*"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [SkippableFact]
    public async Task Object_tagging_delete_clears_tags_without_dropping_blob()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "obj.txt", Encoding.UTF8.GetBytes("x"));

        // Slice 9 routes DELETE /b/k?tagging to DeleteObjectTagging (clearing
        // blob index tags via PUT comp=tags with an empty TagSet). The
        // critical invariant from the old "Unsupported" world is still that
        // the underlying blob is NOT deleted.
        using var resp = await SendAsync(HttpMethod.Delete, $"/{bucket}/obj.txt?tagging", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Verify the blob is still there.
        using var head = await SendAsync(HttpMethod.Head, $"/{bucket}/obj.txt", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    private async Task PutBucket(string bucket)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", Array.Empty<byte>());
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket} → {(int)resp.StatusCode}");
    }

    private async Task PutObject(string bucket, string key, byte[] body)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/{key}", body);
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket}/{key} → {(int)resp.StatusCode}");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        byte[] body,
        string? contentType = null,
        (string Name, string Value)? extraHeaders = null)
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
        if (extraHeaders is { } eh)
        {
            req.Headers.TryAddWithoutValidation(eh.Name, eh.Value);
        }
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
