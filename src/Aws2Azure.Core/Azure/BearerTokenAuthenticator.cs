using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Bridges <see cref="EntraIdTokenProvider"/> into the
/// <see cref="IAzureAuthenticator"/> contract for a fixed
/// (tenant, clientId, secret, scope) tuple.
/// </summary>
public sealed class BearerTokenAuthenticator : IAzureAuthenticator
{
    private readonly EntraIdTokenProvider _provider;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scope;

    public BearerTokenAuthenticator(
        EntraIdTokenProvider provider,
        string tenantId,
        string clientId,
        string clientSecret,
        string scope)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _scope = scope;
    }

    public async ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var token = await _provider.GetTokenAsync(_tenantId, _clientId, _clientSecret, _scope, cancellationToken).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
    }
}
