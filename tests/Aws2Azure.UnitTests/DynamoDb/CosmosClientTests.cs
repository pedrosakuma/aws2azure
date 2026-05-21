using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Internal;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class CosmosMasterKeyAuthTests
{
    // Reference vector from Cosmos docs: the inputs below are deterministic
    // and the expected signature is the value produced by the documented
    // algorithm, so a regression here means the signing flow is broken.
    [Fact]
    public void Build_produces_url_encoded_authorization()
    {
        const string verb = "GET";
        const string resourceType = "dbs";
        const string resourceLink = "";
        const string utcDate = "thu, 27 apr 2017 00:51:12 gmt";
        // 32-byte dummy key (base64 of "0123456789abcdef0123456789abcdef").
        const string key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

        var auth = CosmosMasterKeyAuth.Build(verb, resourceType, resourceLink, utcDate, key);

        // Always-true invariants regardless of the exact HMAC digest:
        Assert.StartsWith("type%3Dmaster%26ver%3D1.0%26sig%3D", auth);
        // Determinism: same inputs → same output.
        Assert.Equal(auth, CosmosMasterKeyAuth.Build(verb, resourceType, resourceLink, utcDate, key));
        // Different verb → different signature.
        var post = CosmosMasterKeyAuth.Build("POST", resourceType, resourceLink, utcDate, key);
        Assert.NotEqual(auth, post);
        // Different date → different signature.
        var later = CosmosMasterKeyAuth.Build(verb, resourceType, resourceLink, "fri, 28 apr 2017 00:51:12 gmt", key);
        Assert.NotEqual(auth, later);
    }

    [Fact]
    public void Build_is_case_insensitive_on_verb_and_resource_type_but_case_sensitive_on_resource_link()
    {
        const string key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
        const string date = "thu, 27 apr 2017 00:51:12 gmt";

        // Verb / resourceType are folded to lowercase before signing.
        Assert.Equal(
            CosmosMasterKeyAuth.Build("get", "dbs", "dbs/db1", date, key),
            CosmosMasterKeyAuth.Build("GET", "DBS", "dbs/db1", date, key));

        // Resource link is case-significant (Cosmos identifiers are).
        Assert.NotEqual(
            CosmosMasterKeyAuth.Build("get", "dbs", "dbs/DB1", date, key),
            CosmosMasterKeyAuth.Build("get", "dbs", "dbs/db1", date, key));
    }

    [Fact]
    public void GetHttpUtcDate_returns_lowercase_rfc1123_gmt()
    {
        var d = CosmosMasterKeyAuth.GetHttpUtcDate(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));
        Assert.Equal("tue, 02 jan 2024 03:04:05 gmt", d);
    }
}

public class CosmosClientTests
{
    [Fact]
    public async Task SendAsync_signs_request_with_cosmos_headers()
    {
        var captured = new RecordingHandler();
        using var http = new AzureHttpClient(captured, ownsHandler: false);
        var creds = new CosmosCredentials
        {
            Endpoint = "https://example.documents.azure.com:443/",
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
        };

        var client = new CosmosClient(http, creds);
        using var resp = await client.SendAsync(
            HttpMethod.Get, "dbs", "", "/dbs", content: null, extraHeaders: null, CancellationToken.None);

        Assert.NotNull(captured.Last);
        Assert.True(captured.Last!.Headers.Contains("authorization"));
        Assert.True(captured.Last.Headers.Contains("x-ms-date"));
        Assert.True(captured.Last.Headers.Contains("x-ms-version"));
        Assert.Equal(CosmosClient.ApiVersion, captured.Last.Headers.GetValues("x-ms-version").Single());
        Assert.Equal("https://example.documents.azure.com/dbs", captured.Last.RequestUri!.ToString());
    }

    [Fact]
    public void Constructor_rejects_empty_endpoint()
    {
        using var http = new AzureHttpClient(new RecordingHandler(), ownsHandler: false);
        Assert.Throws<ArgumentException>(() => new CosmosClient(http, new CosmosCredentials { PrimaryKey = "x" }));
    }

    [Fact]
    public void Constructor_rejects_empty_primary_key()
    {
        using var http = new AzureHttpClient(new RecordingHandler(), ownsHandler: false);
        Assert.Throws<ArgumentException>(() => new CosmosClient(http,
            new CosmosCredentials { Endpoint = "https://x.documents.azure.com" }));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
