using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

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

        using var p1 = await SendAsync(HttpMethod.Get,
            $"/{bucket}/{key}?uploadId={uploadId}&max-parts=2", Array.Empty<byte>());
        var d1 = XDocument.Parse(await p1.Content.ReadAsStringAsync());
        Assert.Equal("true", d1.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal("2", d1.Root!.Element(S3Ns + "NextPartNumberMarker")!.Value);
        Assert.Equal(new[] { 1, 2 }, d1.Root!.Elements(S3Ns + "Part")
            .Select(p => int.Parse(p.Element(S3Ns + "PartNumber")!.Value)).ToArray());

        using var p2 = await SendAsync(HttpMethod.Get,
            $"/{bucket}/{key}?uploadId={uploadId}&part-number-marker=2", Array.Empty<byte>());
        var d2 = XDocument.Parse(await p2.Content.ReadAsStringAsync());
        Assert.Equal("false", d2.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal(new[] { 3, 4, 5 }, d2.Root!.Elements(S3Ns + "Part")
            .Select(p => int.Parse(p.Element(S3Ns + "PartNumber")!.Value)).ToArray());
    }

    [SkippableFact]
    public async Task ListMultipartUploads_orders_paginates_and_survives_proxy_restart()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        var firstUploadId = await Initiate(bucket, "a.txt");
        var secondUploadId = await Initiate(bucket, "é.txt");
        var thirdUploadId = await Initiate(bucket, "😀.txt");

        await _fx.RestartProxyAsync().ConfigureAwait(false);

        using var p1 = await SendAsync(HttpMethod.Get, $"/{bucket}?uploads&max-uploads=2", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, p1.StatusCode);
        var d1 = XDocument.Parse(await p1.Content.ReadAsStringAsync());
        var page1 = ParseUploads(d1);

        Assert.Equal("true", d1.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal(new[] { "a.txt", "é.txt" }, page1.Select(u => u.Key).ToArray());
        Assert.Equal("é.txt", d1.Root!.Element(S3Ns + "NextKeyMarker")!.Value);
        Assert.Equal(secondUploadId, d1.Root!.Element(S3Ns + "NextUploadIdMarker")!.Value);

        var nextKeyMarker = Uri.EscapeDataString(d1.Root!.Element(S3Ns + "NextKeyMarker")!.Value);
        var nextUploadIdMarker = Uri.EscapeDataString(d1.Root!.Element(S3Ns + "NextUploadIdMarker")!.Value);
        using var p2 = await SendAsync(HttpMethod.Get,
            $"/{bucket}?uploads&max-uploads=2&key-marker={nextKeyMarker}&upload-id-marker={nextUploadIdMarker}",
            Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, p2.StatusCode);
        var d2 = XDocument.Parse(await p2.Content.ReadAsStringAsync());
        var page2 = ParseUploads(d2);

        Assert.Equal("false", d2.Root!.Element(S3Ns + "IsTruncated")!.Value);
        Assert.Equal(new[] { "😀.txt" }, page2.Select(u => u.Key).ToArray());
        Assert.Equal(thirdUploadId, page2.Single().UploadId);
        Assert.DoesNotContain(page2, u => u.UploadId == firstUploadId && u.Key == "a.txt");
    }

    [SkippableFact]
    public async Task ListMultipartUploads_applies_prefix_and_delimiter()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await Initiate(bucket, "logs/2026/a.txt");
        await Initiate(bucket, "logs/2027/b.txt");
        await Initiate(bucket, "other/c.txt");

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?uploads&prefix=logs/&delimiter=/", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("logs/", doc.Root!.Element(S3Ns + "Prefix")!.Value);
        Assert.Equal("/", doc.Root!.Element(S3Ns + "Delimiter")!.Value);
        Assert.Equal(new[] { "logs/2026/", "logs/2027/" }, doc.Root!.Elements(S3Ns + "CommonPrefixes").Select(p => p.Element(S3Ns + "Prefix")!.Value).ToArray());
        Assert.Empty(doc.Root!.Elements(S3Ns + "Upload"));
    }

    [SkippableFact]
    public async Task ListMultipartUploads_rejects_max_uploads_zero()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?uploads&max-uploads=0", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("<Code>InvalidArgument</Code>", await resp.Content.ReadAsStringAsync());
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

        using (var del = await SendAsync(HttpMethod.Delete, $"/{bucket}", Array.Empty<byte>()))
            Assert.True(del.IsSuccessStatusCode, $"DeleteBucket failed: {del.StatusCode}");

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}?uploadId={uploadId}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("<Code>NoSuchBucket</Code>", await resp.Content.ReadAsStringAsync());
    }

    private async Task<string> Initiate(string bucket, string key)
    {
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}/{EncodeKeyPath(key)}?uploads", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return XDocument.Parse(await resp.Content.ReadAsStringAsync())
            .Root!.Element(S3Ns + "UploadId")!.Value;
    }

    /// <summary>
    /// Percent-encodes each path segment of an S3 key individually so keys
    /// containing non-ASCII characters (e.g. accented letters, emoji) survive
    /// <see cref="Uri"/> construction, while preserving '/' as a literal path
    /// separator for nested keys.
    /// </summary>
    private static string EncodeKeyPath(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private static List<(string Key, string UploadId)> ParseUploads(XDocument doc)
        => doc.Root!.Elements(S3Ns + "Upload")
            .Select(u => (
                Key: u.Element(S3Ns + "Key")!.Value,
                UploadId: u.Element(S3Ns + "UploadId")!.Value))
            .ToList();

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
