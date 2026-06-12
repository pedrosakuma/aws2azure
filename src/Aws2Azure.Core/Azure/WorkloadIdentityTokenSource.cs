using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Acquires Entra ID tokens using AKS Workload Identity's federated JWT exchange.
/// The projected service-account token file is read on every refresh because
/// kubelet rotates it in place.
/// </summary>
public sealed class WorkloadIdentityTokenSource : CachedTokenSource, IEntraTokenSource
{
    private const string TenantIdEnvironmentVariable = "AZURE_TENANT_ID";
    private const string ClientIdEnvironmentVariable = "AZURE_CLIENT_ID";
    private const string FederatedTokenFileEnvironmentVariable = "AZURE_FEDERATED_TOKEN_FILE";
    private const string AuthorityHostEnvironmentVariable = "AZURE_AUTHORITY_HOST";
    private static readonly Uri DefaultAuthority = new("https://login.microsoftonline.com/");

    private readonly Func<string, CancellationToken, ValueTask<AccessToken>> _fetch;
    private readonly AzureHttpClient _http;
    private readonly Uri _authority;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _federatedTokenFilePath;

    // Single-slot memo so repeated cache-hit calls with the same scope reuse the
    // precomputed cache-key string instead of re-allocating per call.
    private volatile ScopeBinding? _lastScope;

    private sealed class ScopeBinding(string scope, string cacheKey)
    {
        public readonly string Scope = scope;
        public readonly string CacheKey = cacheKey;
    }

    public WorkloadIdentityTokenSource(
        AzureHttpClient http,
        string tenantId,
        string clientId,
        string federatedTokenFilePath,
        Uri? authority = null,
        TimeProvider? clock = null)
        : base(clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(federatedTokenFilePath);

        _http = http;
        _tenantId = tenantId;
        _clientId = clientId;
        _federatedTokenFilePath = federatedTokenFilePath;
        _authority = authority ?? DefaultAuthority;
        _fetch = RequestTokenAsync;
    }

    public static WorkloadIdentityTokenSource FromEnvironment(AzureHttpClient http, TimeProvider? clock = null)
        => FromEnvironmentValues(
            http,
            Environment.GetEnvironmentVariable(TenantIdEnvironmentVariable),
            Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable),
            Environment.GetEnvironmentVariable(FederatedTokenFileEnvironmentVariable),
            Environment.GetEnvironmentVariable(AuthorityHostEnvironmentVariable),
            clock);

    public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var binding = _lastScope;
        if (binding is null || !string.Equals(binding.Scope, scope, StringComparison.Ordinal))
        {
            binding = new ScopeBinding(scope, "wi|" + _clientId + "|" + scope);
            _lastScope = binding;
        }

        return GetOrRefreshAsync(binding.CacheKey, scope, _fetch, cancellationToken);
    }

    internal static WorkloadIdentityTokenSource FromEnvironmentValues(
        AzureHttpClient http,
        string? tenantId,
        string? clientId,
        string? federatedTokenFilePath,
        string? authorityHost,
        TimeProvider? clock = null)
    {
        var missing = new List<string>(3);
        if (string.IsNullOrWhiteSpace(tenantId)) missing.Add(TenantIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add(ClientIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(federatedTokenFilePath)) missing.Add(FederatedTokenFileEnvironmentVariable);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("Missing required workload identity environment variables: " + string.Join(", ", missing) + ".");
        }

        var authority = string.IsNullOrWhiteSpace(authorityHost) ? null : new Uri(authorityHost);
        return new WorkloadIdentityTokenSource(http, tenantId!, clientId!, federatedTokenFilePath!, authority, clock);
    }

    private ValueTask<AccessToken> RequestTokenAsync(string scope, CancellationToken cancellationToken)
        => RequestWorkloadIdentityTokenAsync(_http, _authority, _tenantId, _clientId, _federatedTokenFilePath, scope, cancellationToken);

    private static async ValueTask<AccessToken> RequestWorkloadIdentityTokenAsync(
        AzureHttpClient http,
        Uri authority,
        string tenantId,
        string clientId,
        string federatedTokenFilePath,
        string scope,
        CancellationToken cancellationToken)
    {
        string clientAssertion;
        try
        {
            clientAssertion = (await File.ReadAllTextAsync(federatedTokenFilePath, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Workload identity federated token file could not be read: " + federatedTokenFilePath + ".", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Workload identity federated token file could not be read: " + federatedTokenFilePath + ".", ex);
        }

        if (string.IsNullOrEmpty(clientAssertion))
        {
            throw new InvalidOperationException("Workload identity federated token file was empty: " + federatedTokenFilePath + ".");
        }

        var url = new Uri(authority, tenantId + "/oauth2/v2.0/token");
        var form = new List<KeyValuePair<string, string>>(5)
        {
            new("grant_type", "client_credentials"),
            new("client_id", clientId),
            new("scope", scope),
            new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
            new("client_assertion", clientAssertion)
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
