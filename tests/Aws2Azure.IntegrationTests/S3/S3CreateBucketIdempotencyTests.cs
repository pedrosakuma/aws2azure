using System.Net;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Region-sensitive CreateBucket idempotency (issue #236). Real S3 makes
/// CreateBucket idempotent for the bucket owner in <c>us-east-1</c> (re-creating
/// a bucket you already own returns 200 OK), while every other region returns
/// 409 <c>BucketAlreadyOwnedByYou</c>. The proxy reproduces this from the signed
/// credential-scope region.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3CreateBucketIdempotencyTests
{
    private readonly S3IntegrationFixture _fx;
    public S3CreateBucketIdempotencyTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Recreate_owned_bucket_is_idempotent_in_us_east_1()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];

        using (var resp = await PutBucketAsync(bucket, region: "us-east-1"))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"first PUT /{bucket} → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }

        // Re-create the same bucket signed for us-east-1: real S3 answers 200 OK.
        using (var resp = await PutBucketAsync(bucket, region: "us-east-1"))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("/" + bucket, resp.Headers.Location?.ToString());
            Assert.Equal(0, resp.Content.Headers.ContentLength ?? 0);
        }

        await DeleteBucketAsync(bucket);
    }

    [SkippableFact]
    public async Task Recreate_owned_bucket_returns_409_outside_us_east_1()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];

        using (var resp = await PutBucketAsync(bucket, region: "eu-west-1"))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"first PUT /{bucket} → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }

        // Re-create signed for a non-default region: real S3 answers 409
        // BucketAlreadyOwnedByYou.
        using (var resp = await PutBucketAsync(bucket, region: "eu-west-1"))
        {
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            var xml = await resp.Content.ReadAsStringAsync();
            var code = XDocument.Parse(xml).Root!.Element("Code")!.Value;
            Assert.Equal("BucketAlreadyOwnedByYou", code);
        }

        await DeleteBucketAsync(bucket);
    }

    private async Task<HttpResponseMessage> PutBucketAsync(string bucket, string region)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, "/" + bucket);
        var req = new HttpRequestMessage(HttpMethod.Put, absolute);
        var body = Array.Empty<byte>();
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentLength = 0;
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret, region: region);
        return await _fx.Client.SendAsync(req);
    }

    private async Task DeleteBucketAsync(string bucket)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, "/" + bucket);
        using var req = new HttpRequestMessage(HttpMethod.Delete, absolute);
        TestSigV4Signer.SignHeader(req, Array.Empty<byte>(), _fx.AccessKeyId, _fx.Secret);
        using var _ = await _fx.Client.SendAsync(req);
    }
}
