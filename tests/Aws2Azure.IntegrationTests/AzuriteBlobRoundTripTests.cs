using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests;

/// <summary>
/// Exercises <see cref="SharedKeyAuthenticator"/> end-to-end against a real
/// Azurite container managed by Testcontainers. This proves the canonicalization
/// and HMAC chain match what Azure (and Azurite) actually accept, without any
/// Azure SDK on the client side.
///
/// Skipped automatically when Docker is not reachable.
/// </summary>
[Collection(AzuriteCollection.Name)]
public class AzuriteBlobRoundTripTests
{
    private readonly AzuriteFixture _fx;
    public AzuriteBlobRoundTripTests(AzuriteFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task PutBlob_ThenGetBlob_RoundTripsBytes()
    {
        Skip.IfNot(_fx.DockerAvailable, "Docker not available; skipping Azurite integration test.");

        var container = "it-" + Guid.NewGuid().ToString("N")[..8];
        var blob = "hello.txt";
        var payload = Encoding.UTF8.GetBytes("hello azure from aws2azure\n");

        var auth = new SharedKeyAuthenticator(AzuriteFixture.AccountName, AzuriteFixture.AccountKey);
        using var http = new AzureHttpClient();

        // PUT container.
        using (var req = new HttpRequestMessage(HttpMethod.Put, $"{_fx.BlobEndpoint}/{container}?restype=container"))
        {
            req.Content = new ByteArrayContent(Array.Empty<byte>());
            req.Content.Headers.ContentLength = 0;
            await auth.AuthenticateAsync(req);
            using var response = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
            Assert.True(response.IsSuccessStatusCode, $"PUT container failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        // PUT blob.
        using (var req = new HttpRequestMessage(HttpMethod.Put, $"{_fx.BlobEndpoint}/{container}/{blob}"))
        {
            req.Content = new ByteArrayContent(payload);
            req.Content.Headers.ContentLength = payload.Length;
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            req.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            await auth.AuthenticateAsync(req);
            using var response = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
            Assert.True(response.IsSuccessStatusCode, $"PUT blob failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        // GET blob.
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"{_fx.BlobEndpoint}/{container}/{blob}"))
        {
            await auth.AuthenticateAsync(req);
            using var response = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
            Assert.True(response.IsSuccessStatusCode, $"GET blob failed: {(int)response.StatusCode}");
            var roundTripped = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(payload, roundTripped);
        }
    }
}
