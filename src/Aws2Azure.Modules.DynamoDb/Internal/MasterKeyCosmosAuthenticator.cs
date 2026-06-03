using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Master-key HMAC authenticator. Delegates the signature math to
/// <see cref="CosmosMasterKeyAuth"/>; this class owns the per-request
/// timestamp and header wiring.
/// </summary>
internal sealed class MasterKeyCosmosAuthenticator : ICosmosAuthenticator
{
    private readonly byte[] _masterKeyBytes;
    private readonly Func<DateTimeOffset> _clock;

    public MasterKeyCosmosAuthenticator(string base64MasterKey, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(base64MasterKey);
        // Decode the master key once: it is fixed per credential, so paying
        // the base64 decode per request was pure hot-path waste.
        _masterKeyBytes = Convert.FromBase64String(base64MasterKey);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ValueTask AuthenticateAsync(
        HttpRequestMessage request,
        string resourceType,
        string resourceLink,
        CancellationToken ct)
    {
        var utcDate = CosmosMasterKeyAuth.GetHttpUtcDate(_clock());
        var auth = CosmosMasterKeyAuth.BuildAuthHeader(
            request.Method.Method, resourceType, resourceLink, utcDate, _masterKeyBytes);

        request.Headers.TryAddWithoutValidation("authorization", auth);
        request.Headers.TryAddWithoutValidation("x-ms-date", utcDate);
        return ValueTask.CompletedTask;
    }
}
