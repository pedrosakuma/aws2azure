using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Acquires OAuth 2.0 bearer tokens from Entra ID using the client-credentials
/// flow, talking directly to the v2.0 token endpoint (no Azure.Identity).
/// Tokens are cached per (tenant, clientId, scope) tuple and refreshed when
/// inside a safety window before expiry.
/// </summary>
public sealed class EntraIdTokenProvider
{
    private static readonly TimeSpan SafetyWindow = TimeSpan.FromMinutes(5);

    private readonly AzureHttpClient _http;
    private readonly TimeProvider _clock;
    private readonly Uri _authority;
    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string>> _inflight = new(StringComparer.Ordinal);

    public EntraIdTokenProvider(AzureHttpClient http, Uri? authority = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _authority = authority ?? new Uri("https://login.microsoftonline.com/");
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<string> GetTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var cacheKey = tenantId + "|" + clientId + "|" + scope;
        var now = _clock.GetUtcNow();

        CacheEntry? cached = null;
        Task<string> refresh;
        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                cached = entry;
                if (entry.ExpiresAt - SafetyWindow > now)
                {
                    return entry.Token;
                }
            }

            // Single-flight: coalesce concurrent refreshes for the same key onto one
            // token-endpoint request. The cache check and the in-flight join happen
            // under the same lock the leader uses to publish a fresh entry, so a
            // caller that observed an in-window entry cannot also start a duplicate
            // refresh once another leader has already repopulated the cache.
            if (!_inflight.TryGetValue(cacheKey, out refresh!))
            {
                refresh = RefreshAndCacheAsync(cacheKey, tenantId, clientId, clientSecret, scope);
                _inflight[cacheKey] = refresh;
            }
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
            return valid.Token;
        }
    }

    private async Task<string> RefreshAndCacheAsync(
        string cacheKey,
        string tenantId,
        string clientId,
        string clientSecret,
        string scope)
    {
        // Yield before touching the network so the refresh body never runs on the
        // caller's stack while the dictionary lock is held (the leader starts this
        // task from inside the lock).
        await Task.Yield();
        try
        {
            var issuedAt = _clock.GetUtcNow();
            var token = await RequestTokenAsync(tenantId, clientId, clientSecret, scope, CancellationToken.None).ConfigureAwait(false);

            lock (_lock)
            {
                _cache[cacheKey] = new CacheEntry(token.AccessToken, issuedAt.AddSeconds(token.ExpiresInSeconds));
            }
            return token.AccessToken;
        }
        finally
        {
            lock (_lock)
            {
                _inflight.Remove(cacheKey);
            }
        }
    }

    private async Task<EntraIdTokenResponse> RequestTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken)
    {
        var url = new Uri(_authority, tenantId + "/oauth2/v2.0/token");
        var form = new List<KeyValuePair<string, string>>(4)
        {
            new("grant_type", "client_credentials"),
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("scope", scope)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EntraIdTokenException(response.StatusCode, body);
        }

        var token = System.Text.Json.JsonSerializer.Deserialize(body, EntraIdJsonContext.Default.EntraIdTokenResponse);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            throw new InvalidOperationException("Entra ID token response did not contain access_token.");
        }
        return token;
    }

    public void InvalidateAll()
    {
        lock (_lock) { _cache.Clear(); }
    }

    private readonly record struct CacheEntry(string Token, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Thrown by <see cref="EntraIdTokenProvider"/> when the Entra ID token endpoint
/// returns a non-success status. Carries the originating HTTP status so consuming
/// modules can render the AWS-service-native error shape (a token 429 becomes the
/// service's retryable throttle, a token 5xx its transient error, an auth /
/// bad-request its access-denied error) instead of a bare HTTP 500.
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

public sealed class EntraIdTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = "Bearer";
    [JsonPropertyName("expires_in")] public int ExpiresInSeconds { get; set; }
    [JsonPropertyName("ext_expires_in")] public int? ExtExpiresInSeconds { get; set; }
}

[JsonSerializable(typeof(EntraIdTokenResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, AllowTrailingCommas = true)]
internal partial class EntraIdJsonContext : JsonSerializerContext
{
}
