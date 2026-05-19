using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Thrown when the AMQP connection fails: peer rejected open, sent an
/// unexpected frame, missed its idle deadline, or closed with an error.
/// </summary>
internal sealed class AmqpConnectionException : Exception
{
    public AmqpConnectionException(string message, AmqpErrorKind kind = AmqpErrorKind.Unknown)
        : base(message)
    {
        Kind = kind;
    }

    public AmqpConnectionException(string message, Exception inner, AmqpErrorKind kind = AmqpErrorKind.Unknown)
        : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>
    /// Classification of the underlying failure (per
    /// <see cref="AmqpErrorClassifier"/>) so retry/backoff can act on it
    /// without inspecting the message text.
    /// </summary>
    public AmqpErrorKind Kind { get; init; }

    /// <summary>
    /// The error condition the peer reported (<c>close.error.condition</c>),
    /// when applicable. <c>null</c> for client-side faults.
    /// </summary>
    public string? PeerCondition { get; init; }
}
