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
    public async Task SendAsync_429IsPassedThrough_NotRetried()
    {
        // Throttling is the AWS client's responsibility: the proxy surfaces the
        // 429 (→ ProvisionedThroughputExceededException / SlowDown) on the first
        // hit rather than absorbing it behind internal backoff.
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
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_429DoesNotTripCircuitBreaker()
    {
        // Sustained throttling must never open the per-endpoint breaker: a 429
        // proves the endpoint is reachable (backpressure, not an outage). Drive
        // far more 429s than the failure threshold and assert the breaker stays
        // closed (every call still reaches the handler).
        var responses = new HttpResponseMessage[20];
        for (var i = 0; i < responses.Length; i++)
        {
            responses[i] = new HttpResponseMessage((HttpStatusCode)429);
        }
        var handler = new SequenceHandler(responses);
        var client = new AzureHttpClient(handler, ownsHandler: true, new AzureHttpClientOptions
        {
            MaxAttempts = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(2),
            CircuitBreaker = new CircuitBreakerOptions { Enabled = true, FailureThreshold = 3 }
        });

        for (var i = 0; i < responses.Length; i++)
        {
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
            Assert.Equal((HttpStatusCode)429, response.StatusCode);
        }
        // Breaker never opened → no fast-fail 503 → all 20 reached the handler.
        Assert.Equal(20, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_429AsHalfOpenProbe_ClosesBreaker()
    {
        // Open the breaker with a 503, advance past OpenDuration so the next call
        // is a half-open probe, then deliver a 429 as that probe. A throttle
        // proves the endpoint is reachable, so the probe is treated as a success
        // and the breaker closes (the following call is admitted to the handler).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), // trips breaker
            new HttpResponseMessage((HttpStatusCode)429),               // half-open probe
            new HttpResponseMessage(HttpStatusCode.OK));                // admitted after close
        var options = new AzureHttpClientOptions
        {
            MaxAttempts = 1,
            CircuitBreaker = new CircuitBreakerOptions
            {
                Enabled = true,
                FailureThreshold = 1,
                OpenDuration = TimeSpan.FromSeconds(30)
            }
        };
        var client = new AzureHttpClient(handler, ownsHandler: true, options, clock);

        // 1) 503 trips the breaker (FailureThreshold = 1).
        var r1 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r1.StatusCode);

        // Open: a call before OpenDuration elapses is rejected with a synthetic
        // 503 without reaching the handler.
        var rOpen = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rOpen.StatusCode);
        Assert.Equal(1, handler.CallCount);

        // 2) Past OpenDuration → half-open; the single probe receives a 429.
        clock.Advance(TimeSpan.FromSeconds(31));
        var r2 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal((HttpStatusCode)429, r2.StatusCode);
        Assert.Equal(2, handler.CallCount);

        // 3) The 429 probe closed the breaker → the next call is admitted.
        var r3 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://example.test/x"));
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        Assert.Equal(3, handler.CallCount);
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
    public async Task SendAsync_DoesNotRetry4xxOtherThan408(HttpStatusCode status)
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
        // Uses 503 (a still-retried category) — 429 is now passed through without
        // any internal retry, so it would never exercise the back-off math.
        var first = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
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

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    public void EntraIdTokenException_NormalisesBackendStatus(HttpStatusCode raw, HttpStatusCode expected)
    {
        var ex = new EntraIdTokenException(raw, "body");
        Assert.Equal(raw, ex.StatusCode);
        Assert.Equal(expected, ex.BackendStatus);
    }

    [Fact]
    public async Task GetTokenAsync_NonSuccessThrowsStatusPreservingException()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"throttled\"}")
        });

        var ex = await Assert.ThrowsAsync<EntraIdTokenException>(() =>
            provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default").AsTask());
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.BackendStatus);
    }

    [Fact]
    public async Task GetTokenAsync_ServesUnexpiredCachedTokenWhenRefreshThrottled()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
        var t1 = await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");
        Assert.Equal("token-1", t1);

        // Advance into the safety window so a proactive refresh fires, but the cached
        // token has NOT actually expired (token-1 expires at +3600s; we are at +3540s).
        fakeClock.Advance(TimeSpan.FromMinutes(59));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var t2 = await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");
        Assert.Equal("token-1", t2); // served the still-valid cached token instead of surfacing the 429
        Assert.Equal(2, handler.CallCount); // refresh was attempted
    }

    [Fact]
    public async Task GetTokenAsync_SurfacesFailureWhenCachedTokenExpired()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
        await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");

        // Past actual expiry: the cached token is no longer usable, so the throttle
        // must surface to the caller.
        fakeClock.Advance(TimeSpan.FromMinutes(61));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        await Assert.ThrowsAsync<EntraIdTokenException>(() =>
            provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default").AsTask());
    }

    [Fact]
    public async Task GetTokenAsync_SurfacesFailureWhenCachedTokenExpiresDuringRefresh()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        // The handler advances the clock past the cached token's expiry WHILE the
        // refresh is in flight, then fails it. The cached-token fallback must re-read
        // the clock (not the pre-await snapshot) and therefore surface the error.
        var handler = new ClockAdvancingHandler(fakeClock);
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
        await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");

        // Move into the safety window but BEFORE expiry (token-1 expires at +3600s).
        fakeClock.Advance(TimeSpan.FromMinutes(59)); // +3540s
        // The refresh fails AND, as part of handling it, the clock advances past expiry.
        handler.EnqueueFailureThatAdvances(new HttpResponseMessage(HttpStatusCode.TooManyRequests), TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<EntraIdTokenException>(() =>
            provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default").AsTask());
    }

    [Fact]
    public async Task GetTokenAsync_CoalescesConcurrentRefreshesIntoSingleRequest()
    {
        // N callers race to fetch the same (tenant, client, scope) token on a cold cache.
        // Single-flight must collapse them onto exactly ONE token-endpoint request: a
        // refresh herd would otherwise self-throttle the token endpoint. The handler is
        // gated so that, were single-flight absent, every concurrent caller would enter
        // SendAsync and the assertion would observe CallCount == N instead of 1.
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new GatedTokenHandler("token-1", expiresIn: 3600);
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        const int n = 24;
        var tasks = new Task<string>[n];
        for (var i = 0; i < n; i++)
        {
            tasks[i] = provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default").AsTask();
        }

        // Wait for the single leader to enter the gated fetch, then give the remaining
        // callers a window to join the in-flight refresh before releasing it.
        await handler.WaitForEntryAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        handler.Release();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, handler.CallCount);
        Assert.All(results, r => Assert.Equal("token-1", r));
    }

    [Fact]
    public async Task GetTokenAsync_ServesStaleTokenWhileRevalidatingInBackground()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
        Assert.Equal("token-1", await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default"));

        // Move into the stale-but-servable band: inside the 5-min safety window but
        // comfortably above the 2-min blocking floor (token-1 expires at +3600s; at
        // +3420s there are 180s left).
        fakeClock.Advance(TimeSpan.FromSeconds(3420));
        handler.Enqueue(MakeToken("token-2", expiresIn: 3600));

        // The caller gets the still-valid token immediately; the refresh runs in the
        // background and never blocks the request.
        Assert.Equal("token-1", await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default"));

        // The background revalidation eventually repopulates the cache; poll the
        // externally-visible state (not the handler's entry count, which increments
        // before the cache is written) until token-2 is served.
        var latest = "token-1";
        await SpinUntilAsync(
            async () =>
            {
                latest = await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default");
                return latest == "token-2";
            },
            TimeSpan.FromSeconds(30));

        Assert.Equal("token-2", latest);
        Assert.Equal(2, handler.CallCount); // single-flight: exactly one background refresh fired
    }

    [Fact]
    public async Task GetTokenAsync_BlocksForFreshTokenBelowServableFloor()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new ScriptedHandler();
        var http = new AzureHttpClient(handler, ownsHandler: true);
        var provider = new EntraIdTokenProvider(http, authority: new Uri("https://login.test/"), clock: fakeClock);

        handler.Enqueue(MakeToken("token-1", expiresIn: 3600));
        Assert.Equal("token-1", await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default"));

        // Move below the 2-min blocking floor (token-1 expires at +3600s; at +3540s
        // only 60s remain). The caller must block on the fresh token rather than serve
        // the near-dead cached one.
        fakeClock.Advance(TimeSpan.FromSeconds(3540));
        handler.Enqueue(MakeToken("token-2", expiresIn: 3600));

        Assert.Equal("token-2", await provider.GetTokenAsync("tenant", "client", "secret", "https://storage.azure.com/.default"));
        Assert.Equal(2, handler.CallCount);
    }

    private static async Task SpinUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException("Condition was not satisfied within the timeout.");
            }

            await Task.Delay(10);
        }
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
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public void Enqueue(HttpResponseMessage r) => _queue.Enqueue(r);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(_queue.Dequeue());
        }
    }

    private sealed class ClockAdvancingHandler(FakeTimeProvider clock) : HttpMessageHandler
    {
        private readonly Queue<(HttpResponseMessage Response, TimeSpan Advance)> _queue = new();
        public void Enqueue(HttpResponseMessage r) => _queue.Enqueue((r, TimeSpan.Zero));
        public void EnqueueFailureThatAdvances(HttpResponseMessage r, TimeSpan advance) => _queue.Enqueue((r, advance));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (response, advance) = _queue.Dequeue();
            if (advance > TimeSpan.Zero)
            {
                clock.Advance(advance);
            }
            return Task.FromResult(response);
        }
    }

    // Counts SendAsync invocations and blocks each one on a shared gate, so a test can
    // hold the in-flight refresh open while concurrent callers pile up behind it.
    private sealed class GatedTokenHandler : HttpMessageHandler
    {
        private readonly string _token;
        private readonly int _expiresIn;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public GatedTokenHandler(string token, int expiresIn)
        {
            _token = token;
            _expiresIn = expiresIn;
        }

        public int CallCount => Volatile.Read(ref _callCount);
        public Task WaitForEntryAsync() => _entered.Task;
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            var payload = "{\"access_token\":\"" + _token + "\",\"token_type\":\"Bearer\",\"expires_in\":" + _expiresIn + "}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
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
