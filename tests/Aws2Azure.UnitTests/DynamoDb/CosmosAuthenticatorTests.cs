using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class CosmosAuthenticatorTests
{
    [Fact]
    public async Task MasterKey_sets_authorization_and_xms_date()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://example/dbs");
        var auth = new MasterKeyCosmosAuthenticator(
            "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            clock: () => new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));

        await auth.AuthenticateAsync(req, "dbs", "", CancellationToken.None);

        Assert.True(req.Headers.Contains("authorization"));
        Assert.True(req.Headers.Contains("x-ms-date"));
        Assert.Equal("tue, 02 jan 2024 03:04:05 gmt", string.Join(",", req.Headers.GetValues("x-ms-date")));
    }

    [Fact]
    public async Task Aad_emits_url_encoded_aad_signature_and_xms_date()
    {
        // EntraIdTokenProvider hits a token endpoint; supply a scripted
        // handler that returns a JSON access_token payload.
        var fixedToken = "eyJhbGciOiJIUzI1NiJ9.payload.sig+/=";
        var tokenJson = "{\"access_token\":\"" + fixedToken
            + "\",\"token_type\":\"Bearer\",\"expires_in\":3600}";

        var tokenHandler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenJson, Encoding.UTF8, "application/json"),
            });
        var http = new AzureHttpClient(tokenHandler, ownsHandler: false);
        var provider = new EntraIdTokenProvider(http);

        var auth = new AadCosmosAuthenticator(
            provider,
            new AadAuthSettings(AzureAuthMode.ClientSecret, "tenant", "client", "secret"),
            clock: () => new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var req = new HttpRequestMessage(HttpMethod.Get, "https://example/dbs");
        await auth.AuthenticateAsync(req, "dbs", "", CancellationToken.None);

        var authHeader = string.Join(",", req.Headers.GetValues("authorization"));
        Assert.StartsWith("type%3Daad%26ver%3D1.0%26sig%3D", authHeader);
        // Token contained '+' and '/' — must be percent-encoded.
        Assert.Contains("%2B", authHeader);
        Assert.Contains("%2F", authHeader);
        Assert.Equal("tue, 02 jan 2024 03:04:05 gmt", string.Join(",", req.Headers.GetValues("x-ms-date")));
    }

    [Fact]
    public async Task Aad_caches_token_across_requests()
    {
        var tokenJson = "{\"access_token\":\"tok\",\"token_type\":\"Bearer\",\"expires_in\":3600}";
        var tokenHandler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenJson, Encoding.UTF8, "application/json"),
            });
        var http = new AzureHttpClient(tokenHandler, ownsHandler: false);
        var provider = new EntraIdTokenProvider(http);
        var auth = new AadCosmosAuthenticator(provider, new AadAuthSettings(AzureAuthMode.ClientSecret, "t", "c", "s"));

        for (int i = 0; i < 3; i++)
        {
            tokenHandler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenJson, Encoding.UTF8, "application/json"),
            };
            var req = new HttpRequestMessage(HttpMethod.Get, "https://example/dbs");
            await auth.AuthenticateAsync(req, "dbs", "", CancellationToken.None);
        }

        // Token endpoint should be hit exactly once due to in-memory caching.
        Assert.Equal(1, tokenHandler.CallCount);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public RecordingHandler(HttpResponseMessage initial) { NextResponse = initial; }
        public HttpResponseMessage NextResponse { get; set; }
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(NextResponse);
        }
    }
}
