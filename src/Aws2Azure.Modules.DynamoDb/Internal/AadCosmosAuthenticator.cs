using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// AAD bearer-token authenticator for Cosmos DB. Acquires tokens via
/// the shared <see cref="EntraIdTokenProvider"/> (client-credentials
/// flow) for the Cosmos scope <c>https://cosmos.azure.com/.default</c>
/// and emits the <c>Authorization: Bearer ...</c> header plus the
/// mandatory <c>x-ms-date</c>.
///
/// <para>Cosmos's AAD endpoint accepts the master-key resource-type +
/// resource-link still for RBAC checks, but the canonical signing
/// string is replaced by the bearer token — this implementation
/// therefore ignores <c>resourceType</c> / <c>resourceLink</c> at
/// signature time.</para>
/// </summary>
internal sealed class AadCosmosAuthenticator : ICosmosAuthenticator
{
    public const string CosmosScope = "https://cosmos.azure.com/.default";

    private readonly EntraIdTokenProvider _tokenProvider;
    private readonly AadAuthSettings _auth;
    private readonly Func<DateTimeOffset> _clock;

    public AadCosmosAuthenticator(
        EntraIdTokenProvider tokenProvider,
        AadAuthSettings auth,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);

        _tokenProvider = tokenProvider;
        _auth = auth;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask AuthenticateAsync(
        HttpRequestMessage request,
        string resourceType,
        string resourceLink,
        CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(
            _auth, CosmosScope, ct).ConfigureAwait(false);

        // Cosmos AAD requires the literal string "type=aad&ver=1.0&sig=<token>"
        // URL-encoded, NOT a bare bearer header. The token itself is appended
        // as the signature portion.
        var raw = string.Concat("type=aad&ver=1.0&sig=", token);
        request.Headers.TryAddWithoutValidation("authorization", Uri.EscapeDataString(raw));
        request.Headers.TryAddWithoutValidation("x-ms-date", CosmosMasterKeyAuth.GetHttpUtcDate(_clock()));
    }
}
