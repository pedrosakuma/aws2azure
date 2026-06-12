using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Core.Azure;

/// <summary>
/// Acquires OAuth 2.0 bearer tokens for a single, fixed Azure AD (Entra ID)
/// identity. Implementations differ only by where the token comes from —
/// the <c>client_credentials</c> flow (service principal + secret), the
/// Instance Metadata Service (managed identity), or an AKS federated-token
/// exchange (workload identity) — while sharing the cache / single-flight /
/// stale-while-revalidate machinery in <see cref="CachedTokenSource"/>.
/// </summary>
public interface IEntraTokenSource
{
    /// <summary>
    /// Returns a valid bearer token for <paramref name="scope"/>, fetching and
    /// caching one when none is cached or the cached one is inside its refresh
    /// window. Throws <see cref="EntraIdTokenException"/> (carrying a normalised
    /// backend status) when no usable token can be produced.
    /// </summary>
    ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken = default);
}
