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

    [Fact]
    public void StaleClosedEraSuccessAfterTripDoesNotReopenBreaker()
    {
        // Simulate the race directly against the breaker: two requests
        // admitted while closed; then enough failures to trip; then the
        // first admission reports success (it was in-flight from before).
        // The success must not re-close the breaker.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(30)
        }, clock);

        var key = "https://example.test:443";
        // Request A admitted while closed.
        Assert.Equal(CircuitBreakerDecision.Allow, breaker.OnBeforeRequest(key, out var admA));
        // Request B admitted, fails, trips threshold-but-not-yet.
        Assert.Equal(CircuitBreakerDecision.Allow, breaker.OnBeforeRequest(key, out var admB));
        breaker.OnFailure(key, admB);
        // Request C admitted (still closed), fails -> trips.
        Assert.Equal(CircuitBreakerDecision.Allow, breaker.OnBeforeRequest(key, out var admC));
        breaker.OnFailure(key, admC);
        Assert.Equal(CircuitBreakerStateName.Open, breaker.Inspect(key).State);

        // Now request A (admitted before the trip) reports success — STALE.
        breaker.OnSuccess(key, admA);
        // Breaker must still be open.
        Assert.Equal(CircuitBreakerStateName.Open, breaker.Inspect(key).State);
        // And new requests must still fail fast.
        Assert.Equal(CircuitBreakerDecision.RejectOpen, breaker.OnBeforeRequest(key, out _));
    }

    [Fact]
    public void StaleClosedEraFailureAfterTripDoesNotExtendOpenWindow()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(30)
        }, clock);

        var key = "https://example.test:443";
        breaker.OnBeforeRequest(key, out var admA);
        breaker.OnBeforeRequest(key, out var admB);
        breaker.OnBeforeRequest(key, out var admC);
        breaker.OnFailure(key, admB);
        breaker.OnFailure(key, admC); // trips at t=0; openUntil=t+30s
        var openUntil1 = breaker.Inspect(key).OpenUntil!.Value;

        // 25s later, the stale request A reports failure.
        clock.Advance(TimeSpan.FromSeconds(25));
        breaker.OnFailure(key, admA);
        var openUntil2 = breaker.Inspect(key).OpenUntil!.Value;

        // Open-until window must NOT be extended by the stale outcome.
        Assert.Equal(openUntil1, openUntil2);
    }

    [Fact]
    public void AbandonedProbeReopensBreakerInsteadOfLatching()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var breaker = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1,
            OpenDuration = TimeSpan.FromSeconds(10)
        }, clock);

        var key = "https://example.test:443";
        // Trip.
        breaker.OnBeforeRequest(key, out var adm1);
        breaker.OnFailure(key, adm1);
        Assert.Equal(CircuitBreakerStateName.Open, breaker.Inspect(key).State);

        // Cool-down elapses -> probe admitted.
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(CircuitBreakerDecision.AllowProbe, breaker.OnBeforeRequest(key, out var probe));

        // Probe is abandoned (caller cancellation, etc.). Without a fix
        // the state would stay HalfOpen forever and reject every future
        // request.
        breaker.OnAbandon(key, probe);

        // Breaker is reopened with a fresh cooldown.
        Assert.Equal(CircuitBreakerStateName.Open, breaker.Inspect(key).State);

        // After the new cooldown, another probe is admissible.
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(CircuitBreakerDecision.AllowProbe, breaker.OnBeforeRequest(key, out _));
    }

    [Fact]
    public async Task SendAsyncCallsOnAbandonWhenCallerCancellationLeavesNoOutcome()
    {
        // Simulates the high-severity finding via an explicit cancellation
        // before the request even completes a network attempt: the
        // try/finally in SendAsync must release the admission so the
        // half-open slot isn't latched.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var responses = new Queue<Func<HttpResponseMessage>>();
        for (int i = 0; i < 3; i++)
            responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var handler = new CancellingHandler(responses);
        using var client = new AzureHttpClient(handler, ownsHandler: true,
            FastOpts(threshold: 3, maxAttempts: 1, openDuration: TimeSpan.FromSeconds(10)),
            clock);

        // Trip.
        for (int i = 0; i < 3; i++)
            (await client.SendAsync(Req("https://example.test/x"))).Dispose();

        // Past cooldown: next call is the probe.
        clock.Advance(TimeSpan.FromSeconds(11));
        // Caller cancels immediately so the probe never reports an outcome.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAsync(Req("https://example.test/x"), HttpCompletionOption.ResponseHeadersRead, cts.Token));

        // Without OnAbandon this would stay HalfOpen and reject every
        // future request. With OnAbandon the breaker reopened with a
        // fresh cooldown — advance the clock and a new probe is admitted.
        clock.Advance(TimeSpan.FromSeconds(11));
        var r = await client.SendAsync(Req("https://example.test/x"));
        Assert.NotNull(r);
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _queue;
        public CancellingHandler(Queue<Func<HttpResponseMessage>> queue) { _queue = queue; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_queue.Count > 0) return Task.FromResult(_queue.Dequeue()());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
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
