using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public class AzureHttpClientTests
{
    [Fact]
    public async Task SendAsync_RetriesOn503ThenSucceeds()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK));
        var client = new AzureHttpClient(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2)
        });

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_StopsAtMaxAttempts()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new AzureHttpClient(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 2,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2)
        });

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_Retries429_HonoringRetryAfterDelta()
    {
        var first = new HttpResponseMessage((HttpStatusCode)429);
        first.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(5));
        var handler = new SequenceHandler(first, new HttpResponseMessage(HttpStatusCode.OK));
        var client = new AzureHttpClient(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(1)
        });

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_Retries408()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.RequestTimeout),
            new HttpResponseMessage(HttpStatusCode.OK));
        var client = new AzureHttpClient(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2)
        });

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task SendAsync_DoesNotRetry4xxOtherThan408And429(HttpStatusCode status)
    {
        var handler = new SequenceHandler(new HttpResponseMessage(status));
        var client = new AzureHttpClient(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 5,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2)
        });

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, AzureHttpClient.RetryCategory.None)]
    [InlineData(HttpStatusCode.NotFound, AzureHttpClient.RetryCategory.None)]
    [InlineData(HttpStatusCode.RequestTimeout, AzureHttpClient.RetryCategory.RequestTimeout)]
    [InlineData((HttpStatusCode)429, AzureHttpClient.RetryCategory.Throttled)]
    [InlineData(HttpStatusCode.InternalServerError, AzureHttpClient.RetryCategory.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, AzureHttpClient.RetryCategory.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AzureHttpClient.RetryCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, AzureHttpClient.RetryCategory.ServerError)]
    internal void ClassifyStatus_AssignsExpectedCategory(HttpStatusCode status, AzureHttpClient.RetryCategory expected)
    {
        Assert.Equal(expected, AzureHttpClient.ClassifyStatus(status));
    }

    [Fact]
    public async Task SendAsync_LargeAttemptNumber_DoesNotOverflowDelay()
    {
        // Regression: with high attempt counts the exponential `Math.Pow(2, n-1)`
        // can produce a non-finite double that overflows TimeSpan.FromMilliseconds.
        // Compute should clamp to MaxRetryDelay even when the math overflows.
        var first = new HttpResponseMessage((HttpStatusCode)429); // Throttled path
        var handler = new SequenceHandler(first, new HttpResponseMessage(HttpStatusCode.OK));
        var client = new AzureHttpClient(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 2000,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2)
        });

        // Manually drive several attempts to force a high exponent before delivering OK.
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public int CallCount { get; private set; }
        public SequenceHandler(params HttpResponseMessage[] responses) => _responses = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

public class EntraIdTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsync_CachesTokenUntilSafetyWindow()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
        var t1 = await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");
        Assert.Equal("token-1", t1);

        // Within safety window (5 min before expiry): cached.
        fakeClock.Advance(TimeSpan.FromMinutes(30));
        var t2 = await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");
        Assert.Equal("token-1", t2);
        Assert.Equal(1, handler.CallCount);

        // Past safety window: refresh.
        handler.Enqueue(MakeToken("token-2", expiresIn: 3600));
        fakeClock.Advance(TimeSpan.FromMinutes(30));
        var t3 = await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");
        Assert.Equal("token-2", t3);
        Assert.Equal(2, handler.CallCount);
    }

    private static HttpResponseMessage MakeToken(string token, int expiresIn)
    {
        var payload = "{\"access_token\":\"" + token + "\",\"token_type\":\"Bearer\",\"expires_in\":" + expiresIn + "}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();
        public int CallCount { get; private set; }
        public void Enqueue(HttpResponseMessage r) => _queue.Enqueue(r);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_queue.Dequeue());
        }
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset now) { _now = now; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
