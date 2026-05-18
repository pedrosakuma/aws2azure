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

    public AzureHttpClient(AzureHttpClientOptions? options = null)
        : this(BuildDefaultHandler(), ownsHandler: true, options)
    {
    }

    public AzureHttpClient(HttpMessageHandler handler, bool ownsHandler, AzureHttpClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        _ownsHandler = ownsHandler;
        _options = options ?? new AzureHttpClientOptions();
        _client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = _options.RequestTimeout
        };
    }

    public HttpClient Inner => _client;

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var attempt = 0;
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        while (true)
        {
            attempt++;
            HttpResponseMessage? response = null;
            try
            {
                response = await _client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
                if (!ShouldRetry(response.StatusCode) || attempt >= maxAttempts)
                {
                    return response;
                }
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
            }
            catch (TaskCanceledException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
            }

            var delay = ComputeDelay(attempt, response);
            response?.Dispose();
            request = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (code == 408 || code == 429)
        {
            return true;
        }
        return code >= 500 && code <= 599;
    }

    private TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
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

        var backoffMs = (int)Math.Min(
            _options.MaxRetryDelay.TotalMilliseconds,
            _options.BaseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(backoffMs);
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
            AutomaticDecompression = DecompressionMethods.All,
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
}
