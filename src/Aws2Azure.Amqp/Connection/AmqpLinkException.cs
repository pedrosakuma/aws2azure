using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Thrown when an <see cref="AmqpLink"/> operation fails because the
/// peer detached the link, sent a malformed performative, or because
/// the link transitioned to a terminal state mid-operation.
///
/// <para>When the peer included an AMQP <c>error</c> on its
/// <c>detach</c> frame (§2.6.5), the decoded <see cref="PeerError"/>
/// is attached so callers — including the SQS module's AMQP
/// dispatcher — can map the condition to a transport-shaped error
/// (e.g. <c>com.microsoft:message-lock-lost</c> →
/// <c>MessageNotInflight</c>) instead of leaking a raw exception
/// message back to the upstream SQS caller.</para>
/// </summary>
internal sealed class AmqpLinkException : Exception
{
    public AmqpLinkException(string message, AmqpErrorKind kind = AmqpErrorKind.Unknown)
        : base(message)
    {
        Kind = kind;
    }

    public AmqpLinkException(string message, Exception inner, AmqpErrorKind kind = AmqpErrorKind.Unknown)
        : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>
    /// Classification of the underlying failure (per
    /// <see cref="AmqpErrorClassifier"/>) so retry / failover logic can
    /// act on it without inspecting the message text.
    /// </summary>
    public AmqpErrorKind Kind { get; init; }

    /// <summary>
    /// The AMQP error condition the peer reported on its
    /// <c>detach</c> frame, when present. <c>null</c> for client-side
    /// faults (terminal-state checks, decode failures) or when the
    /// peer detached without an error.
    /// </summary>
    public string? PeerCondition { get; init; }

    /// <summary>
    /// The peer's human-readable error description, when present.
    /// </summary>
    public string? PeerDescription { get; init; }
}
