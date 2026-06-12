using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// An <see cref="IEntraTokenSource"/> backed by the OAuth 2.0 client-credentials
/// flow: it posts a service-principal <c>client_id</c> + <c>client_secret</c> to
/// the Entra ID v2.0 token endpoint (no Azure.Identity dependency). One instance
/// is bound to a single (tenant, clientId, clientSecret) identity; the cache /
/// single-flight / stale-while-revalidate machinery lives in
/// <see cref="CachedTokenSource"/>.
/// </summary>
public sealed class ClientCredentialsTokenSource : CachedTokenSource, IEntraTokenSource
{
    private readonly Func<string, CancellationToken, ValueTask<AccessToken>> _fetch;
    private readonly AzureHttpClient _http;
    private readonly Uri _authority;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;

    public ClientCredentialsTokenSource(
        AzureHttpClient http,
        string tenantId,
        string clientId,
        string clientSecret,
        Uri? authority = null,
        TimeProvider? clock = null)
        : base(clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        _http = http;
        _authority = authority ?? new Uri("https://login.microsoftonline.com/");
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        // Bound once per source so the cache-hit path never allocates a delegate.
        _fetch = RequestTokenAsync;
    }

    public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return GetOrRefreshAsync(scope, scope, _fetch, cancellationToken);
    }

    private ValueTask<AccessToken> RequestTokenAsync(string scope, CancellationToken cancellationToken)
        => RequestClientCredentialsTokenAsync(_http, _authority, _tenantId, _clientId, _clientSecret, scope, cancellationToken);

    /// <summary>
    /// Posts a <c>client_credentials</c> grant to the Entra ID v2.0 token endpoint and
    /// returns the access token plus its lifetime. Throws <see cref="EntraIdTokenException"/>
    /// (carrying the originating HTTP status) on a non-success response. Shared with
    /// the multi-identity <see cref="EntraIdTokenProvider"/> facade so both paths emit
    /// identical wire-faithful errors.
    /// </summary>
    internal static async ValueTask<AccessToken> RequestClientCredentialsTokenAsync(
        AzureHttpClient http,
        Uri authority,
        string tenantId,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken)
    {
        var url = new Uri(authority, tenantId + "/oauth2/v2.0/token");
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

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EntraIdTokenException(response.StatusCode, body);
        }

        var token = JsonSerializer.Deserialize(body, EntraIdJsonContext.Default.EntraIdTokenResponse);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            throw new InvalidOperationException("Entra ID token response did not contain access_token.");
        }
        return new AccessToken(token.AccessToken, token.ExpiresInSeconds);
    }
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
