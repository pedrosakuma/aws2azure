using System.Threading;
using System.Threading.Tasks;

namespace Aws2Azure.Amqp.Security;

/// <summary>
/// Supplies a CBS-compatible token for a given audience (typically
/// <c>amqps://&lt;namespace&gt;.servicebus.windows.net/&lt;entity&gt;</c>).
/// Implementations are responsible for caching and renewal — the
/// AMQP layer just asks for a token whenever it needs to authorise.
/// </summary>
internal interface IAmqpTokenProvider
{
    /// <summary>Token type sent in the CBS put-token application property (e.g. <c>servicebus.windows.net:sastoken</c>).</summary>
    string TokenType { get; }

    /// <summary>
    /// Produces a token string for <paramref name="audience"/>.
    /// Must include the absolute expiry that callers can read via
    /// <see cref="TryGetExpiry"/> when the implementation wishes to
    /// surface it (for proactive renewal).
    /// </summary>
    AmqpToken GetToken(string audience);

    /// <summary>
    /// Asynchronously produces a token string for <paramref name="audience"/>.
    /// Implementations that can mint tokens synchronously may rely on the
    /// default wrapper; OAuth-backed providers override this to avoid
    /// sync-over-async on CBS refresh.
    /// </summary>
    ValueTask<AmqpToken> GetTokenAsync(string audience, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(GetToken(audience));
}

/// <summary>
/// A token plus its absolute expiry. <see cref="ExpiresAtUtc"/> may be
/// <c>null</c> when the provider can't statically derive an expiry
/// (e.g. opaque OAuth tokens delegated from a managed identity).
/// </summary>
internal readonly record struct AmqpToken(string Value, DateTimeOffset? ExpiresAtUtc)
{
    public bool TryGetExpiry(out DateTimeOffset expiry)
    {
        if (ExpiresAtUtc is { } e) { expiry = e; return true; }
        expiry = default;
        return false;
    }
}
