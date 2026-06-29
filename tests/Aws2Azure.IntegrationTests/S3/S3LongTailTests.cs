using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// Phase-1 Slice-9 coverage: long-tail S3 subresources (tagging, ACL,
/// configuration stubs, object-scope stubs).
///
/// Tagging on objects exercises a real Azure-side translation (Blob Index
/// Tags). Bucket tagging round-trips through container metadata. ACL and
/// the never-configured "Get*" surfaces are local stubs and do not touch
/// Azure beyond the optional bucket-existence check the handler already
/// performs.
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3LongTailTests
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly S3IntegrationFixture _fx;
    public S3LongTailTests(S3IntegrationFixture fx) => _fx = fx;

    // ── Object tagging ────────────────────────────────────────────────

    [SkippableFact]
    public async Task PutObjectTagging_round_trips_via_blob_index_tags()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "doc.txt", "hello"u8.ToArray());

        var put = "<Tagging><TagSet>" +
                  "<Tag><Key>env</Key><Value>prod</Value></Tag>" +
                  "<Tag><Key>owner</Key><Value>team-a</Value></Tag>" +
                  "</TagSet></Tagging>";
        using (var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/doc.txt?tagging",
            Encoding.UTF8.GetBytes(put), contentType: "application/xml"))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"PutObjectTagging → {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        }

        using var get = await SendAsync(HttpMethod.Get, $"/{bucket}/doc.txt?tagging", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = XDocument.Parse(await get.Content.ReadAsStringAsync());
        var tags = doc.Root!.Element(S3Ns + "TagSet")!.Elements(S3Ns + "Tag")
            .Select(t => (t.Element(S3Ns + "Key")!.Value, t.Element(S3Ns + "Value")!.Value))
            .OrderBy(t => t.Item1).ToList();
        Assert.Equal(new[] { ("env", "prod"), ("owner", "team-a") }, tags);

        using var del = await SendAsync(HttpMethod.Delete, $"/{bucket}/doc.txt?tagging", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var afterDel = await SendAsync(HttpMethod.Get, $"/{bucket}/doc.txt?tagging", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, afterDel.StatusCode);
        var after = XDocument.Parse(await afterDel.Content.ReadAsStringAsync());
        Assert.Empty(after.Root!.Element(S3Ns + "TagSet")!.Elements());
    }

    [SkippableFact]
    public async Task PutObjectTagging_with_too_many_tags_is_rejected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "k", "x"u8.ToArray());

        var sb = new StringBuilder("<Tagging><TagSet>");
        for (var i = 0; i < 11; i++) sb.Append($"<Tag><Key>k{i}</Key><Value>v</Value></Tag>");
        sb.Append("</TagSet></Tagging>");
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/k?tagging",
            Encoding.UTF8.GetBytes(sb.ToString()), contentType: "application/xml");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Bucket tagging ────────────────────────────────────────────────

    [SkippableFact]
    public async Task PutBucketTagging_round_trips_via_container_metadata()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using (var notFound = await SendAsync(HttpMethod.Get, $"/{bucket}?tagging", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            Assert.Contains("NoSuchTagSet", await notFound.Content.ReadAsStringAsync());
        }

        var put = "<Tagging><TagSet><Tag><Key>cost-center</Key><Value>42</Value></Tag></TagSet></Tagging>";
        using (var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?tagging",
            Encoding.UTF8.GetBytes(put), contentType: "application/xml"))
        {
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }

        using var get = await SendAsync(HttpMethod.Get, $"/{bucket}?tagging", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = XDocument.Parse(await get.Content.ReadAsStringAsync());
        var tag = doc.Root!.Element(S3Ns + "TagSet")!.Element(S3Ns + "Tag")!;
        Assert.Equal("cost-center", tag.Element(S3Ns + "Key")!.Value);
        Assert.Equal("42", tag.Element(S3Ns + "Value")!.Value);

        using var del = await SendAsync(HttpMethod.Delete, $"/{bucket}?tagging", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var afterDel = await SendAsync(HttpMethod.Get, $"/{bucket}?tagging", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, afterDel.StatusCode);
    }

    // ── Bucket versioning ─────────────────────────────────────────────

    [SkippableFact]
    public async Task PutBucketVersioning_round_trips_via_container_metadata()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        // Never configured → empty document, no <Status>.
        using (var initial = await SendAsync(HttpMethod.Get, $"/{bucket}?versioning", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            var doc = XDocument.Parse(await initial.Content.ReadAsStringAsync());
            Assert.Null(doc.Root!.Element(S3Ns + "Status"));
        }

        var enable = "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>";
        using (var put = await SendAsync(HttpMethod.Put, $"/{bucket}?versioning",
            Encoding.UTF8.GetBytes(enable), contentType: "application/xml"))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using (var get = await SendAsync(HttpMethod.Get, $"/{bucket}?versioning", Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var doc = XDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.Equal("Enabled", doc.Root!.Element(S3Ns + "Status")!.Value);
        }

        var suspend = "<VersioningConfiguration><Status>Suspended</Status></VersioningConfiguration>";
        using (var put = await SendAsync(HttpMethod.Put, $"/{bucket}?versioning",
            Encoding.UTF8.GetBytes(suspend), contentType: "application/xml"))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using var after = await SendAsync(HttpMethod.Get, $"/{bucket}?versioning", Array.Empty<byte>());
        var doc2 = XDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal("Suspended", doc2.Root!.Element(S3Ns + "Status")!.Value);
    }

    [SkippableTheory]
    [InlineData("<VersioningConfiguration><Status>Bogus</Status></VersioningConfiguration>")]
    [InlineData("<NotVersioning><Status>Enabled</Status></NotVersioning>")]
    [InlineData("<VersioningConfiguration><Status>Enabled</Status>")]
    public async Task PutBucketVersioning_with_malformed_status_returns_MalformedXML(string bad)
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?versioning",
            Encoding.UTF8.GetBytes(bad), contentType: "application/xml");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("MalformedXML", await resp.Content.ReadAsStringAsync());
    }

    // ── ACL ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListObjectVersions_lists_current_objects_as_versions()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using (var p1 = await SendAsync(HttpMethod.Put, $"/{bucket}/a.txt",
            Encoding.UTF8.GetBytes("aaa"), contentType: "text/plain"))
            Assert.Equal(HttpStatusCode.OK, p1.StatusCode);
        using (var p2 = await SendAsync(HttpMethod.Put, $"/{bucket}/b.txt",
            Encoding.UTF8.GetBytes("bbbbb"), contentType: "text/plain"))
            Assert.Equal(HttpStatusCode.OK, p2.StatusCode);

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?versions", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ListVersionsResult", doc.Root!.Name.LocalName);

        var versions = doc.Root!.Elements(S3Ns + "Version").ToArray();
        Assert.Equal(2, versions.Length);
        var keys = versions.Select(v => v.Element(S3Ns + "Key")!.Value).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "a.txt", "b.txt" }, keys);
        // Unversioned Azurite account → each blob is the current version.
        Assert.All(versions, v => Assert.Equal("true", v.Element(S3Ns + "IsLatest")!.Value));
        Assert.All(versions, v => Assert.NotNull(v.Element(S3Ns + "VersionId")));
    }

    [SkippableFact]
    public async Task GetBucketAcl_reports_ownership_only_shape()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?acl", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var perm = doc.Root!.Element(S3Ns + "AccessControlList")!.Element(S3Ns + "Grant")!
            .Element(S3Ns + "Permission")!.Value;
        Assert.Equal("FULL_CONTROL", perm);
    }

    [SkippableFact]
    public async Task PutBucketAcl_with_public_read_is_rejected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?acl", Array.Empty<byte>(),
            extraHeaders: new[] { ("x-amz-acl", "public-read") });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("AccessControlListNotSupported", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task PutBucketAcl_with_canned_private_is_accepted()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?acl", Array.Empty<byte>(),
            extraHeaders: new[] { ("x-amz-acl", "private") });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Configuration stubs: probe one of each category ────────────────

    [SkippableTheory]
    [InlineData("lifecycle", HttpStatusCode.NotFound, "NoSuchLifecycleConfiguration")]
    [InlineData("cors", HttpStatusCode.NotFound, "NoSuchCORSConfiguration")]
    [InlineData("website", HttpStatusCode.NotFound, "NoSuchWebsiteConfiguration")]
    [InlineData("replication", HttpStatusCode.NotFound, "ReplicationConfigurationNotFoundError")]
    [InlineData("encryption", HttpStatusCode.NotFound, "ServerSideEncryptionConfigurationNotFoundError")]
    [InlineData("publicAccessBlock", HttpStatusCode.NotFound, "NoSuchPublicAccessBlockConfiguration")]
    [InlineData("policy", HttpStatusCode.NotFound, "NoSuchBucketPolicy")]
    [InlineData("object-lock", HttpStatusCode.NotFound, "ObjectLockConfigurationNotFoundError")]
    [InlineData("ownershipControls", HttpStatusCode.NotFound, "OwnershipControlsNotFoundError")]
    public async Task GetBucket_configuration_probes_return_NoSuch_404(string sub, HttpStatusCode expected, string code)
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?{sub}", Array.Empty<byte>());
        Assert.Equal(expected, resp.StatusCode);
        Assert.Contains(code, await resp.Content.ReadAsStringAsync());
    }

    [SkippableTheory]
    [InlineData("logging", "BucketLoggingStatus")]
    [InlineData("versioning", "VersioningConfiguration")]
    [InlineData("notification", "NotificationConfiguration")]
    [InlineData("accelerate", "AccelerateConfiguration")]
    public async Task GetBucket_default_configuration_returns_empty_200(string sub, string rootElement)
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?{sub}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<" + rootElement, body);
    }

    [SkippableFact]
    public async Task GetBucketRequestPayment_returns_BucketOwner_default()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?requestPayment", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("BucketOwner", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task PutBucket_configuration_stub_returns_501_NotImplemented()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?lifecycle",
            "<LifecycleConfiguration/>"u8.ToArray(), contentType: "application/xml");
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    [SkippableFact]
    public async Task DeleteBucket_configuration_stub_returns_204()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        using var resp = await SendAsync(HttpMethod.Delete, $"/{bucket}?lifecycle", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [SkippableTheory]
    [InlineData("torrent", HttpMethodEnum.Get)]
    [InlineData("retention", HttpMethodEnum.Get)]
    [InlineData("legal-hold", HttpMethodEnum.Get)]
    public async Task GetObject_subresource_stub_returns_501(string sub, HttpMethodEnum method)
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        await PutObject(bucket, "k", "x"u8.ToArray());

        var m = method == HttpMethodEnum.Get ? HttpMethod.Get : HttpMethod.Put;
        using var resp = await SendAsync(m, $"/{bucket}/k?{sub}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
    }

    // ── Review-finding coverage (Slice 9) ─────────────────────────────

    [SkippableFact]
    public async Task GetBucketAcl_on_missing_bucket_returns_NoSuchBucket()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-missing-" + Guid.NewGuid().ToString("N")[..10];
        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?acl", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("NoSuchBucket", await resp.Content.ReadAsStringAsync());
    }

    [SkippableTheory]
    [InlineData("lifecycle")]
    [InlineData("cors")]
    [InlineData("website")]
    [InlineData("versioning")]
    [InlineData("logging")]
    public async Task Get_bucket_configuration_on_missing_bucket_returns_NoSuchBucket(string sub)
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-missing-" + Guid.NewGuid().ToString("N")[..10];
        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}?{sub}", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("NoSuchBucket", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task GetObjectAcl_on_missing_object_returns_NoSuchKey()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);
        using var resp = await SendAsync(HttpMethod.Get, $"/{bucket}/no-such-key?acl", Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("NoSuchKey", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task PutBucketAcl_with_explicit_non_owner_grantee_is_rejected()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        // Well-formed AccessControlPolicy but with a forged Grantee ID
        // that does not match the proxy's deterministic owner ID.
        var body = """
            <AccessControlPolicy>
              <Owner><ID>not-the-owner</ID></Owner>
              <AccessControlList>
                <Grant>
                  <Grantee xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:type="CanonicalUser">
                    <ID>some-other-user</ID>
                  </Grantee>
                  <Permission>FULL_CONTROL</Permission>
                </Grant>
              </AccessControlList>
            </AccessControlPolicy>
            """;
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?acl",
            Encoding.UTF8.GetBytes(body), contentType: "application/xml");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("AccessControlListNotSupported", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task PutBucketTagging_with_bad_content_md5_returns_BadDigest()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        var put = "<Tagging><TagSet><Tag><Key>k</Key><Value>v</Value></Tag></TagSet></Tagging>";
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?tagging",
            Encoding.UTF8.GetBytes(put), contentType: "application/xml",
            extraHeaders: new[] { ("Content-MD5", "deadbeefdeadbeefdeadbeefdeadbe==") });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("BadDigest", await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task PutBucketTagging_with_oversized_body_returns_EntityTooLarge()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];
        await PutBucket(bucket);

        // 70 KiB exceeds the 64 KiB cap (declared via Content-Length).
        var body = new byte[70 * 1024];
        Array.Fill(body, (byte)'x');
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}?tagging", body,
            contentType: "application/xml");
        Assert.Equal((HttpStatusCode)413, resp.StatusCode);
        Assert.Contains("EntityTooLarge", await resp.Content.ReadAsStringAsync());
    }

    public enum HttpMethodEnum { Get, Put }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task PutBucket(string bucket)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", Array.Empty<byte>());
        Assert.True(resp.IsSuccessStatusCode);
    }

    private async Task PutObject(string bucket, string key, byte[] body)
    {
        using var resp = await SendAsync(HttpMethod.Put, $"/{bucket}/{key}", body,
            contentType: "application/octet-stream");
        Assert.True(resp.IsSuccessStatusCode, $"PutObject → {(int)resp.StatusCode}");
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
                if (n.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && req.Content is not null)
                {
                    req.Content.Headers.TryAddWithoutValidation(n, v);
                }
                else
                {
                    req.Headers.TryAddWithoutValidation(n, v);
                }
            }
        }
        TestSigV4Signer.SignHeader(req, body, _fx.AccessKeyId, _fx.Secret);
        return await _fx.Client.SendAsync(req);
    }
}
