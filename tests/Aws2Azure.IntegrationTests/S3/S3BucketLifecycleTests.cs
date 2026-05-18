using System.Net;
using System.Xml.Linq;
using Xunit;

namespace Aws2Azure.IntegrationTests.S3;

/// <summary>
/// End-to-end Phase-1 slice-1 coverage: drives the proxy as an S3 endpoint
/// using hand-signed SigV4 requests, expecting it to round-trip them through
/// Azure Blob storage (Azurite).
/// </summary>
[Collection(S3IntegrationCollection.Name)]
public class S3BucketLifecycleTests
{
    private readonly S3IntegrationFixture _fx;
    public S3BucketLifecycleTests(S3IntegrationFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Create_Head_List_Delete_Roundtrip()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        var bucket = "it-" + Guid.NewGuid().ToString("N")[..10];

        using (var resp = await SendAsync(HttpMethod.Put, $"/{bucket}", body: Array.Empty<byte>()))
        {
            Assert.True(resp.IsSuccessStatusCode,
                $"PUT /{bucket} → {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
            Assert.Equal("/" + bucket, resp.Headers.Location?.ToString());
        }

        using (var resp = await SendAsync(HttpMethod.Head, $"/{bucket}", body: Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        using (var resp = await SendAsync(HttpMethod.Get, "/", body: Array.Empty<byte>()))
        {
            Assert.True(resp.IsSuccessStatusCode);
            var xml = await resp.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
            var names = doc.Root!.Element(ns + "Buckets")!.Elements(ns + "Bucket")
                .Select(b => b.Element(ns + "Name")!.Value).ToArray();
            Assert.Contains(bucket, names);
        }

        using (var resp = await SendAsync(HttpMethod.Delete, $"/{bucket}", body: Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }

        using (var resp = await SendAsync(HttpMethod.Head, $"/{bucket}", body: Array.Empty<byte>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("NoSuchBucket", resp.Headers.GetValues("x-amz-error-code").Single());
        }
    }

    [SkippableFact]
    public async Task Unsigned_request_returns_xml_error()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping S3 integration test.");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        using var resp = await _fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.StartsWith("application/xml", resp.Content.Headers.ContentType?.ToString() ?? "");
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<Code>", xml);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathAndQuery, byte[] body)
    {
        // Resolve against the client's BaseAddress so the signer sees an
        // absolute URI (HttpRequestMessage holds the raw value otherwise).
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
