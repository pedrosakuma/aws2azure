using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Reusable token-cache primitive shared by every Entra ID token flow. It caches
/// tokens per cache key and refreshes them when inside a safety window before
/// expiry. Inside that window — but while the cached token still has comfortable
/// life left — refresh happens off the caller's critical path
/// (stale-while-revalidate): the still-valid token is returned immediately and a
/// single-flight refresh runs in the background.
///
/// <para>Subclasses funnel every request through <see cref="GetOrRefreshAsync{TState}"/>,
/// supplying only the cache key and the identity-specific token fetch (via a state
/// value + a static delegate, so the hot cache-hit path stays allocation-free). The
/// concurrency state machine — single-flight coalescing, the stale-serve band, the
/// last-resort fallback that re-reads the clock, and unobserved-fault swallowing —
/// lives here once.</para>
/// </summary>
public abstract class CachedTokenSource
{
    // Below this remaining lifetime a (single-flight) refresh is started. While the
    // token still has more than the blocking floor left, that refresh runs in the
    // background and the cached token is served immediately.
    private static readonly TimeSpan SafetyWindow = TimeSpan.FromMinutes(5);

    // Hard floor: once a token has less than this remaining we stop serving it stale
    // and block on a fresh fetch instead. A token this close to expiry may not
    // outlive the downstream Azure call (plus clock skew), so the small latency cost
    // buys correctness. Must stay below SafetyWindow so a stale-serve band exists.
    private static readonly TimeSpan MinimumServableLifetime = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string>> _inflight = new(StringComparer.Ordinal);

    protected CachedTokenSource(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns a valid token for <paramref name="cacheKey"/>, serving the cached one
    /// when it is fresh, serving it while a background single-flight refresh runs when
    /// it is inside the safety window but above the blocking floor, and otherwise
    /// awaiting a fresh fetch. <paramref name="requestToken"/> is invoked (with
    /// <paramref name="state"/>) only when a fetch is actually needed; pass a static
    /// delegate plus a value-type state so the cache-hit path allocates nothing.
    /// </summary>
    protected async ValueTask<string> GetOrRefreshAsync<TState>(
        string cacheKey,
        TState state,
        Func<TState, CancellationToken, ValueTask<AccessToken>> requestToken,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        CacheEntry? cached = null;
        Task<string> refresh;
        string? staleToken = null;
        var createdRefresh = false;
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                cached = entry;
                var remaining = entry.ExpiresAt - now;
                if (remaining > SafetyWindow)
                {
                    // Outside the safety window: the cached token is fresh, no refresh.
                    return entry.Token;
                }

                if (remaining > MinimumServableLifetime)
                {
                    // Stale-while-revalidate: inside the safety window but still well
                    // above the blocking floor. Arm a single-flight refresh below and
                    // serve the current token now, keeping the fetch off this request's
                    // critical path. Only callers past the floor (or with no usable
                    // cached token) pay the refresh latency.
                    staleToken = entry.Token;
                }
            }

            // Single-flight: coalesce concurrent refreshes for the same key onto one
            // token-endpoint request. The cache check and the in-flight join happen
            // under the same lock the leader uses to publish a fresh entry, so a
            // caller that observed an in-window entry cannot also start a duplicate
            // refresh once another leader has already repopulated the cache.
            if (!_inflight.TryGetValue(cacheKey, out refresh!))
            {
                refresh = RefreshAndCacheAsync(cacheKey, state, requestToken);
                _inflight[cacheKey] = refresh;
                createdRefresh = true;
            }
        }

        if (createdRefresh)
        {
            // Observe the refresh's outcome exactly once, at creation, so a background
            // (stale-while-revalidate) refresh that faults with no awaiter never
            // surfaces as an unobserved task exception. Harmless for an awaited
            // refresh — the awaiter observes it too. Correctness never depends on this:
            // a failed revalidation leaves the still-valid cached token in place and
            // the next caller retries (or, once the token crosses the blocking floor,
            // awaits a fresh fetch and surfaces any error wire-faithfully).
            ObserveExceptions(refresh);
        }

        if (staleToken is not null)
        {
            return staleToken;
        }

        try
        {
            // Await the shared refresh under this caller's own cancellation token so an
            // individual caller giving up never cancels the fetch other callers (or the
            // cache) still depend on.
            return await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (EntraIdTokenException) when (cached is { } valid && valid.ExpiresAt > _clock.GetUtcNow())
        {
            // A proactive refresh inside the safety window hit a throttle/transient/
            // auth failure, but the previously cached token has not actually expired
            // (re-checked against the current clock, not the pre-await snapshot, so a
            // refresh that outlives the token's remaining validity still surfaces the
            // error). Serve it rather than failing the client's request: a real AWS
            // service would not surface a throttle that originated in our internal
            // token refresh while a usable credential is still in hand. The
            // token-endpoint error is only surfaced once no unexpired token remains.
            //
            // This last-resort fallback intentionally serves ANY still-unexpired token,
            // including one below MinimumServableLifetime. The floor only governs the
            // happy-path decision (serve-stale vs. block for a fresh token) when a
            // refresh CAN still succeed; here the refresh has already failed, so the
            // choice is between serving a near-expiry credential or leaking an internal
            // token-refresh error to the AWS client — wire-faithfulness favours the
            // former. The floor is not an absolute no-near-expiry guarantee.
            return valid.Token;
        }
    }

    /// <summary>Drops every cached token, forcing the next call to fetch afresh.</summary>
    public void InvalidateAll()
    {
        lock (_lock) { _cache.Clear(); }
    }

    private static void ObserveExceptions(Task task)
    {
        task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<string> RefreshAndCacheAsync<TState>(
        string cacheKey,
        TState state,
        Func<TState, CancellationToken, ValueTask<AccessToken>> requestToken)
    {
        // Yield before touching the network so the refresh body never runs on the
        // caller's stack while the dictionary lock is held (the leader starts this
        // task from inside the lock).
        await Task.Yield();
        try
        {
            var issuedAt = _clock.GetUtcNow();
            var token = await requestToken(state, CancellationToken.None).ConfigureAwait(false);

            lock (_lock)
            {
                _cache[cacheKey] = new CacheEntry(token.Token, issuedAt.AddSeconds(token.ExpiresInSeconds));
            }
            return token.Token;
        }
        finally
        {
            lock (_lock)
            {
                _inflight.Remove(cacheKey);
            }
        }
    }

    private readonly record struct CacheEntry(string Token, DateTimeOffset ExpiresAt);
}

/// <summary>A freshly-fetched token plus its lifetime in seconds, as reported by the token endpoint.</summary>
public readonly record struct AccessToken(string Token, int ExpiresInSeconds);

/// <summary>
/// Thrown by a token source when the Entra ID token endpoint returns a non-success
/// status. Carries the originating HTTP status so consuming modules can render the
/// AWS-service-native error shape (a token 429 becomes the service's retryable
/// throttle, a token 5xx its transient error, an auth / bad-request its access-denied
/// error) instead of a bare HTTP 500.
/// </summary>
public sealed class EntraIdTokenException : Exception
{
    public EntraIdTokenException(HttpStatusCode statusCode, string? responseBody)
        : base($"Entra ID token request failed with HTTP {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>The raw status returned by the Entra ID token endpoint.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The token-endpoint response body. For internal logging only — it must never
    /// be echoed to AWS clients, as it can carry Azure auth diagnostics.
    /// </summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Normalises the token-endpoint status into the downstream backend status a
    /// service error mapper should see, preserving wire-faithfulness: 429 stays a
    /// throttle; 408 / 5xx (incl. the open-breaker synthetic 503) collapse to a
    /// transient 503; every other status (400 / 401 / 403 — invalid_client, expired
    /// secret, tenant mismatch) is a downstream auth failure surfaced as 403 so the
    /// service mapper renders its access-denied shape rather than a misleading
    /// client-side ValidationException / InvalidParameter.
    /// </summary>
    public HttpStatusCode BackendStatus => StatusCode switch
    {
        HttpStatusCode.TooManyRequests => HttpStatusCode.TooManyRequests,
        HttpStatusCode.RequestTimeout => HttpStatusCode.ServiceUnavailable,
        >= HttpStatusCode.InternalServerError => HttpStatusCode.ServiceUnavailable,
        _ => HttpStatusCode.Forbidden,
    };
}
