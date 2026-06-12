using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Managed Identity token source backed by Azure's IMDS-compatible endpoints.
/// Supports system-assigned identities and user-assigned identities by
/// <c>client_id</c>, without depending on Azure.Identity.
/// </summary>
public sealed class ImdsTokenSource : CachedTokenSource, IEntraTokenSource
{
    private const string DefaultEndpoint = "http://169.254.169.254/metadata/identity/oauth2/token";
    private const string ImdsApiVersion = "2018-02-01";
    private const string AppServiceApiVersion = "2019-08-01";
    private const string MetadataHeaderName = "Metadata";
    private const string AppServiceIdentityHeaderName = "X-IDENTITY-HEADER";
    private const string DefaultScopeToken = ".default";
    private const string DefaultScopeSuffix = "/.default";

    private readonly Func<string, CancellationToken, ValueTask<AccessToken>> _fetch;
    private readonly AzureHttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiVersion;
    private readonly string? _clientId;
    private readonly string? _identityHeaderName;
    private readonly string? _identityHeaderValue;

    public ImdsTokenSource(
        AzureHttpClient http,
        string? clientId = null,
        Uri? endpoint = null,
        string? apiVersion = null,
        string? identityHeaderName = null,
        string? identityHeaderValue = null,
        TimeProvider? clock = null)
        : base(clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (clientId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        }

        _http = http;
        _clientId = clientId;
        ResolveEndpoint(endpoint, apiVersion, identityHeaderName, identityHeaderValue,
            out _endpoint, out _apiVersion, out _identityHeaderName, out _identityHeaderValue);

        // Bound once per source so the cache-hit path never allocates a delegate.
        _fetch = RequestTokenAsync;
    }

    public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var resource = ScopeToResource(scope);
        var cacheKey = string.Concat("mi|", _clientId ?? "system", "|", resource);
        return GetOrRefreshAsync(cacheKey, resource, _fetch, cancellationToken);
    }

    internal static string ScopeToResource(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope.EndsWith(DefaultScopeSuffix, StringComparison.Ordinal)
            ? scope[..^DefaultScopeToken.Length]
            : scope;
    }

    private static void ResolveEndpoint(
        Uri? endpoint,
        string? apiVersion,
        string? identityHeaderName,
        string? identityHeaderValue,
        out Uri resolvedEndpoint,
        out string resolvedApiVersion,
        out string? resolvedIdentityHeaderName,
        out string? resolvedIdentityHeaderValue)
    {
        resolvedIdentityHeaderName = identityHeaderName;
        resolvedIdentityHeaderValue = identityHeaderValue;

        if (endpoint is null)
        {
            var environmentEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
            var environmentHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
            if (!string.IsNullOrEmpty(environmentEndpoint) && !string.IsNullOrEmpty(environmentHeader))
            {
                resolvedEndpoint = new Uri(environmentEndpoint, UriKind.Absolute);
                resolvedApiVersion = apiVersion ?? AppServiceApiVersion;
                resolvedIdentityHeaderName ??= AppServiceIdentityHeaderName;
                resolvedIdentityHeaderValue ??= environmentHeader;
                ValidateHeader(resolvedIdentityHeaderName, resolvedIdentityHeaderValue);
                return;
            }

            resolvedEndpoint = new Uri(DefaultEndpoint, UriKind.Absolute);
            resolvedApiVersion = apiVersion ?? ImdsApiVersion;
            ValidateHeader(resolvedIdentityHeaderName, resolvedIdentityHeaderValue);
            return;
        }

        resolvedEndpoint = endpoint;
        if (resolvedIdentityHeaderName is null && resolvedIdentityHeaderValue is not null)
        {
            resolvedIdentityHeaderName = AppServiceIdentityHeaderName;
        }

        resolvedApiVersion = apiVersion ?? (resolvedIdentityHeaderValue is null ? ImdsApiVersion : AppServiceApiVersion);
        ValidateHeader(resolvedIdentityHeaderName, resolvedIdentityHeaderValue);
    }

    private static void ValidateHeader(string? headerName, string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerName) != string.IsNullOrWhiteSpace(headerValue))
        {
            throw new ArgumentException("Managed Identity endpoint header name and value must be supplied together.");
        }

        if (headerName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
            ArgumentException.ThrowIfNullOrWhiteSpace(headerValue);
        }
    }

    private async ValueTask<AccessToken> RequestTokenAsync(string resource, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildTokenUri(_endpoint, _apiVersion, resource, _clientId));
        if (_identityHeaderName is null)
        {
            request.Headers.TryAddWithoutValidation(MetadataHeaderName, "true");
        }
        else
        {
            request.Headers.TryAddWithoutValidation(_identityHeaderName, _identityHeaderValue);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EntraIdTokenException(response.StatusCode, body);
        }

        var token = JsonSerializer.Deserialize(body, ImdsJsonContext.Default.ImdsTokenResponse);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            throw new InvalidOperationException("IMDS token response did not contain access_token.");
        }

        return new AccessToken(token.AccessToken, token.ExpiresInSeconds);
    }

    private static Uri BuildTokenUri(Uri endpoint, string apiVersion, string resource, string? clientId)
    {
        var builder = new UriBuilder(endpoint);
        var existingQuery = builder.Query;
        if (existingQuery.Length > 0 && existingQuery[0] == '?')
        {
            existingQuery = existingQuery[1..];
        }

        var query = new StringBuilder(existingQuery);
        AppendQueryParameter(query, "api-version", apiVersion);
        AppendQueryParameter(query, "resource", resource);
        if (clientId is not null)
        {
            AppendQueryParameter(query, "client_id", clientId);
        }

        builder.Query = query.ToString();
        return builder.Uri;
    }

    private static void AppendQueryParameter(StringBuilder query, string name, string value)
    {
        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(Uri.EscapeDataString(name));
        query.Append('=');
        query.Append(Uri.EscapeDataString(value));
    }
}

internal sealed class ImdsTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("expires_in")] public int ExpiresInSeconds { get; set; }
}

[JsonSerializable(typeof(ImdsTokenResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
internal partial class ImdsJsonContext : JsonSerializerContext
{
}
