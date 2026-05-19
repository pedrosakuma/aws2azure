using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 slice-8 coverage: <c>UploadPartCopy</c>.
///
/// Validation/short-circuit paths (bad uploadId, self-copy, malformed range)
/// are exercised end-to-end against Azurite. The Azure-side happy-path
/// scenarios that depend on the <c>Put Block From URL</c> REST operation
/// are <see cref="Skip"/>-ped: Azurite (all 3.x) does not implement that
/// operation and answers 501 InternalError. The implementation is verified
/// against real Azure Blob Storage in the nightly cloud-integration suite.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3MultipartCopyTests
{
    /// <summary>
    /// Standard skip reason for tests that depend on Azure's
    /// <c>Put Block From URL</c> (Azurite limitation). Kept as a constant so
    /// it's trivial to flip when Azurite ships support — see
    /// https://github.com/Azure/Azurite/issues/1020.
    /// </summary>
    private const string AzuritePutBlockFromUrlUnsupported =
        "Azurite does not implement Put Block From URL (responds 501 InternalError); covered by the real-Azure nightly suite instead.";

    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly S3IntegrationFixture _fx;
    public S3MultipartCopyTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task UploadPartCopy_full_source_appears_in_destination_after_complete()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");
        Skip.If(true, AzuritePutBlockFromUrlUnsupported);

        var srcBucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var dstBucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(srcBucket);
        await PutBucket(dstBucket);

        var srcKey = "src/object.bin";
        var srcBody = Encoding.UTF8.GetBytes("hello-from-source");
        await PutObject(srcBucket, srcKey, srcBody);

        var dstKey = "dst/composed.bin";
        var uploadId = await Initiate(dstBucket, dstKey);
        var etag = await UploadPartCopy(dstBucket, dstKey, uploadId, partNumber: 1,
            sourceBucket: srcBucket, sourceKey: srcKey, range: null);
        Assert.False(string.IsNullOrEmpty(etag));

        await Complete(dstBucket, dstKey, uploadId, new[] { (1, etag) });

        using var got = await SendAsync(HttpMethod.Get, $"/{dstBucket}/{dstKey}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal(srcBody, await got.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task UploadPartCopy_with_source_range_copies_subrange()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");
        Skip.If(true, AzuritePutBlockFromUrlUnsupported);

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        var srcKey = "src/big";
        var srcBody = Encoding.UTF8.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        await PutObject(bucket, srcKey, srcBody);

        var dstKey = "dst/sliced";
        var uploadId = await Initiate(bucket, dstKey);
        // Copy "HIJ" — bytes 7..9 inclusive (3 bytes).
        var etag = await UploadPartCopy(bucket, dstKey, uploadId, 1,
            bucket, srcKey, range: "bytes=7-9");
        Assert.False(string.IsNullOrEmpty(etag));

        await Complete(bucket, dstKey, uploadId, new[] { (1, etag) });

        using var got = await SendAsync(HttpMethod.Get, $"/{bucket}/{dstKey}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("HIJ"u8.ToArray(), await got.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task UploadPartCopy_with_missing_source_returns_NoSuchKey()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");
        Skip.If(true, AzuritePutBlockFromUrlUnsupported);

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        var dstKey = "dst/missing-src";
        var uploadId = await Initiate(bucket, dstKey);

        using var resp = await SendUploadPartCopy(bucket, dstKey, uploadId, 1,
            bucket, "does/not/exist", range: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("<Code>NoSuchKey</Code>", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task UploadPartCopy_self_copy_is_rejected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        var key = "self/copy";
        await PutObject(bucket, key, "hi"u8.ToArray());
        var uploadId = await Initiate(bucket, key);

        using var resp = await SendUploadPartCopy(bucket, key, uploadId, 1,
            bucket, key, range: null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("<Code>InvalidRequest</Code>", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task UploadPartCopy_with_bad_uploadId_returns_NoSuchUpload()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "src/k", "src"u8.ToArray());

        using var resp = await SendUploadPartCopy(bucket, "dst/k",
            uploadId: new string('A', 43), partNumber: 1,
            sourceBucket: bucket, sourceKey: "src/k", range: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("<Code>NoSuchUpload</Code>", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task UploadPartCopy_with_malformed_range_returns_InvalidArgument()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "src/k", "abcdefghij"u8.ToArray());
        var dstKey = "dst/k";
        var uploadId = await Initiate(bucket, dstKey);

        using var resp = await SendUploadPartCopy(bucket, dstKey, uploadId, 1,
            bucket, "src/k", range: "lines=1-2");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("<Code>InvalidArgument</Code>", await resp.Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private async Task<string> Initiate(string bucket, string key)
    {
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return XDocument.Parse(await resp.Content.ReadAsStringAsync())
            .Root!.Element(S3Ns + "UploadId")!.Value;
    }

    private async Task<string> UploadPartCopy(string bucket, string key, string uploadId, int partNumber,
        string sourceBucket, string sourceKey, string? range)
    {
        using var resp = await SendUploadPartCopy(bucket, key, uploadId, partNumber, sourceBucket, sourceKey, range);
        Assert.True(resp.IsSuccessStatusCode, $"UploadPartCopy → {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.Root!.Element(S3Ns + "ETag")!.Value;
    }

    private Task<HttpResponseMessage> SendUploadPartCopy(
        string bucket, string key, string uploadId, int partNumber,
        string sourceBucket, string sourceKey, string? range)
    {
        var headers = new List<(string, string)>
        {
            ("x-amz-copy-source", "/" + sourceBucket + "/" + sourceKey),
        };
        if (range is not null)
        {
            headers.Add(("x-amz-copy-source-range", range));
        }
        return SendAsync(HttpMethod.Put,
            $"/{bucket}/{key}?uploadId={uploadId}&partNumber={partNumber}",
            Array.Empty<byte>(), extraHeaders: headers);
    }

    private async Task Complete(string bucket, string key, string uploadId, (int PartNumber, string ETag)[] parts)
    {
        var sb = new StringBuilder();
        sb.Append("<CompleteMultipartUpload>");
        foreach (var p in parts)
        {
            sb.Append("<Part><PartNumber>").Append(p.PartNumber).Append("</PartNumber><ETag>")
              .Append(p.ETag).Append("</ETag></Part>");
        }
        sb.Append("</CompleteMultipartUpload>");
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        using var resp = await SendAsync(HttpMethod.Post,
            $"/{bucket}/{key}?uploadId={uploadId}", body, contentType: "application/xml");
        Assert.True(resp.IsSuccessStatusCode, $"Complete → {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task PutObject(string bucket, string key, byte[] body)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/{key}", body,
            contentType: "application/octet-stream");
        Assert.True(resp.IsSuccessStatusCode, $"PutObject → {(int)resp.StatusCode}");
    }

    private async Task PutBucket(string bucket)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", Array.Empty<byte>());
        Assert.True(resp.IsSuccessStatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathAndQuery, byte[] body,
        string? contentType = null, IReadOnlyList<(string Name, string Value)>? extraHeaders = null)
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
            foreach (var (n, v) in extraHeaders)
            {
                req.Headers.TryAddWithoutValidation(n, v);
            }
        }
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
