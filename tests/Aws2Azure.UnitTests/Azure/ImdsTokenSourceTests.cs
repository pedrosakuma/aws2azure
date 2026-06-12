using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public sealed class ImdsTokenSourceTests
{
    private static readonly Uri ImdsEndpoint = new("http://imds.test/metadata/identity/oauth2/token");

    [Fact]
    public async Task GetTokenAsync_SystemAssignedRequestsImdsWithMetadataHeader()
    {
        var handler = new RecordingScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ImdsTokenSource(http, endpoint: ImdsEndpoint);
        handler.Enqueue(MakeToken("tok", 3599));

        var token = await source.GetTokenAsync("https://x/.default");

        Assert.Equal("tok", token);
        Assert.Equal("true", handler.LastHeader("Metadata"));
        Assert.Null(handler.LastHeader("X-IDENTITY-HEADER"));
        Assert.Equal("2018-02-01", QueryValue(handler.LastRequestUri!, "api-version"));
        Assert.Equal("https://x/", QueryValue(handler.LastRequestUri!, "resource"));
        Assert.Null(QueryValue(handler.LastRequestUri!, "client_id"));
    }

    [Fact]
    public async Task GetTokenAsync_UserAssignedIncludesClientId()
    {
        var handler = new RecordingScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ImdsTokenSource(http, clientId: "client-123", endpoint: ImdsEndpoint);
        handler.Enqueue(MakeToken("tok", 3599));

        _ = await source.GetTokenAsync("https://x/.default");

        Assert.Equal("client-123", QueryValue(handler.LastRequestUri!, "client_id"));
    }

    [Theory]
    [InlineData("https://cosmos.azure.com/.default", "https://cosmos.azure.com/")]
    [InlineData("https://storage.azure.com/", "https://storage.azure.com/")]
    [InlineData("https://example/.defaultx", "https://example/.defaultx")]
    [InlineData("scope", "scope")]
    public void ScopeToResource_StripsTrailingDefaultScopeOnly(string scope, string expected)
    {
        Assert.Equal(expected, ImdsTokenSource.ScopeToResource(scope));
    }

    [Fact]
    public async Task GetTokenAsync_CachesWithinSafetyWindow()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ImdsTokenSource(http, endpoint: ImdsEndpoint, clock: fakeClock);
        handler.Enqueue(MakeToken("tok", 3599));

        Assert.Equal("tok", await source.GetTokenAsync("https://x/.default"));
        fakeClock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("tok", await source.GetTokenAsync("https://x/.default"));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_NonSuccessThrowsStatusPreservingException()
    {
        var handler = new RecordingScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ImdsTokenSource(http, endpoint: ImdsEndpoint);
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"throttled\"}", Encoding.UTF8, "application/json")
        });

        var ex = await Assert.ThrowsAsync<EntraIdTokenException>(() =>
            source.GetTokenAsync("https://x/.default").AsTask());

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.BackendStatus);
    }

    [Fact]
    public async Task GetTokenAsync_AppServiceVariantUsesIdentityHeader()
    {
        var endpoint = new Uri("http://appservice.test/msi/token");
        var handler = new RecordingScriptedHandler();
        using var http = new AzureHttpClient(handler, ownsHandler: true);
        var source = new ImdsTokenSource(
            http,
            endpoint: endpoint,
            identityHeaderName: "X-IDENTITY-HEADER",
            identityHeaderValue: "secret");
        handler.Enqueue(MakeToken("tok", 3599));

        Assert.Equal("tok", await source.GetTokenAsync("https://x/.default"));

        Assert.Equal(endpoint.GetLeftPart(UriPartial.Path), handler.LastRequestUri!.GetLeftPart(UriPartial.Path));
        Assert.Equal("2019-08-01", QueryValue(handler.LastRequestUri!, "api-version"));
        Assert.Equal("secret", handler.LastHeader("X-IDENTITY-HEADER"));
        Assert.Null(handler.LastHeader("Metadata"));
    }

    private static HttpResponseMessage MakeToken(string token, int expiresIn)
    {
        var payload = "{\"access_token\":\"" + token + "\",\"expires_in\":\"" + expiresIn + "\",\"token_type\":\"Bearer\",\"resource\":\"https://x/\"}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static string? QueryValue(Uri uri, string name)
    {
        var query = uri.Query;
        if (query.Length > 0 && query[0] == '?')
        {
            query = query[1..];
        }

        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var key = separator >= 0 ? segment[..separator] : segment;
            if (!string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
            {
                continue;
            }

            var value = separator >= 0 ? segment[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(value);
        }

        return null;
    }

    private sealed class RecordingScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();
        private readonly Dictionary<string, string> _lastHeaders = new(StringComparer.OrdinalIgnoreCase);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public Uri? LastRequestUri { get; private set; }

        public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);

        public string? LastHeader(string name) => _lastHeaders.TryGetValue(name, out var value) ? value : null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            LastRequestUri = request.RequestUri;
            _lastHeaders.Clear();
            foreach (var header in request.Headers)
            {
                foreach (var value in header.Value)
                {
                    _lastHeaders[header.Key] = value;
                    break;
                }
            }

            return Task.FromResult(_queue.Dequeue());
        }
    }
}
