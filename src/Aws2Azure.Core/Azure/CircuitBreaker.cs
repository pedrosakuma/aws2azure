using System.Collections.Concurrent;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Per-endpoint circuit breaker layered in front of <see cref="AzureHttpClient"/>'s
/// retry loop. Trips after <see cref="CircuitBreakerOptions.FailureThreshold"/>
/// consecutive logical-request failures (counted post-retry, so one tripped
/// breaker corresponds to ≥ <c>threshold * MaxAttempts</c> network attempts)
/// and stays open for <see cref="CircuitBreakerOptions.OpenDuration"/>; the
/// next request after that becomes a half-open probe whose result snaps the
/// breaker back to closed (on success) or open again (on failure).
///
/// <para>Designed for Azure REST endpoints where 503 storms and 429 surges
/// are real failure modes. Auth-class failures (401/403) and pure 4xx are
/// <em>not</em> recorded as failures because they reflect request-level
/// problems, not endpoint health.</para>
///
/// <para>Endpoint identity is the lower-cased <c>scheme://host:port</c> tuple
/// taken from the request URI. Path/query/headers are deliberately ignored
/// — Azure throttling is enforced at the namespace/account level, not per
/// resource.</para>
/// </summary>
internal sealed class CircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, EndpointState> _endpoints =
        new(StringComparer.Ordinal);

    public CircuitBreaker(CircuitBreakerOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns the breaker's current decision for a request bound for
    /// <paramref name="endpointKey"/>. Callers must check this before
    /// dispatching the request and bail with
    /// <see cref="CircuitBreakerOpenException"/> when the result is
    /// <see cref="CircuitBreakerDecision.RejectOpen"/>.
    /// </summary>
    public CircuitBreakerDecision OnBeforeRequest(string endpointKey)
    {
        var state = _endpoints.GetOrAdd(endpointKey, static _ => new EndpointState());
        var now = _clock.GetUtcNow();
        return state.OnBeforeRequest(now, _options);
    }

    /// <summary>Records a successful logical request — resets failure counters and closes the breaker.</summary>
    public void OnSuccess(string endpointKey)
    {
        if (_endpoints.TryGetValue(endpointKey, out var state))
            state.OnSuccess();
    }

    /// <summary>
    /// Records a failed logical request (after retry exhaustion or an
    /// unhandled exception). The breaker trips when consecutive failures
    /// reach <see cref="CircuitBreakerOptions.FailureThreshold"/>.
    /// Callers should NOT invoke this for 4xx responses (bad request,
    /// not-found, etc.) or auth failures — those are request-level, not
    /// endpoint-health, signals.
    /// </summary>
    public void OnFailure(string endpointKey)
    {
        var state = _endpoints.GetOrAdd(endpointKey, static _ => new EndpointState());
        var now = _clock.GetUtcNow();
        state.OnFailure(now, _options);
    }

    /// <summary>
    /// Builds the canonical endpoint key for <paramref name="requestUri"/> —
    /// lower-cased <c>scheme://host:port</c>. Returns <c>null</c> for relative
    /// or schemeless URIs (caller should skip the breaker in that case).
    /// </summary>
    public static string? TryBuildEndpointKey(Uri? requestUri)
    {
        if (requestUri is null || !requestUri.IsAbsoluteUri) return null;
        // Use Host (already lowercased per RFC 3986 normalization) + explicit
        // port so http://x and https://x are distinct breakers.
        return $"{requestUri.Scheme}://{requestUri.Host}:{requestUri.Port}";
    }

    internal CircuitBreakerStateSnapshot Inspect(string endpointKey)
    {
        if (!_endpoints.TryGetValue(endpointKey, out var state))
            return new CircuitBreakerStateSnapshot(CircuitBreakerStateName.Closed, 0, null);
        return state.Inspect();
    }

    private sealed class EndpointState
    {
        private readonly object _lock = new();
        // 0=Closed 1=Open 2=HalfOpen
        private CircuitBreakerStateName _state;
        private int _consecutiveFailures;
        private DateTimeOffset _openUntil;

        public CircuitBreakerDecision OnBeforeRequest(DateTimeOffset now, CircuitBreakerOptions opts)
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerStateName.Open)
                {
                    if (now >= _openUntil)
                    {
                        // Cool-down elapsed: promote to half-open and let
                        // exactly one probe through.
                        _state = CircuitBreakerStateName.HalfOpen;
                        return CircuitBreakerDecision.AllowProbe;
                    }
                    return CircuitBreakerDecision.RejectOpen;
                }
                if (_state == CircuitBreakerStateName.HalfOpen)
                {
                    // A probe is already in flight; reject everyone else.
                    return CircuitBreakerDecision.RejectOpen;
                }
                return CircuitBreakerDecision.Allow;
            }
        }

        public void OnSuccess()
        {
            lock (_lock)
            {
                _consecutiveFailures = 0;
                _state = CircuitBreakerStateName.Closed;
            }
        }

        public void OnFailure(DateTimeOffset now, CircuitBreakerOptions opts)
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerStateName.HalfOpen)
                {
                    // Probe failed: reopen the breaker for another cool-down.
                    _state = CircuitBreakerStateName.Open;
                    _openUntil = now + opts.OpenDuration;
                    return;
                }
                _consecutiveFailures++;
                if (_consecutiveFailures >= opts.FailureThreshold)
                {
                    _state = CircuitBreakerStateName.Open;
                    _openUntil = now + opts.OpenDuration;
                }
            }
        }

        public CircuitBreakerStateSnapshot Inspect()
        {
            lock (_lock)
            {
                return new CircuitBreakerStateSnapshot(
                    _state,
                    _consecutiveFailures,
                    _state == CircuitBreakerStateName.Open ? _openUntil : null);
            }
        }
    }
}

/// <summary>Tunables for <see cref="CircuitBreaker"/>.</summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Whether the breaker is enabled. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Consecutive failures (post-retry) needed to trip the breaker.
    /// Defaults to 5 — at MaxAttempts=3 that's ≥ 15 actual network calls
    /// before we declare the endpoint sick.
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>How long the breaker stays open before promoting to half-open. Defaults to 30 s.</summary>
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>The breaker's verdict for a single request attempt.</summary>
internal enum CircuitBreakerDecision
{
    /// <summary>Closed — request flows normally.</summary>
    Allow,
    /// <summary>Half-open — this request is the trial probe.</summary>
    AllowProbe,
    /// <summary>Open — fail-fast with <see cref="CircuitBreakerOpenException"/>.</summary>
    RejectOpen,
}

internal enum CircuitBreakerStateName
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
}

internal readonly record struct CircuitBreakerStateSnapshot(
    CircuitBreakerStateName State,
    int ConsecutiveFailures,
    DateTimeOffset? OpenUntil);

/// <summary>
/// Thrown by <see cref="AzureHttpClient.SendAsync"/> when the per-endpoint
/// circuit breaker is open. Treat as a transient signal — retry layers
/// above should back off and try a different endpoint or wait for the
/// breaker's cool-down to elapse.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string endpointKey, DateTimeOffset? openUntil)
        : base($"Circuit breaker open for endpoint '{endpointKey}'"
            + (openUntil is { } until ? $" until {until:O}." : "."))
    {
        EndpointKey = endpointKey;
        OpenUntil = openUntil;
    }

    public string EndpointKey { get; }
    public DateTimeOffset? OpenUntil { get; }
}
