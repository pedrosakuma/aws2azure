using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Cosmos REST requests can be authenticated either with the master key
/// (HMAC-SHA256 of a per-request canonical string) or with an Entra ID
/// bearer token. This abstraction lets <see cref="CosmosClient"/> stay
/// auth-agnostic — picking the concrete implementation happens once at
/// module composition time based on the credential shape.
/// </summary>
internal interface ICosmosAuthenticator
{
    /// <summary>
    /// Adds the appropriate authorization + ancillary headers
    /// (<c>x-ms-date</c>, etc.) to the request. Cosmos requires
    /// <c>x-ms-date</c> on every call regardless of auth scheme; the
    /// authenticator owns it so master-key signing and AAD agree on the
    /// timestamp.
    /// </summary>
    ValueTask AuthenticateAsync(
        HttpRequestMessage request,
        string resourceType,
        string resourceLink,
        CancellationToken ct);
}
