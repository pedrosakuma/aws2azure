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
    private readonly string _base64MasterKey;
    private readonly Func<DateTimeOffset> _clock;

    public MasterKeyCosmosAuthenticator(string base64MasterKey, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(base64MasterKey);
        _base64MasterKey = base64MasterKey;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ValueTask AuthenticateAsync(
        HttpRequestMessage request,
        string resourceType,
        string resourceLink,
        CancellationToken ct)
    {
        var utcDate = CosmosMasterKeyAuth.GetHttpUtcDate(_clock());
        var auth = CosmosMasterKeyAuth.Build(
            request.Method.Method, resourceType, resourceLink, utcDate, _base64MasterKey);

        request.Headers.TryAddWithoutValidation("authorization", auth);
        request.Headers.TryAddWithoutValidation("x-ms-date", utcDate);
        return ValueTask.CompletedTask;
    }
}
