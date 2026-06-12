using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Multi-identity facade over per-identity <see cref="ClientCredentialsTokenSource"/>
/// instances. Acquires OAuth 2.0 bearer tokens from Entra ID using the
/// client-credentials flow, talking directly to the v2.0 token endpoint (no
/// Azure.Identity). Each distinct (tenant, clientId, clientSecret) tuple gets its
/// own source — and therefore its own per-scope cache, single-flight refresh and
/// stale-while-revalidate behaviour (see <see cref="CachedTokenSource"/>). This
/// type keeps the existing call shape used across modules; the work itself lives
/// in the source it delegates to.
/// </summary>
public sealed class EntraIdTokenProvider
{
    private readonly AzureHttpClient _http;
    private readonly Uri _authority;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, ClientCredentialsTokenSource> _sources = new(StringComparer.Ordinal);

    public EntraIdTokenProvider(AzureHttpClient http, Uri? authority = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _authority = authority ?? new Uri("https://login.microsoftonline.com/");
        _clock = clock ?? TimeProvider.System;
    }

    public ValueTask<string> GetTokenAsync(
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

        return GetOrCreateSource(tenantId, clientId, clientSecret).GetTokenAsync(scope, cancellationToken);
    }

    private ClientCredentialsTokenSource GetOrCreateSource(string tenantId, string clientId, string clientSecret)
    {
        var key = tenantId + "|" + clientId + "|" + clientSecret;
        lock (_lock)
        {
            if (!_sources.TryGetValue(key, out var source))
            {
                source = new ClientCredentialsTokenSource(_http, tenantId, clientId, clientSecret, _authority, _clock);
                _sources[key] = source;
            }
            return source;
        }
    }

    /// <summary>Drops every cached token across all identities, forcing fresh fetches.</summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            foreach (var source in _sources.Values)
            {
                source.InvalidateAll();
            }
        }
    }
}
