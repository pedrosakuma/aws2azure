using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Amazon.S3;
using Amazon.S3.Model;
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
        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);

        var first = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "a.txt",
        }).ConfigureAwait(false);
        var second = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "é.txt",
        }).ConfigureAwait(false);
        var third = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "😀.txt",
        }).ConfigureAwait(false);

        await _fx.RestartProxyAsync().ConfigureAwait(false);

        var page1 = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = bucket,
            MaxUploads = 2,
        }).ConfigureAwait(false);

        Assert.True(page1.IsTruncated);
        Assert.Equal(new[] { "a.txt", "é.txt" }, page1.MultipartUploads.Select(u => u.Key).ToArray());
        Assert.Equal("é.txt", page1.NextKeyMarker);
        Assert.Equal(second.UploadId, page1.NextUploadIdMarker);

        var page2 = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest
        {
            BucketName = bucket,
            MaxUploads = 2,
            KeyMarker = page1.NextKeyMarker,
            UploadIdMarker = page1.NextUploadIdMarker,
        }).ConfigureAwait(false);

        Assert.False(page2.IsTruncated);
        Assert.Equal(new[] { "😀.txt" }, page2.MultipartUploads.Select(u => u.Key).ToArray());
        Assert.Equal(third.UploadId, page2.MultipartUploads.Single().UploadId);
        Assert.DoesNotContain(page2.MultipartUploads, u => u.UploadId == first.UploadId && u.Key == first.Key);
    }

    [SkippableFact]
    public async Task ListMultipartUploads_applies_prefix_and_delimiter()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);
        await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "logs/2026/a.txt" }).ConfigureAwait(false);
        await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "logs/2027/b.txt" }).ConfigureAwait(false);
        await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "other/c.txt" }).ConfigureAwait(false);

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

    private AmazonS3Client CreateClient() => new(
        _fx.AccessKeyId,
        _fx.Secret,
        new AmazonS3Config
        {
            ServiceURL = _fx.Client.BaseAddress!.ToString(),
            ForcePathStyle = true,
            UseHttp = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
        });

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
