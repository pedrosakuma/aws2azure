using System.Net;
using System.Text;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-7 issue #170: presigned URL support. The proxy validates the AWS
/// SigV4 presigned signature against its configured AWS credentials and
/// executes the operation against Azure Blob using the per-tenant Azure
/// credentials — no Azure SAS is generated or returned to the client.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3PresignedUrlTests
{
    private readonly S3IntegrationFixture _fx;
    public S3PresignedUrlTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Presigned_get_put_head_delete_round_trip()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        var key = "presigned/object.txt";
        var body = Encoding.UTF8.GetBytes("presigned hello aws2azure");

        await CreateBucket(bucket);

        // Presigned PUT → upload object
        using (var resp = await SendPresignedAsync(HttpMethod.Put, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), body, contentType: "text/plain"))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"presigned PUT → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }

        // Presigned HEAD → metadata
        using (var resp = await SendPresignedAsync(HttpMethod.Head, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(body.Length, resp.Content.Headers.ContentLength);
        }

        // Presigned GET → full body
        using (var resp = await SendPresignedAsync(HttpMethod.Get, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(body, bytes);
        }

        // Presigned DELETE → 204
        using (var resp = await SendPresignedAsync(HttpMethod.Delete, $"/{bucket}/{key}",
                   TimeSpan.FromMinutes(5), Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
    }

    [SkippableFact]
    public async Task Presigned_url_past_expiry_is_rejected_with_access_denied()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await CreateBucket(bucket);

        // Sign with a Date in the past and a 1-second expiry → already expired.
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-30);
        var uri = TestPresignedUrlBuilder.BuildPresignedUri(
            HttpMethod.Get, _fx.Client.BaseAddress!, $"/{bucket}/missing.txt",
            expiresIn: TimeSpan.FromSeconds(1),
            accessKey: _fx.AccessKeyId, secret: _fx.Secret, now: pastDate);

        using var resp = await _fx.Client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Presigned_url_with_tampered_query_param_is_rejected_with_signature_mismatch()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await CreateBucket(bucket);

        var uri = TestPresignedUrlBuilder.BuildPresignedUri(
            HttpMethod.Get, _fx.Client.BaseAddress!, $"/{bucket}/missing.txt",
            expiresIn: TimeSpan.FromMinutes(5),
            accessKey: _fx.AccessKeyId, secret: _fx.Secret);

        // Flip a single character in X-Amz-Signature so the validator
        // rejects with SignatureDoesNotMatch.
        var tampered = uri.ToString();
        var sigIdx = tampered.IndexOf("X-Amz-Signature=", StringComparison.Ordinal);
        Assert.True(sigIdx > 0, "expected X-Amz-Signature in presigned URL");
        var charIdx = sigIdx + "X-Amz-Signature=".Length;
        var swapped = tampered[charIdx] == 'a' ? 'b' : 'a';
        tampered = tampered[..charIdx] + swapped + tampered[(charIdx + 1)..];

        using var resp = await _fx.Client.GetAsync(new Uri(tampered));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task CreateBucket(string bucket)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, $"/{bucket}");
        using var req = new HttpRequestMessage(HttpMethod.Put, absolute);
        TestSigV4Signer.SignHeader(req, Array.Empty<byte>(), _fx.AccessKeyId, _fx.Secret);
        using var resp = await _fx.Client.SendAsync(req);
        Assert.True(resp.IsSuccessStatusCode,
            $"create bucket → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<HttpResponseMessage> SendPresignedAsync(
        HttpMethod method,
        string pathAndQuery,
        TimeSpan expiresIn,
        byte[] body,
        string? contentType = null)
    {
        var uri = TestPresignedUrlBuilder.BuildPresignedUri(
            method, _fx.Client.BaseAddress!, pathAndQuery, expiresIn,
            _fx.AccessKeyId, _fx.Secret);

        var req = new HttpRequestMessage(method, uri);
        if (body.Length > 0 || method == HttpMethod.Put || method == HttpMethod.Post)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentLength = body.Length;
            if (contentType is not null)
            {
                req.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            }
        }
        return await _fx.Client.SendAsync(req);
    }
}
