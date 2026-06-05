using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Shared <see cref="HttpClient"/> wrapper configured for talking to Azure REST
/// endpoints from an AOT host. Provides connection pooling sensible for
/// long-lived proxy processes and a minimal manual retry policy (no Polly).
/// </summary>
public sealed class AzureHttpClient : IDisposable
{
    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private readonly HttpClient _client;
    private readonly AzureHttpClientOptions _options;
    private readonly CircuitBreaker? _breaker;

    public AzureHttpClient(AzureHttpClientOptions? options = null)
        : this(BuildDefaultHandler(), ownsHandler: true, options)
    {
    }

    public AzureHttpClient(HttpMessageHandler handler, bool ownsHandler, AzureHttpClientOptions? options = null)
        : this(handler, ownsHandler, options, clock: null)
    {
    }

    internal AzureHttpClient(
        HttpMessageHandler handler,
        bool ownsHandler,
        AzureHttpClientOptions? options,
        TimeProvider? clock)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        _ownsHandler = ownsHandler;
        _options = options ?? new AzureHttpClientOptions();
        _client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = _options.RequestTimeout
        };
        _breaker = _options.CircuitBreaker.Enabled
            ? new CircuitBreaker(_options.CircuitBreaker, clock)
            : null;
    }

    public HttpClient Inner => _client;

    /// <summary>
    /// Opt-out flag for the built-in retry loop. Handlers that wrap a
    /// non-replayable stream (e.g. uploads forwarding <c>HttpRequest.Body</c>)
    /// set this so a 5xx never causes the request body to be buffered or
    /// re-read after consumption.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetryOption = new("aws2azure.no-retry");

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var noRetry = request.Options.TryGetValue(NoRetryOption, out var noRetryFlag) && noRetryFlag;
        var attempt = 0;
        var maxAttempts = noRetry ? 1 : Math.Max(1, _options.MaxAttempts);

        // Per-endpoint circuit breaker: pre-check once per logical request,
        // not once per attempt. Inner retries don't get a second vote.
        // The admission token must be reported back exactly once via
        // OnSuccess/OnFailure on terminal paths and OnAbandon if we leave
        // the method without reporting (e.g. caller cancellation).
        var endpointKey = _breaker is null ? null : CircuitBreaker.TryBuildEndpointKey(request.RequestUri);
        var admission = default(CircuitBreakerAdmission);
        var hasAdmission = false;
        if (_breaker is not null && endpointKey is not null)
        {
            var decision = _breaker.OnBeforeRequest(endpointKey, out admission);
            if (decision == CircuitBreakerDecision.RejectOpen)
            {
                var snapshot = _breaker.Inspect(endpointKey);
                throw new CircuitBreakerOpenException(endpointKey, snapshot.OpenUntil);
            }
            hasAdmission = true;
        }

        var outcomeReported = false;
        try
        {
            while (true)
            {
                attempt++;
                HttpResponseMessage? response = null;
                RetryCategory category;
                try
                {
                    response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
                    category = ClassifyStatus(response.StatusCode);

                    // Throttling (HTTP 429) is passed straight through to the
                    // caller instead of being retried internally. AWS clients
                    // own throttle backoff — DynamoDB surfaces
                    // ProvisionedThroughputExceededException, S3 surfaces
                    // SlowDown, both retried by the SDK's (often adaptive)
                    // retry strategy the application configured — and there is
                    // no AWS wire header to forward Azure's x-ms-retry-after-ms
                    // into. Retrying here would stack a second backoff layer
                    // under the client's, hold the request open for seconds,
                    // and trip the per-endpoint breaker on what is backpressure,
                    // not an outage. The attempt is reported to the breaker as a
                    // reachable endpoint (success) so a throttle never opens —
                    // nor latches open via abandon — the circuit.
                    var throttled = category == RetryCategory.Throttled;
                    if (category == RetryCategory.None || throttled || attempt >= maxAttempts)
                    {
                        RecordOutcome(endpointKey, hasAdmission, admission,
                            throttled ? RetryCategory.None : category);
                        outcomeReported = true;
                        return response;
                    }
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                    category = RetryCategory.Transport;
                }
                catch (HttpRequestException)
                {
                    RecordOutcome(endpointKey, hasAdmission, admission, RetryCategory.Transport);
                    outcomeReported = true;
                    throw;
                }
                catch (TaskCanceledException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    // Internal HttpClient timeout (not the caller's CT).
                    category = RetryCategory.Timeout;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    RecordOutcome(endpointKey, hasAdmission, admission, RetryCategory.Timeout);
                    outcomeReported = true;
                    throw;
                }

                var delay = ComputeDelay(attempt, category, response);
                response?.Dispose();
                request = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Any path that didn't explicitly record an outcome (caller
            // cancellation, exception from CloneRequestAsync, async-state-
            // machine teardown) must release the admission so a half-open
            // probe slot is never latched.
            if (!outcomeReported && hasAdmission && _breaker is not null && endpointKey is not null)
            {
                _breaker.OnAbandon(endpointKey, admission);
            }
        }
    }

    /// <summary>
    /// Reports the final outcome of a logical request to the circuit breaker.
    /// Failure is anything in <see cref="RetryCategory"/> that was deemed
    /// retryable but exhausted retries — i.e. real endpoint-health signals.
    /// 4xx/auth do not surface here because <see cref="ClassifyStatus"/> maps
    /// them to <see cref="RetryCategory.None"/>.
    /// </summary>
    private void RecordOutcome(string? endpointKey, bool hasAdmission, CircuitBreakerAdmission admission, RetryCategory finalCategory)
    {
        if (_breaker is null || endpointKey is null || !hasAdmission) return;
        if (finalCategory == RetryCategory.None)
            _breaker.OnSuccess(endpointKey, admission);
        else
            _breaker.OnFailure(endpointKey, admission);
    }

    /// <summary>
    /// Categorises an HTTP response so the retry loop can pick a
    /// per-category back-off. Kept distinct from <c>HttpStatusCode</c>
    /// so it can be unit-tested directly and so a future circuit
    /// breaker can drive its trip logic from the same enum.
    /// </summary>
    internal enum RetryCategory
    {
        /// <summary>2xx/3xx/4xx (other than 408/429): not retried.</summary>
        None,
        /// <summary>HTTP 408 (Request Timeout): retry with exponential back-off.</summary>
        RequestTimeout,
        /// <summary>HTTP 429 (Too Many Requests): retry honouring Retry-After; longer floor when absent.</summary>
        Throttled,
        /// <summary>HTTP 503 (Service Unavailable): retry honouring Retry-After; exponential when absent.</summary>
        ServiceUnavailable,
        /// <summary>HTTP 5xx other than 503: retry with exponential back-off.</summary>
        ServerError,
        /// <summary>Connection-level failure raised as <see cref="HttpRequestException"/>.</summary>
        Transport,
        /// <summary>Internal client-side timeout (HttpClient.Timeout) raised as <see cref="TaskCanceledException"/>.</summary>
        Timeout,
    }

    internal static RetryCategory ClassifyStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code switch
        {
            408 => RetryCategory.RequestTimeout,
            429 => RetryCategory.Throttled,
            503 => RetryCategory.ServiceUnavailable,
            >= 500 and <= 599 => RetryCategory.ServerError,
            _ => RetryCategory.None,
        };
    }

    private TimeSpan ComputeDelay(int attempt, RetryCategory category, HttpResponseMessage? response)
    {
        // Per-category strategy:
        //   Throttled (429): NOT reached here — 429 is passed straight through
        //     to the caller without an internal retry (see SendAsync), so the
        //     AWS client owns throttle backoff. The branch below is retained
        //     intentionally so re-enabling internal throttle-retry is a
        //     one-line change in SendAsync, not a rewrite.
        //   ServiceUnavailable (503): honour Retry-After when present,
        //     otherwise exponential back-off (the most common Azure 503
        //     pattern — transient capacity issue).
        //   RequestTimeout / ServerError / Transport / Timeout:
        //     exponential back-off from BaseRetryDelay capped at
        //     MaxRetryDelay.
        // Final delay always carries jitter (decorrelated half-window) to
        // avoid synchronised retries from concurrent requests hitting the
        // same Azure endpoint.
        if (response is { Headers.RetryAfter: { } retryAfter })
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return Min(delta, _options.MaxRetryDelay);
            }
            if (retryAfter.Date is { } date)
            {
                var now = DateTimeOffset.UtcNow;
                if (date > now)
                {
                    return Min(date - now, _options.MaxRetryDelay);
                }
            }
        }

        TimeSpan baseDelay;
        if (category == RetryCategory.Throttled)
        {
            // Floor: half the max. Server explicitly told us to slow down.
            // Clamp the exponential as a double before constructing a
            // TimeSpan — for large attempt counts `Math.Pow(2, n)` can
            // overflow into infinity and `TimeSpan.FromMilliseconds`
            // throws on non-finite/over-range values.
            var maxMs = _options.MaxRetryDelay.TotalMilliseconds;
            var expMs = _options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            var clampedMs = double.IsFinite(expMs) ? Math.Min(maxMs, expMs) : maxMs;
            var halfMaxMs = maxMs / 2.0;
            baseDelay = TimeSpan.FromMilliseconds(Math.Max(halfMaxMs, clampedMs));
        }
        else
        {
            var expMs = _options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            var clampedMs = double.IsFinite(expMs) ? Math.Min(_options.MaxRetryDelay.TotalMilliseconds, expMs) : _options.MaxRetryDelay.TotalMilliseconds;
            baseDelay = TimeSpan.FromMilliseconds(clampedMs);
        }

        return ApplyJitter(baseDelay);
    }

    private static TimeSpan ApplyJitter(TimeSpan baseDelay)
    {
        // Half-window jitter: pick uniformly from [base/2, base]. Keeps a
        // floor so we never fire instantly while still spreading retries.
        if (baseDelay <= TimeSpan.Zero) return baseDelay;
        var ms = baseDelay.TotalMilliseconds;
        var jittered = (ms / 2.0) + Random.Shared.NextDouble() * (ms / 2.0);
        return TimeSpan.FromMilliseconds(jittered);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var prop in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[prop.Key] = prop.Value;
        }

        if (request.Content is { } content)
        {
            var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var newContent = new ByteArrayContent(bytes);
            foreach (var header in content.Headers)
            {
                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = newContent;
        }

        return clone;
    }

    private static SocketsHttpHandler BuildDefaultHandler() =>
        new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 64,
            // Object bodies must be byte-faithful end-to-end; Content-Encoding
            // is metadata that we forward unchanged to the client. Auto-decompression
            // would silently rewrite the bytes and break round-trips.
            AutomaticDecompression = DecompressionMethods.None,
            EnableMultipleHttp2Connections = true
        };

    public void Dispose()
    {
        _client.Dispose();
        if (_ownsHandler)
        {
            _handler.Dispose();
        }
    }
}

public sealed class AzureHttpClientOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Per-endpoint circuit-breaker tunables. Defaults are conservative
    /// (5 consecutive failures, 30 s open window). Set <c>Enabled=false</c>
    /// to bypass the breaker entirely — useful for tests that want
    /// deterministic retry behaviour.
    /// </summary>
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
}
