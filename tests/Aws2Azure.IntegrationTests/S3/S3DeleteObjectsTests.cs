using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 slice-5 coverage: multi-object DeleteObjects batch.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3DeleteObjectsTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly S3IntegrationFixture _fx;
    public S3DeleteObjectsTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task DeleteObjects_mixed_existing_and_missing_returns_all_as_deleted()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "a.txt", "aaa"u8.ToArray());
        await PutObject(bucket, "nested/b.txt", "bbb"u8.ToArray());

        var body = BuildDeleteXml(quiet: false, keys: new[] { "a.txt", "nested/b.txt", "missing.txt" });
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}?delete", body,
            contentType: "application/xml");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var deleted = doc.Root!.Elements(S3Ns + "Deleted").Select(e => e.Element(S3Ns + "Key")!.Value).ToHashSet();
        var errors = doc.Root!.Elements(S3Ns + "Error").ToList();

        Assert.Equal(new[] { "a.txt", "missing.txt", "nested/b.txt" }, deleted.OrderBy(x => x).ToArray());
        Assert.Empty(errors);

        using var head = await SendAsync(HttpMethod.Head, $"/{bucket}/a.txt", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
    }

    [SkippableFact]
    public async Task DeleteObjects_quiet_mode_only_emits_errors()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "k1", "x"u8.ToArray());
        await PutObject(bucket, "k2", "x"u8.ToArray());

        var body = BuildDeleteXml(quiet: true, keys: new[] { "k1", "k2" });
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}?delete", body,
            contentType: "application/xml");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Empty(doc.Root!.Elements(S3Ns + "Deleted"));
        Assert.Empty(doc.Root!.Elements(S3Ns + "Error"));
    }

    [SkippableFact]
    public async Task DeleteObjects_missing_bucket_returns_top_level_NoSuchBucket()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10]; // never created
        var body = BuildDeleteXml(quiet: false, keys: new[] { "k1", "k2" });
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}?delete", body,
            contentType: "application/xml");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>NoSuchBucket</Code>", xml);
    }

    [SkippableFact]
    public async Task DeleteObjects_missing_content_md5_is_rejected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var body = BuildDeleteXml(quiet: false, keys: new[] { "k1" });
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}?delete", body,
            contentType: "application/xml", includeContentMd5: false);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Content-MD5", xml);
    }

    [SkippableFact]
    public async Task DeleteObjects_wrong_content_md5_returns_BadDigest()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var body = BuildDeleteXml(quiet: false, keys: new[] { "k1" });
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}?delete", body,
            contentType: "application/xml", overrideMd5: "AAAAAAAAAAAAAAAAAAAAAA==");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>BadDigest</Code>", xml);
    }

    [SkippableFact]
    public async Task DeleteObjects_malformed_xml_returns_MalformedXML()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}?delete",
            Encoding.UTF8.GetBytes("<Delete><Object></Delete>"), contentType: "application/xml");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>MalformedXML</Code>", xml);
    }

    [SkippableFact]
    public async Task DeleteObjects_versionId_in_payload_reports_per_key_NotImplemented()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        var body = Encoding.UTF8.GetBytes(
            "<Delete><Object><Key>k</Key><VersionId>v1</VersionId></Object></Delete>");
        using var resp = await SendAsync(HttpMethod.Post, $"/{bucket}?delete", body,
            contentType: "application/xml");
        // VersionId is detected at parser level → whole-request MalformedXML.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("versionId", xml, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ----

    private static byte[] BuildDeleteXml(bool quiet, IEnumerable<string> keys)
    {
        var sb = new StringBuilder("<Delete>");
        foreach (var k in keys)
        {
            sb.Append("<Object><Key>").Append(System.Security.SecurityElement.Escape(k)).Append("</Key></Object>");
        }
        if (quiet)
        {
            sb.Append("<Quiet>true</Quiet>");
        }
        sb.Append("</Delete>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private async Task PutBucket(string bucket)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", Array.Empty<byte>());
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket} → {(int)resp.StatusCode}");
    }

    private async Task PutObject(string bucket, string key, byte[] body)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/{key}", body, contentType: "application/octet-stream");
        Assert.True(resp.IsSuccessStatusCode, $"PUT /{bucket}/{key} → {(int)resp.StatusCode}");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        byte[] body,
        string? contentType = null,
        bool includeContentMd5 = true,
        string? overrideMd5 = null)
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
            if (method == HttpMethod.Post && pathAndQuery.Contains("?delete") && (includeContentMd5 || overrideMd5 is not null))
            {
                var md5 = overrideMd5 ?? Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(body));
                req.Content.Headers.TryAddWithoutValidation("Content-MD5", md5);
            }
        }
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
