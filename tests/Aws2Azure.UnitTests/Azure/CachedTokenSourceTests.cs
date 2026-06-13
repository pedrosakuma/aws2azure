using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Xunit;

namespace Aws2Azure.UnitTests.Azure;

/// <summary>
/// Concurrency tests for the cache / single-flight / stale-while-revalidate state
/// machine in <see cref="CachedTokenSource"/>. These exercise the base primitive
/// directly (independent of any concrete IMDS / workload-identity flow): coalescing
/// of concurrent refreshes, serving a cached token while a background refresh runs,
/// the blocking floor, the last-resort fallback when a refresh fails, and the
/// guarantee that an individual caller cancelling never cancels the shared fetch.
/// </summary>
public sealed class CachedTokenSourceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Default IMDS-style lifetime that lands comfortably outside the 5-minute safety window.
    private const int OneHour = 3599;

    [Fact]
    public async Task ConcurrentCallers_OnEmptyCache_CoalesceIntoSingleFetch()
    {
        var clock = new FakeTimeProvider(Origin);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestTokenSource(
            async (_, _) =>
            {
                started.TrySetResult();
                await gate.Task;
                return new AccessToken("tok", OneHour);
            },
            clock);

        var callers = Enumerable.Range(0, 16)
            .Select(_ => source.GetTokenAsync("scope").AsTask())
            .ToArray();

        // The single in-flight fetch has entered but not completed: every caller is
        // parked on the same refresh task, so only one fetch can have started.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.FetchCount);

        gate.SetResult();
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, r => Assert.Equal("tok", r));
        Assert.Equal(1, source.FetchCount);
    }

    [Fact]
    public async Task FreshCachedToken_OutsideSafetyWindow_IsServedWithoutRefresh()
    {
        var clock = new FakeTimeProvider(Origin);
        var source = new TestTokenSource(
            (n, _) => new ValueTask<AccessToken>(new AccessToken("tok" + n, OneHour)),
            clock);

        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        // 30 minutes in, the token still has ~30 minutes left — well outside the
        // 5-minute safety window, so no refresh is triggered.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        Assert.Equal(1, source.FetchCount);
    }

    [Fact]
    public async Task InsideSafetyWindowAboveFloor_ServesCachedTokenAndRefreshesInBackground()
    {
        var clock = new FakeTimeProvider(Origin);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestTokenSource(
            async (n, _) =>
            {
                if (n == 1)
                {
                    return new AccessToken("tok1", OneHour);
                }

                // Background refresh: signal it started, then block so the test can
                // assert the cached token was served WITHOUT waiting on this fetch.
                secondFetchStarted.TrySetResult();
                await gate.Task;
                return new AccessToken("tok2", OneHour);
            },
            clock);

        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        // Move into the safety window but keep ~4 minutes of life left: above the
        // 2-minute blocking floor, so the request must serve the cached token now and
        // refresh off the critical path.
        clock.Advance(TimeSpan.FromSeconds(OneHour) - TimeSpan.FromMinutes(4));

        // If stale-while-revalidate regressed into a blocking refresh, this await would
        // hang on the gated fetch; the timeout turns that into a fast failure.
        var served = await source.GetTokenAsync("scope").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("tok1", served);

        // The background single-flight refresh was nonetheless armed.
        await secondFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, source.FetchCount);

        // Drop past the blocking floor so the next caller cannot serve stale and must
        // instead JOIN the still-in-flight background refresh (single-flight) and await
        // it. Releasing the gate then lets that refresh publish tok2, which this caller
        // observes — proving the background refresh actually wrote the fresh token to
        // the cache rather than merely starting.
        clock.Advance(TimeSpan.FromMinutes(3));
        var joining = source.GetTokenAsync("scope").AsTask();
        gate.SetResult();
        Assert.Equal("tok2", await joining.WaitAsync(TimeSpan.FromSeconds(5)));

        // No third fetch: the second caller coalesced onto the background refresh.
        Assert.Equal(2, source.FetchCount);
    }

    [Fact]
    public async Task BelowBlockingFloor_BlocksAndReturnsFreshToken()
    {
        var clock = new FakeTimeProvider(Origin);
        var source = new TestTokenSource(
            (n, _) => new ValueTask<AccessToken>(new AccessToken("tok" + n, OneHour)),
            clock);

        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        // Only 1 minute of life left — below the 2-minute floor, so the caller must
        // block on a fresh fetch rather than serve the near-expiry token.
        clock.Advance(TimeSpan.FromSeconds(OneHour) - TimeSpan.FromMinutes(1));
        Assert.Equal("tok2", await source.GetTokenAsync("scope"));

        Assert.Equal(2, source.FetchCount);
    }

    [Fact]
    public async Task RefreshFailure_WithUnexpiredCachedToken_ServesStaleToken()
    {
        var clock = new FakeTimeProvider(Origin);
        var source = new TestTokenSource(
            (n, _) => n == 1
                ? new ValueTask<AccessToken>(new AccessToken("tok1", OneHour))
                : throw new EntraIdTokenException(HttpStatusCode.TooManyRequests, "throttled"),
            clock);

        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        // Below the floor → the refresh is awaited, but it fails. The cached token has
        // not actually expired, so the last-resort fallback serves it rather than
        // leaking the token-endpoint throttle to the caller.
        clock.Advance(TimeSpan.FromSeconds(OneHour) - TimeSpan.FromMinutes(1));
        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        Assert.Equal(2, source.FetchCount);
    }

    [Fact]
    public async Task RefreshFailure_TokenExpiringWhileRefreshInFlight_PropagatesException()
    {
        var clock = new FakeTimeProvider(Origin);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestTokenSource(
            async (n, _) =>
            {
                if (n == 1)
                {
                    return new AccessToken("tok1", OneHour);
                }

                started.TrySetResult();
                await gate.Task;
                throw new EntraIdTokenException(HttpStatusCode.ServiceUnavailable, "down");
            },
            clock);

        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        // Below the floor: the caller awaits a fresh fetch. The cached token is still
        // unexpired when the (gated) refresh begins.
        clock.Advance(TimeSpan.FromSeconds(OneHour) - TimeSpan.FromMinutes(1));
        var caller = source.GetTokenAsync("scope").AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the refresh is in flight, the cached token crosses its expiry. The
        // fallback must re-read the clock AFTER the await (not a pre-await snapshot):
        // once the refresh fails there is no unexpired token to serve, so the error
        // must propagate rather than serving a now-expired credential.
        clock.Advance(TimeSpan.FromMinutes(2));
        gate.SetResult();

        var ex = await Assert.ThrowsAsync<EntraIdTokenException>(() => caller);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.BackendStatus);
        Assert.Equal(2, source.FetchCount);
    }

    [Fact]
    public async Task RefreshFailure_WithExpiredCachedToken_PropagatesException()
    {
        var clock = new FakeTimeProvider(Origin);
        var source = new TestTokenSource(
            (n, _) => n == 1
                ? new ValueTask<AccessToken>(new AccessToken("tok1", 60))
                : throw new EntraIdTokenException(HttpStatusCode.ServiceUnavailable, "down"),
            clock);

        Assert.Equal("tok1", await source.GetTokenAsync("scope"));

        // Token is now fully expired; with no usable cached credential the failed
        // refresh is surfaced wire-faithfully.
        clock.Advance(TimeSpan.FromMinutes(2));
        var ex = await Assert.ThrowsAsync<EntraIdTokenException>(
            () => source.GetTokenAsync("scope").AsTask());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.BackendStatus);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelSharedFetch_WhichRunsUncancellable()
    {
        var clock = new FakeTimeProvider(Origin);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchTokenCanBeCanceled = true;
        var source = new TestTokenSource(
            async (_, ct) =>
            {
                fetchTokenCanBeCanceled = ct.CanBeCanceled;
                started.TrySetResult();
                await gate.Task;
                return new AccessToken("tok", OneHour);
            },
            clock);

        using var ctsA = new CancellationTokenSource();
        var callerA = source.GetTokenAsync("scope", ctsA.Token).AsTask();
        var callerB = source.GetTokenAsync("scope").AsTask();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Caller A gives up. This must cancel only A's await, never the shared fetch
        // that caller B (and the cache) still depend on.
        ctsA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerA);

        gate.SetResult();
        Assert.Equal("tok", await callerB.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, source.FetchCount);
        Assert.False(fetchTokenCanBeCanceled);
    }

    [Fact]
    public async Task InvalidateAll_ForcesRefetchOnNextCall()
    {
        var clock = new FakeTimeProvider(Origin);
        var source = new TestTokenSource(
            (n, _) => new ValueTask<AccessToken>(new AccessToken("tok" + n, OneHour)),
            clock);

        Assert.Equal("tok1", await source.GetTokenAsync("scope"));
        source.InvalidateAll();
        Assert.Equal("tok2", await source.GetTokenAsync("scope"));

        Assert.Equal(2, source.FetchCount);
    }

    /// <summary>
    /// Minimal concrete <see cref="CachedTokenSource"/> whose fetch is a test-supplied
    /// delegate. The delegate receives a 1-based invocation index so a test can script
    /// per-call behaviour (distinct tokens, gating, throwing).
    /// </summary>
    private sealed class TestTokenSource : CachedTokenSource
    {
        private readonly Func<int, CancellationToken, ValueTask<AccessToken>> _fetch;
        private int _fetchCount;

        public TestTokenSource(Func<int, CancellationToken, ValueTask<AccessToken>> fetch, TimeProvider clock)
            : base(clock)
        {
            _fetch = fetch;
        }

        public int FetchCount => Volatile.Read(ref _fetchCount);

        public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
            => GetOrRefreshAsync(scope, scope, InvokeFetch, cancellationToken);

        private ValueTask<AccessToken> InvokeFetch(string scope, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _fetchCount);
            return _fetch(index, cancellationToken);
        }
    }
}
