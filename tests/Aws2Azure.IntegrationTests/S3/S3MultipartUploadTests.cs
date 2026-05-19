using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 slice-6 coverage: S3 multipart upload core
/// (Create / UploadPart / Complete / Abort) mapped to Azure Block Blobs.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3MultipartUploadTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly S3IntegrationFixture _fx;
    public S3MultipartUploadTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Multipart_three_parts_completes_and_blob_round_trips()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "multipart/object.bin";

        // 1) Initiate
        using var initResp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        var initDoc = XDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var uploadId = initDoc.Root!.Element(S3Ns + "UploadId")!.Value;
        Assert.False(string.IsNullOrEmpty(uploadId));

        // 2) UploadPart × 3 — each part is 5 MiB except the last (S3-style),
        //    but we use small parts here because Azurite has no size minimum.
        var p1 = MakePart('a', 1024);
        var p2 = MakePart('b', 2048);
        var p3 = MakePart('c', 512);

        var etag1 = await UploadPart(bucket, key, uploadId, 1, p1);
        var etag2 = await UploadPart(bucket, key, uploadId, 2, p2);
        var etag3 = await UploadPart(bucket, key, uploadId, 3, p3);
        Assert.False(string.IsNullOrEmpty(etag1));
        Assert.False(string.IsNullOrEmpty(etag2));
        Assert.False(string.IsNullOrEmpty(etag3));

        // 3) Complete
        var completeBody = BuildCompleteXml(new[]
        {
            (1, etag1!), (2, etag2!), (3, etag3!),
        });
        using var compResp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploadId={uploadId}",
            completeBody, contentType: "application/xml");
        Assert.Equal(HttpStatusCode.OK, compResp.StatusCode);
        var compDoc = XDocument.Parse(await compResp.Content.ReadAsStringAsync());
        var multipartEtag = compDoc.Root!.Element(S3Ns + "ETag")!.Value;
        Assert.Contains("-3", multipartEtag); // multipart shape "{hash}-{partCount}"

        // 4) GET — body must equal p1 || p2 || p3
        using var getResp = await SendAsync(HttpMethod.Get, $"/{bucket}/{key}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var got = await getResp.Content.ReadAsByteArrayAsync();
        var expected = p1.Concat(p2).Concat(p3).ToArray();
        Assert.Equal(expected, got);
    }

    [SkippableFact]
    public async Task UploadPart_with_unknown_uploadId_returns_NoSuchUpload()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var bogus = new string('A', 43); // base64url-shaped but unsigned
        using var resp = await SendAsync(HttpMethod.Put,
            $"/{bucket}/k?uploadId={bogus}&partNumber=1",
            "data"u8.ToArray(), contentType: "application/octet-stream");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>NoSuchUpload</Code>", xml);
    }

    [SkippableFact]
    public async Task UploadPart_with_bad_partNumber_is_rejected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "k";

        using var initResp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", Array.Empty<byte>());
        var uploadId = XDocument.Parse(await initResp.Content.ReadAsStringAsync())
            .Root!.Element(S3Ns + "UploadId")!.Value;

        foreach (var pn in new[] { "0", "10001", "abc" })
        {
            using var resp = await SendAsync(HttpMethod.Put,
                $"/{bucket}/{key}?uploadId={uploadId}&partNumber={pn}",
                "x"u8.ToArray(), contentType: "application/octet-stream");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Complete_with_out_of_order_parts_returns_MalformedXML()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "k";

        using var initResp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", Array.Empty<byte>());
        var uploadId = XDocument.Parse(await initResp.Content.ReadAsStringAsync())
            .Root!.Element(S3Ns + "UploadId")!.Value;

        var e1 = await UploadPart(bucket, key, uploadId, 1, "a"u8.ToArray());
        var e2 = await UploadPart(bucket, key, uploadId, 2, "b"u8.ToArray());

        // Send parts 2,1 — out of order.
        var body = BuildCompleteXml(new[] { (2, e2!), (1, e1!) });
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploadId={uploadId}",
            body, contentType: "application/xml");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("<Code>MalformedXML</Code>", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Complete_with_missing_partNumber_returns_InvalidPart()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "k";

        using var initResp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", Array.Empty<byte>());
        var uploadId = XDocument.Parse(await initResp.Content.ReadAsStringAsync())
            .Root!.Element(S3Ns + "UploadId")!.Value;

        var e1 = await UploadPart(bucket, key, uploadId, 1, "a"u8.ToArray());
        // Reference part 7 which was never uploaded.
        var body = BuildCompleteXml(new[] { (1, e1!), (7, "\"00000000000000000000000000000000\"") });
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploadId={uploadId}",
            body, contentType: "application/xml");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("<Code>InvalidPart</Code>", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Abort_returns_204_and_does_not_create_blob()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var key = "k";

        using var initResp = await SendAsync(HttpMethod.Post, $"/{bucket}/{key}?uploads", Array.Empty<byte>());
        var uploadId = XDocument.Parse(await initResp.Content.ReadAsStringAsync())
            .Root!.Element(S3Ns + "UploadId")!.Value;

        await UploadPart(bucket, key, uploadId, 1, "data"u8.ToArray());

        using var abortResp = await SendAsync(HttpMethod.Delete, $"/{bucket}/{key}?uploadId={uploadId}",
            Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NoContent, abortResp.StatusCode);

        using var head = await SendAsync(HttpMethod.Head, $"/{bucket}/{key}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
    }

    [SkippableFact]
    public async Task CreateMultipart_against_missing_bucket_returns_NoSuchBucket()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10]; // never created
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}/k?uploads", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("<Code>NoSuchBucket</Code>", await resp.Content.ReadAsStringAsync());
    }

    // ---- helpers ----

    private static byte[] MakePart(char fill, int size)
    {
        var b = new byte[size];
        Array.Fill(b, (byte)fill);
        return b;
    }

    private async Task<string?> UploadPart(string bucket, string key, string uploadId, int partNumber, byte[] body)
    {
        using var resp = await SendAsync(HttpMethod.Put,
            $"/{bucket}/{key}?uploadId={uploadId}&partNumber={partNumber}",
            body, contentType: "application/octet-stream");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return resp.Headers.TryGetValues("ETag", out var values) ? values.First() : null;
    }

    private static byte[] BuildCompleteXml(IEnumerable<(int part, string etag)> parts)
    {
        var sb = new StringBuilder("<CompleteMultipartUpload>");
        foreach (var (n, e) in parts)
        {
            sb.Append("<Part><PartNumber>").Append(n).Append("</PartNumber>");
            sb.Append("<ETag>").Append(System.Security.SecurityElement.Escape(e)).Append("</ETag></Part>");
        }
        sb.Append("</CompleteMultipartUpload>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private async Task PutBucket(string bucket)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", Array.Empty<byte>());
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket} → {(int)resp.StatusCode}");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        byte[] body,
        string? contentType = null)
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
