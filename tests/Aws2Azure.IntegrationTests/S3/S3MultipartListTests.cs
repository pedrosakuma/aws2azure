using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 slice-7 coverage: multipart upload listing
/// (<c>ListParts</c>, <c>ListMultipartUploads</c>).
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3MultipartListTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly S3IntegrationFixture _fx;
    public S3MultipartListTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task ListParts_returns_uploaded_parts()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "lp/object.bin";

        var uploadId = await Initiate(bucket, key);
        await UploadPart(bucket, key, uploadId, 1, "aaa"u8.ToArray());
        await UploadPart(bucket, key, uploadId, 2, "bbbbb"u8.ToArray());
        await UploadPart(bucket, key, uploadId, 7, "ccccccc"u8.ToArray());

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}?uploadId={uploadId}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var parts = doc.Root!.Elements(S3Ns + "Part").ToList();
        Assert.Equal(3, parts.Count);
        Assert.Equal(new[] { 1, 2, 7 }, parts.Select(p => int.Parse(p.Element(S3Ns + "PartNumber")!.Value)).ToArray());
        Assert.Equal(new[] { 3L, 5L, 7L }, parts.Select(p => long.Parse(p.Element(S3Ns + "Size")!.Value)).ToArray());
        // Each part has a non-empty ETag (synthetic, see gap doc).
        foreach (var p in parts)
        {
            Assert.False(string.IsNullOrEmpty(p.Element(S3Ns + "ETag")!.Value));
        }
    }

    [SkippableFact]
    public async Task ListParts_respects_max_parts_and_marker()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "k";
        var uploadId = await Initiate(bucket, key);
        for (int i = 1; i <= 5; i++)
        {
            await UploadPart(bucket, key, uploadId, i, new byte[] { (byte)i });
        }

        // Page 1: max-parts=2 → parts 1,2 ; NextPartNumberMarker=2 ; IsTruncated=true
        using var p1 = await SendAsync(HttpMethod.Get,
            $"/{bucket}/{key}?uploadId={uploadId}&max-parts=2", Array.Empty<byte>());
        var d1 = XDocument.Parse(await p1.Content.ReadAsStringAsync());
        Assert.Equal("true", d1.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal("2", d1.Root!.Element(S3Ns + "NextPartNumberMarker")!.Value);
        Assert.Equal(new[] { 1, 2 }, d1.Root!.Elements(S3Ns + "Part")
            .Select(p => int.Parse(p.Element(S3Ns + "PartNumber")!.Value)).ToArray());

        // Page 2: part-number-marker=2 → parts 3,4,5 ; IsTruncated=false
        using var p2 = await SendAsync(HttpMethod.Get,
            $"/{bucket}/{key}?uploadId={uploadId}&part-number-marker=2", Array.Empty<byte>());
        var d2 = XDocument.Parse(await p2.Content.ReadAsStringAsync());
        Assert.Equal("false", d2.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal(new[] { 3, 4, 5 }, d2.Root!.Elements(S3Ns + "Part")
            .Select(p => int.Parse(p.Element(S3Ns + "PartNumber")!.Value)).ToArray());
    }

    [SkippableFact]
    public async Task ListParts_with_bad_uploadId_returns_NoSuchUpload()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        using var resp = await SendAsync(HttpMethod.Get,
            $"/{bucket}/k?uploadId={new string('A', 43)}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("<Code>NoSuchUpload</Code>", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task ListParts_filters_out_blocks_from_other_uploads()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "shared/object";

        var u1 = await Initiate(bucket, key);
        var u2 = await Initiate(bucket, key);
        await UploadPart(bucket, key, u1, 1, "u1-part"u8.ToArray());
        await UploadPart(bucket, key, u2, 1, "u2-part"u8.ToArray());

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}?uploadId={u1}", Array.Empty<byte>());
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var parts = doc.Root!.Elements(S3Ns + "Part").ToList();
        // Only u1's part should appear, not u2's.
        Assert.Single(parts);
        Assert.Equal(7L, long.Parse(parts[0].Element(S3Ns + "Size")!.Value));
    }

    [SkippableFact]
    public async Task ListMultipartUploads_returns_empty_envelope()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await Initiate(bucket, "k"); // exists but enumeration is unsupported

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?uploads", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("false", doc.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Empty(doc.Root!.Elements(S3Ns + "Upload"));
    }

    [SkippableFact]
    public async Task ListMultipartUploads_against_missing_bucket_returns_NoSuchBucket()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?uploads", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("<Code>NoSuchBucket</Code>", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task ListParts_against_missing_bucket_returns_NoSuchBucket()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "lp/missing-bucket";
        var uploadId = await Initiate(bucket, key);

        // Tear the bucket down; uploadId is HMAC-bound to (bucket,key) so it
        // decodes fine, but Azure now returns 404 ContainerNotFound and that
        // must surface as S3 NoSuchBucket — not a misleading empty parts list.
        using (var del = await SendAsync(HttpMethod.Delete, $"/{bucket}", Array.Empty<byte>()))
            Assert.True(del.IsSuccessStatusCode, $"DeleteBucket failed: {del.StatusCode}");

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}?uploadId={uploadId}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("<Code>NoSuchBucket</Code>", await resp.Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private async Task<string> Initiate(string bucket, string key)
    {
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return XDocument.Parse(await resp.Content.ReadAsStringAsync())
            .Root!.Element(S3Ns + "UploadId")!.Value;
    }

    private async Task UploadPart(string bucket, string key, string uploadId, int partNumber, byte[] body)
    {
        using var resp = await SendAsync(HttpMethod.Put,
            $"/{bucket}/{key}?uploadId={uploadId}&partNumber={partNumber}",
            body, contentType: "application/octet-stream");
        Assert.True(resp.IsSuccessStatusCode, $"UploadPart → {(int)resp.StatusCode}");
    }

    private async Task PutBucket(string bucket)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", Array.Empty<byte>());
        Assert.True(resp.IsSuccessStatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathAndQuery, byte[] body, string? contentType = null)
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
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
