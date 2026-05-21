using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

public class CircuitBreakerTests
{
    private static AzureHttpClientOptions FastOpts(int threshold = 3, int maxAttempts = 1, TimeSpan? openDuration = null) =>
        new()
        {
            MaxAttempts = maxAttempts,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2),
            CircuitBreaker = new CircuitBreakerOptions
            {
                Enabled = true,
                FailureThreshold = threshold,
                OpenDuration = openDuration ?? TimeSpan.FromSeconds(30),
            }
        };

    [Fact]
    public async Task TripsAfterThresholdConsecutiveServiceUnavailableFailures()
    {
        var handler = new InfiniteHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = NewClient(handler, FastOpts(threshold: 3, maxAttempts: 1));

        // Three logical 503 responses → trip.
        for (int i = 0; i < 3; i++)
        {
            var r = await client.SendAsync(Req("https://example.test/x"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        }
        Assert.Equal(3, handler.CallCount);

        // Next one fails fast — no extra HTTP call.
        var ex = await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => client.SendAsync(Req("https://example.test/x")));
        Assert.Contains("example.test", ex.EndpointKey);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task SuccessResetsConsecutiveFailureCounter()
    {
        var responses = new Queue<Func<HttpResponseMessage>>();
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var handler = new ScriptedHandler(responses);
        using var client = NewClient(handler, FastOpts(threshold: 3, maxAttempts: 1));

        // Two failures, then success (counter resets), then two more failures = still closed.
        for (int i = 0; i < 5; i++)
            (await client.SendAsync(Req("https://example.test/x"))).Dispose();

        Assert.Equal(5, handler.CallCount); // No fast-fail.
    }

    [Fact]
    public async Task ProbeAfterOpenDurationClosesBreakerOnSuccess()
    {
        var responses = new Queue<Func<HttpResponseMessage>>();
        for (int i = 0; i < 3; i++)
            responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));        // probe success
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK));        // post-close
        var handler = new ScriptedHandler(responses);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var client = NewClient(handler, FastOpts(threshold: 3, maxAttempts: 1, openDuration: TimeSpan.FromSeconds(10)), clock);

        for (int i = 0; i < 3; i++)
            (await client.SendAsync(Req("https://example.test/x"))).Dispose();

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => client.SendAsync(Req("https://example.test/x")));

        // Advance past open duration → next call is the probe.
        clock.Advance(TimeSpan.FromSeconds(11));
        var probe = await client.SendAsync(Req("https://example.test/x"));
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);

        // Breaker should now be closed; another call goes through.
        var next = await client.SendAsync(Req("https://example.test/x"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(5, handler.CallCount);
    }

    [Fact]
    public async Task ProbeFailureReopensBreaker()
    {
        var responses = new Queue<Func<HttpResponseMessage>>();
        for (int i = 0; i < 3; i++)
            responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); // probe failure
        var handler = new ScriptedHandler(responses);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var client = NewClient(handler, FastOpts(threshold: 3, maxAttempts: 1, openDuration: TimeSpan.FromSeconds(10)), clock);

        for (int i = 0; i < 3; i++)
            (await client.SendAsync(Req("https://example.test/x"))).Dispose();

        clock.Advance(TimeSpan.FromSeconds(11));
        // Probe call returns 503 → reopens breaker.
        var probe = await client.SendAsync(Req("https://example.test/x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, probe.StatusCode);

        // Without advancing, next request should fail fast again.
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => client.SendAsync(Req("https://example.test/x")));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task FourxxResponsesDoNotCountAsFailures()
    {
        var handler = new InfiniteHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = NewClient(handler, FastOpts(threshold: 3, maxAttempts: 1));

        for (int i = 0; i < 10; i++)
            (await client.SendAsync(Req("https://example.test/x"))).Dispose();

        Assert.Equal(10, handler.CallCount); // No fast-fail.
    }

    [Fact]
    public async Task SeparateEndpointsHaveSeparateBreakers()
    {
        var handler = new InfiniteHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = NewClient(handler, FastOpts(threshold: 3, maxAttempts: 1));

        for (int i = 0; i < 3; i++)
            (await client.SendAsync(Req("https://a.example.test/x"))).Dispose();
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => client.SendAsync(Req("https://a.example.test/x")));

        // b.example.test is a different endpoint — its breaker is still closed.
        var r = await client.SendAsync(Req("https://b.example.test/x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task ExhaustedRetriesAreOneFailureNotN()
    {
        // MaxAttempts=3 with 503 every time → 3 network calls, 1 logical failure.
        var handler = new InfiniteHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = NewClient(handler, FastOpts(threshold: 3, maxAttempts: 3));

        // Need 3 logical failures (= 9 network calls) to trip.
        for (int i = 0; i < 3; i++)
            (await client.SendAsync(Req("https://example.test/x"))).Dispose();
        Assert.Equal(9, handler.CallCount);

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => client.SendAsync(Req("https://example.test/x")));
        Assert.Equal(9, handler.CallCount);
    }

    [Fact]
    public async Task TransportExceptionsCountAsFailures()
    {
        var handler = new InfiniteHandler(() => throw new HttpRequestException("boom"));
        using var client = NewClient(handler, FastOpts(threshold: 2, maxAttempts: 1));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(Req("https://example.test/x")));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(Req("https://example.test/x")));
        // Tripped now.
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => client.SendAsync(Req("https://example.test/x")));
    }

    [Fact]
    public async Task DisabledBreakerNeverTrips()
    {
        var handler = new InfiniteHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var opts = FastOpts(threshold: 1, maxAttempts: 1);
        opts.CircuitBreaker.Enabled = false;
        using var client = NewClient(handler, opts);

        for (int i = 0; i < 50; i++)
            (await client.SendAsync(Req("https://example.test/x"))).Dispose();
        Assert.Equal(50, handler.CallCount);
    }

    [Fact]
    public void TryBuildEndpointKeyIncludesPort()
    {
        var http = CircuitBreaker.TryBuildEndpointKey(new Uri("http://example.test:80/x"));
        var https = CircuitBreaker.TryBuildEndpointKey(new Uri("https://example.test:443/x"));
        Assert.NotEqual(http, https);
        Assert.Null(CircuitBreaker.TryBuildEndpointKey(null));
    }

    private static HttpRequestMessage Req(string url) => new(HttpMethod.Get, url);

    private static AzureHttpClient NewClient(HttpMessageHandler handler, AzureHttpClientOptions opts, TimeProvider? clock = null)
        => new(handler, ownsHandler: true, opts, clock);

    private sealed class InfiniteHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public int CallCount { get; private set; }
        public InfiniteHandler(Func<HttpResponseMessage> factory) { _factory = factory; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_factory());
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _queue;
        public int CallCount { get; private set; }
        public ScriptedHandler(Queue<Func<HttpResponseMessage>> queue) { _queue = queue; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_queue.Dequeue()());
        }
    }
}
