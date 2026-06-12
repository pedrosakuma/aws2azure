using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Acquires OAuth 2.0 bearer tokens from Entra ID using the client-credentials
/// flow, talking directly to the v2.0 token endpoint (no Azure.Identity). A single
/// instance serves many service-principal identities: tokens are cached per
/// (tenant, clientId, scope) tuple and refreshed when inside a safety window before
/// expiry, with the single-flight / stale-while-revalidate machinery provided by
/// <see cref="CachedTokenSource"/>. The per-call <c>clientSecret</c> is used only to
/// mint the token; it is not part of the cache identity, so a fresh token already
/// held for a (tenant, clientId, scope) is served regardless of which secret the
/// caller presents.
///
/// <para>For a single fixed identity (the shape Managed Identity / Workload Identity
/// build on) prefer the per-identity <see cref="ClientCredentialsTokenSource"/>; this
/// facade keeps the multi-identity call shape used across the service modules.</para>
/// </summary>
public sealed class EntraIdTokenProvider : CachedTokenSource
{
    // Static so the cache-hit path never allocates a delegate; per-call inputs ride
    // in the value-type RequestState instead of a captured closure.
    private static readonly Func<RequestState, CancellationToken, ValueTask<AccessToken>> Fetch =
        static (s, ct) => ClientCredentialsTokenSource.RequestClientCredentialsTokenAsync(
            s.Http, s.Authority, s.TenantId, s.ClientId, s.ClientSecret, s.Scope, ct);

    private readonly AzureHttpClient _http;
    private readonly Uri _authority;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, IEntraTokenSource> _modeSources = new(StringComparer.Ordinal);

    public EntraIdTokenProvider(AzureHttpClient http, Uri? authority = null, TimeProvider? clock = null)
        : base(clock)
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

        var cacheKey = tenantId + "|" + clientId + "|" + scope;
        var state = new RequestState(_http, _authority, tenantId, clientId, clientSecret, scope);
        return GetOrRefreshAsync(cacheKey, state, Fetch, cancellationToken);
    }

    public ValueTask<string> GetTokenAsync(in AadAuthSettings auth, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        switch (auth.Mode)
        {
            case AzureAuthMode.ClientSecret:
                return GetTokenAsync(auth.TenantId!, auth.ClientId!, auth.ClientSecret!, scope, cancellationToken);
            case AzureAuthMode.ManagedIdentity:
                return ResolveManagedIdentity(auth.ClientId).GetTokenAsync(scope, cancellationToken);
            case AzureAuthMode.WorkloadIdentity:
                return ResolveWorkloadIdentity().GetTokenAsync(scope, cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(auth));
        }
    }

    private IEntraTokenSource ResolveManagedIdentity(string? clientId)
        => _modeSources.GetOrAdd(
            "mi|" + (clientId ?? "system"),
            static (_, s) => new ImdsTokenSource(s.Http, s.ClientId, clock: s.Clock),
            (Http: _http, ClientId: clientId, Clock: _clock));

    private IEntraTokenSource ResolveWorkloadIdentity()
        => _modeSources.GetOrAdd(
            "wi",
            static (_, s) => WorkloadIdentityTokenSource.FromEnvironment(s.Http, s.Clock),
            (Http: _http, Clock: _clock));

    private readonly record struct RequestState(
        AzureHttpClient Http,
        Uri Authority,
        string TenantId,
        string ClientId,
        string ClientSecret,
        string Scope);
}
