using System.Net;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 slice-3 coverage: object listing (V1 + V2) with prefix, delimiter,
/// continuation, encoding-type, and bucket-not-found handling.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3ListObjectsTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly S3IntegrationFixture _fx;
    public S3ListObjectsTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task ListObjectsV2_basic_flat_listing()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "alpha.txt", "1"u8.ToArray());
        await PutObject(bucket, "beta.txt", "22"u8.ToArray());
        await PutObject(bucket, "gamma.txt", "333"u8.ToArray());

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?list-type=2", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ListBucketResult", doc.Root!.Name.LocalName);
        var keys = doc.Root!.Elements(S3Ns + "Contents")
            .Select(c => c.Element(S3Ns + "Key")!.Value).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "alpha.txt", "beta.txt", "gamma.txt" }, keys);
        Assert.Equal("3", doc.Root!.Element(S3Ns + "KeyCount")!.Value);
        Assert.Equal("false", doc.Root!.Element(S3Ns + "IsTruncated")!.Value);

        var sizes = doc.Root!.Elements(S3Ns + "Contents")
            .Select(c => c.Element(S3Ns + "Size")!.Value).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "1", "2", "3" }, sizes);
    }

    [SkippableFact]
    public async Task ListObjectsV2_prefix_and_delimiter_emits_common_prefixes()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "a/file1.txt", "x"u8.ToArray());
        await PutObject(bucket, "a/sub/file2.txt", "x"u8.ToArray());
        await PutObject(bucket, "a/sub/file3.txt", "x"u8.ToArray());
        await PutObject(bucket, "b/other.txt", "x"u8.ToArray());

        using var resp = await SendAsync(HttpMethod.Get,
            $"/{bucket}?list-type=2&prefix=a/&delimiter=/", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());

        var keys = doc.Root!.Elements(S3Ns + "Contents")
            .Select(c => c.Element(S3Ns + "Key")!.Value).ToArray();
        Assert.Equal(new[] { "a/file1.txt" }, keys);

        var cps = doc.Root!.Elements(S3Ns + "CommonPrefixes")
            .Select(c => c.Element(S3Ns + "Prefix")!.Value).ToArray();
        Assert.Equal(new[] { "a/sub/" }, cps);
        Assert.Equal("/", doc.Root!.Element(S3Ns + "Delimiter")!.Value);
        Assert.Equal("a/", doc.Root!.Element(S3Ns + "Prefix")!.Value);
    }

    [SkippableFact]
    public async Task ListObjectsV2_paginates_via_continuation_token()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var expected = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var k = $"k{i:D2}.bin";
            expected.Add(k);
            await PutObject(bucket, k, new byte[] { (byte)i });
        }
        expected.Sort(StringComparer.Ordinal);

        var seen = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            pages++;
            Assert.InRange(pages, 1, 10);
            var query = "?list-type=2&max-keys=2" + (token is null ? "" : "&continuation-token=" + Uri.EscapeDataString(token));
            using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}{query}", Array.Empty<byte>());
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());

            seen.AddRange(doc.Root!.Elements(S3Ns + "Contents")
                .Select(c => c.Element(S3Ns + "Key")!.Value));

            var truncated = doc.Root!.Element(S3Ns + "IsTruncated")!.Value == "true";
            token = truncated
                ? doc.Root!.Element(S3Ns + "NextContinuationToken")?.Value
                : null;
            if (truncated)
            {
                Assert.False(string.IsNullOrEmpty(token));
            }
        } while (token is not null);

        Assert.Equal(expected, seen);
    }

    [SkippableFact]
    public async Task ListObjectsV2_encoding_type_url_percent_encodes_keys()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "with space/file.txt", "x"u8.ToArray());

        using var resp = await SendAsync(HttpMethod.Get,
            $"/{bucket}?list-type=2&encoding-type=url", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("url", doc.Root!.Element(S3Ns + "EncodingType")!.Value);
        var key = doc.Root!.Element(S3Ns + "Contents")!.Element(S3Ns + "Key")!.Value;
        Assert.Equal("with%20space%2Ffile.txt", key);
    }

    [SkippableFact]
    public async Task ListObjects_v1_uses_marker_pagination()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "a.txt", "1"u8.ToArray());
        await PutObject(bucket, "b.txt", "2"u8.ToArray());
        await PutObject(bucket, "c.txt", "3"u8.ToArray());

        // V1 — no list-type. Marker is exclusive; expect b.txt and c.txt.
        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?marker=a.txt", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ListBucketResult", doc.Root!.Name.LocalName);
        // V1 must not emit KeyCount / NextContinuationToken.
        Assert.Null(doc.Root!.Element(S3Ns + "KeyCount"));
        Assert.Null(doc.Root!.Element(S3Ns + "NextContinuationToken"));
        Assert.Equal("a.txt", doc.Root!.Element(S3Ns + "Marker")!.Value);
        var keys = doc.Root!.Elements(S3Ns + "Contents")
            .Select(c => c.Element(S3Ns + "Key")!.Value).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "b.txt", "c.txt" }, keys);
    }

    [SkippableFact]
    public async Task ListObjectsV2_on_missing_bucket_returns_NoSuchBucket()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "ghost-" + Guid.NewGuid().ToString("N")[..10];
        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?list-type=2", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>NoSuchBucket</Code>", xml);
    }

    [SkippableFact]
    public async Task ListObjectsV2_malformed_continuation_token_returns_InvalidArgument()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Get,
            $"/{bucket}?list-type=2&continuation-token=!!notbase64!!", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>InvalidArgument</Code>", xml);
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
        byte[] body)
    {
        var absolute = new Uri(_fx.Client.BaseAddress!, pathAndQuery);
        var req = new HttpRequestMessage(method, absolute);
        if (body.Length > 0 || method == HttpMethod.Put || method == HttpMethod.Post)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentLength = body.Length;
        }
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
