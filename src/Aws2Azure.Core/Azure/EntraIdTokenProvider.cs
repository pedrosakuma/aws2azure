using System;
using System.Collections.Generic;
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

        lock (_lock)
        {
            if (_cache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAt - SafetyWindow > now)
            {
                return entry.Token;
            }
        }

        var token = await RequestTokenAsync(tenantId, clientId, clientSecret, scope, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            _cache[cacheKey] = new CacheEntry(token.AccessToken, now.AddSeconds(token.ExpiresInSeconds));
        }
        return token.AccessToken;
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
            throw new InvalidOperationException($"Entra ID token request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
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
