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
    /// Decides whether a request bound for <paramref name="endpointKey"/>
    /// may proceed. Returns the verdict plus an <see cref="CircuitBreakerAdmission"/>
    /// token that callers MUST pass back to <see cref="OnSuccess"/>,
    /// <see cref="OnFailure"/>, or <see cref="OnAbandon"/> exactly once.
    /// The token carries the admission epoch so stale outcomes from
    /// requests admitted before the breaker tripped cannot mutate the
    /// current state.
    /// </summary>
    public CircuitBreakerDecision OnBeforeRequest(string endpointKey, out CircuitBreakerAdmission admission)
    {
        var state = _endpoints.GetOrAdd(endpointKey, static _ => new EndpointState());
        var now = _clock.GetUtcNow();
        return state.OnBeforeRequest(now, _options, out admission);
    }

    /// <summary>
    /// Reports a successful logical request. Closed-era admissions whose
    /// epoch no longer matches are silently ignored (a concurrent failure
    /// already tripped the breaker; we must not undo that). Probe success
    /// snaps the breaker back to closed.
    /// </summary>
    public void OnSuccess(string endpointKey, CircuitBreakerAdmission admission)
    {
        if (_endpoints.TryGetValue(endpointKey, out var state))
            state.OnSuccess(admission);
    }

    /// <summary>
    /// Reports a failed logical request (after retry exhaustion or an
    /// unhandled transport/timeout exception). Stale closed-era outcomes
    /// are ignored. Probe failure restarts the open window from now.
    /// </summary>
    public void OnFailure(string endpointKey, CircuitBreakerAdmission admission)
    {
        var state = _endpoints.GetOrAdd(endpointKey, static _ => new EndpointState());
        var now = _clock.GetUtcNow();
        state.OnFailure(now, _options, admission);
    }

    /// <summary>
    /// Releases an admission whose outcome cannot be determined (e.g.
    /// caller cancellation before completion). For probe admissions this
    /// re-opens the breaker for a fresh cool-down so the half-open slot
    /// is never latched. For closed-era admissions this is a no-op.
    /// </summary>
    public void OnAbandon(string endpointKey, CircuitBreakerAdmission admission)
    {
        if (_endpoints.TryGetValue(endpointKey, out var state))
        {
            var now = _clock.GetUtcNow();
            state.OnAbandon(now, _options, admission);
        }
    }

    /// <summary>
    /// Builds the canonical endpoint key for <paramref name="requestUri"/> —
    /// lower-cased <c>scheme://host:port</c>. Returns <c>null</c> for relative
    /// or schemeless URIs (caller should skip the breaker in that case).
    /// </summary>
    public static string? TryBuildEndpointKey(Uri? requestUri)
    {
        if (requestUri is null || !requestUri.IsAbsoluteUri) return null;
        // Uri.Scheme is normalized to lower-case; Uri.Host is normalized per
        // RFC 3986. Explicit port keeps http://x:443 and https://x:443 distinct.
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
        // Bumped every time the breaker transitions away from the closed
        // run that admitted a request. Used to ignore stale outcomes from
        // in-flight requests that were admitted before a trip / probe.
        private int _epoch;

        public CircuitBreakerDecision OnBeforeRequest(DateTimeOffset now, CircuitBreakerOptions opts, out CircuitBreakerAdmission admission)
        {
            lock (_lock)
            {
                if (_state == CircuitBreakerStateName.Open)
                {
                    if (now >= _openUntil)
                    {
                        // Cool-down elapsed: promote to half-open and admit
                        // exactly one probe. Bump the epoch so any straggler
                        // outcomes from the previous closed run can't mutate
                        // the current state.
                        _state = CircuitBreakerStateName.HalfOpen;
                        _epoch++;
                        admission = new CircuitBreakerAdmission(isProbe: true, epoch: _epoch);
                        return CircuitBreakerDecision.AllowProbe;
                    }
                    admission = default;
                    return CircuitBreakerDecision.RejectOpen;
                }
                if (_state == CircuitBreakerStateName.HalfOpen)
                {
                    // A probe is already in flight; reject everyone else.
                    admission = default;
                    return CircuitBreakerDecision.RejectOpen;
                }
                admission = new CircuitBreakerAdmission(isProbe: false, epoch: _epoch);
                return CircuitBreakerDecision.Allow;
            }
        }

        public void OnSuccess(CircuitBreakerAdmission admission)
        {
            lock (_lock)
            {
                if (admission.IsProbe)
                {
                    if (_state != CircuitBreakerStateName.HalfOpen || admission.Epoch != _epoch)
                        return; // Probe already resolved by abandon / re-admission.
                    // Probe success: snap to closed, reset counters, bump
                    // epoch so any closed-era stragglers from before the
                    // trip cannot poison the freshly-closed state.
                    _state = CircuitBreakerStateName.Closed;
                    _consecutiveFailures = 0;
                    _epoch++;
                    return;
                }
                // Closed-era admission: only act if the breaker is still in
                // the same closed run we were admitted under. If a
                // concurrent failure tripped the breaker meanwhile, this
                // outcome is stale and must be ignored.
                if (_state == CircuitBreakerStateName.Closed && admission.Epoch == _epoch)
                {
                    _consecutiveFailures = 0;
                }
            }
        }

        public void OnFailure(DateTimeOffset now, CircuitBreakerOptions opts, CircuitBreakerAdmission admission)
        {
            lock (_lock)
            {
                if (admission.IsProbe)
                {
                    if (_state != CircuitBreakerStateName.HalfOpen || admission.Epoch != _epoch)
                        return;
                    _state = CircuitBreakerStateName.Open;
                    _openUntil = now + opts.OpenDuration;
                    _epoch++;
                    return;
                }
                if (_state != CircuitBreakerStateName.Closed || admission.Epoch != _epoch)
                    return; // Stale: trip already happened on another thread.
                _consecutiveFailures++;
                if (_consecutiveFailures >= opts.FailureThreshold)
                {
                    _state = CircuitBreakerStateName.Open;
                    _openUntil = now + opts.OpenDuration;
                    _epoch++;
                }
            }
        }

        public void OnAbandon(DateTimeOffset now, CircuitBreakerOptions opts, CircuitBreakerAdmission admission)
        {
            lock (_lock)
            {
                if (admission.IsProbe)
                {
                    // Probe outcome is unknown (caller cancelled, clone
                    // failed, etc.). Don't treat as success — that would
                    // bypass the cooldown protection. Reopen with a fresh
                    // window so the half-open slot isn't latched forever.
                    if (_state != CircuitBreakerStateName.HalfOpen || admission.Epoch != _epoch)
                        return;
                    _state = CircuitBreakerStateName.Open;
                    _openUntil = now + opts.OpenDuration;
                    _epoch++;
                }
                // Closed-era abandon is a no-op: the request didn't
                // signal endpoint health either way.
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

/// <summary>
/// Token returned by <see cref="CircuitBreaker.OnBeforeRequest"/> identifying
/// an admitted request and the breaker generation it was admitted under.
/// Outcomes carrying a stale epoch are ignored, preventing in-flight
/// closed-era requests from mutating the breaker after a trip.
/// </summary>
internal readonly struct CircuitBreakerAdmission
{
    public CircuitBreakerAdmission(bool isProbe, int epoch)
    {
        IsProbe = isProbe;
        Epoch = epoch;
    }
    public bool IsProbe { get; }
    public int Epoch { get; }
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
