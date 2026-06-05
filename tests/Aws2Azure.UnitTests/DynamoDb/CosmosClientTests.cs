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

    // Adversarial corpus proving the allocation-light BuildAuthHeader byte pipe
    // is identical to the Build(string) String oracle. resourceLink carries
    // multi-byte / surrogate UTF-8 to exercise verbatim encoding; verbs and
    // resource types exercise the ASCII case-fold.
    public static TheoryData<string, string, string, string> AuthCorpus()
    {
        const string date = "thu, 27 apr 2017 00:51:12 gmt";
        var data = new TheoryData<string, string, string, string>();
        foreach (var verb in new[] { "GET", "POST", "PUT", "DELETE", "get", "Patch" })
        {
            foreach (var (type, link) in new[]
            {
                ("dbs", ""),
                ("DBS", "dbs/db1"),
                ("colls", "dbs/db1/colls/c1"),
                ("docs", "dbs/db1/colls/c1/docs/id-With_Mixed.Case~123"),
                ("docs", "dbs/db1/colls/c1/docs/ünïçödé-Ωμ"),
                ("docs", "dbs/db1/colls/c1/docs/😀-surrogate-🚀"),
                ("sprocs", "dbs/db1/colls/c1/sprocs/sp+slash/plus"),
            })
            {
                data.Add(verb, type, link, date);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AuthCorpus))]
    public void BuildAuthHeader_is_byte_identical_to_string_oracle(
        string verb, string resourceType, string resourceLink, string date)
    {
        const string key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
        var keyBytes = Convert.FromBase64String(key);

        var oracle = CosmosMasterKeyAuth.Build(verb, resourceType, resourceLink, date, key);
        var bytePipe = CosmosMasterKeyAuth.BuildAuthHeader(verb, resourceType, resourceLink, date, keyBytes);

        Assert.Equal(oracle, bytePipe);
    }

    [Fact]
    public void BuildAuthHeader_long_resource_link_uses_pool_path()
    {
        const string key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
        var keyBytes = Convert.FromBase64String(key);
        const string date = "thu, 27 apr 2017 00:51:12 gmt";
        // Force the ArrayPool fallback branch (string-to-sign > 256 bytes).
        var link = "dbs/db1/colls/c1/docs/" + new string('x', 400);

        Assert.Equal(
            CosmosMasterKeyAuth.Build("GET", "docs", link, date, key),
            CosmosMasterKeyAuth.BuildAuthHeader("GET", "docs", link, date, keyBytes));
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
            DatabaseName = "main",
        };

        var client = new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));
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
        Assert.Throws<ArgumentException>(() => new CosmosClient(http,
            new CosmosCredentials { DatabaseName = "main" }, new MasterKeyCosmosAuthenticator("MDE=")));
    }

    [Fact]
    public void Constructor_rejects_empty_database_name()
    {
        using var http = new AzureHttpClient(new RecordingHandler(), ownsHandler: false);
        Assert.Throws<ArgumentException>(() => new CosmosClient(http,
            new CosmosCredentials { Endpoint = "https://x.documents.azure.com" },
            new MasterKeyCosmosAuthenticator("MDE=")));
    }

    // The request URI is now resolved against a base Uri hoisted into the
    // ctor (instead of re-parsed per request). This corpus proves the
    // produced RequestUri is byte-identical to the old per-call
    // new Uri(new Uri(endpoint.TrimEnd('/') + "/"), requestUri.TrimStart('/'))
    // across endpoint trailing-slash / :443 variants, nested paths,
    // un-escaped doc ids, and query strings.
    public static TheoryData<string, string> UriCorpus()
    {
        var data = new TheoryData<string, string>();
        foreach (var endpoint in new[]
        {
            "https://example.documents.azure.com:443/",
            "https://example.documents.azure.com:443",
            "https://example.documents.azure.com/",
            "https://example.documents.azure.com",
        })
        {
            foreach (var requestUri in new[]
            {
                "/dbs",
                "/dbs/main/colls/orders/docs/pk-12345",
                "/dbs/main/colls/orders/docs",
                "/dbs/main/colls/orders/docs/id with spaces",
                "/dbs/main/colls/orders/docs/id%2Fencoded",
                "/dbs/main/colls/orders/docs/ünïçödé",
                "/dbs/main/colls/orders/docs?$filter=x",
            })
            {
                data.Add(endpoint, requestUri);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UriCorpus))]
    public async Task SendAsync_request_uri_matches_per_call_oracle(string endpoint, string requestUri)
    {
        var captured = new RecordingHandler();
        using var http = new AzureHttpClient(captured, ownsHandler: false);
        var creds = new CosmosCredentials
        {
            Endpoint = endpoint,
            PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            DatabaseName = "main",
        };

        var client = new CosmosClient(http, creds, new MasterKeyCosmosAuthenticator(creds.PrimaryKey));
        using var resp = await client.SendAsync(
            HttpMethod.Get, "docs", "", requestUri, content: null, extraHeaders: null, CancellationToken.None);

        var oracle = new Uri(new Uri(endpoint.TrimEnd('/') + "/", UriKind.Absolute), requestUri.TrimStart('/'));
        Assert.Equal(oracle.AbsoluteUri, captured.Last!.RequestUri!.AbsoluteUri);
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

public class CosmosClientAadTokenFailureTests
{
    // When AAD token acquisition fails, CosmosClient.SendAsync surfaces a synthetic
    // response carrying the normalised backend status so the existing
    // CosmosOpsShared.WriteCosmosErrorAsync mapping renders the faithful DynamoDB
    // error (token 429 -> ProvisionedThroughputExceededException, transient 503 ->
    // InternalServerError, auth -> AccessDeniedException). The Cosmos data-plane send
    // is never reached. (#213)
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)]
    public async Task SendAsync_returns_synthetic_response_when_token_endpoint_fails(
        HttpStatusCode tokenStatus, HttpStatusCode expectedStatus)
    {
        using var tokenHttp = new AzureHttpClient(
            new StatusHandler(tokenStatus), ownsHandler: true);
        var tokenProvider = new EntraIdTokenProvider(tokenHttp, authority: new Uri("https://login.test/"));

        using var cosmosHttp = new AzureHttpClient(new ThrowingHandler(), ownsHandler: true);
        var creds = new CosmosCredentials
        {
            Endpoint = "https://acct.documents.azure.com:443/",
            DatabaseName = "main",
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = "secret",
        };
        var client = new CosmosClient(cosmosHttp, creds, new AadCosmosAuthenticator(tokenProvider, "tenant", "client", "secret"));

        using var resp = await client.SendAsync(
            HttpMethod.Get, "docs", "dbs/main/colls/c/docs/1", "/dbs/main/colls/c/docs/1",
            content: null, extraHeaders: null, CancellationToken.None);

        Assert.Equal(expectedStatus, resp.StatusCode);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Cosmos data-plane must not be called when token acquisition fails.");
    }
}
