using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Per-link configuration for the <c>attach</c> handshake (§2.6).
/// </summary>
internal sealed record AmqpLinkSettings
{
    /// <summary>
    /// Link name. Must be unique within the connection for a given
    /// link-endpoint pair; CBS uses well-known names like
    /// <c>"cbs-sender"</c>/<c>"cbs-receiver"</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Sender or receiver role (§2.6.3).</summary>
    public required AmqpRole Role { get; init; }

    /// <summary>Address of the source terminus (queue/topic/<c>$cbs</c>).</summary>
    public string? SourceAddress { get; init; }

    /// <summary>
    /// Opaque AMQP-encoded <c>filter</c> map written into the source
    /// terminus (§3.5.3 field 8). Used by Service Bus session-bound
    /// receivers to carry the
    /// <see cref="ServiceBus.ServiceBusSessionFilter"/> entry. Empty
    /// means "no filter".
    /// </summary>
    public ReadOnlyMemory<byte> SourceFilter { get; init; }

    /// <summary>Address of the target terminus.</summary>
    public string? TargetAddress { get; init; }

    /// <summary>Opaque AMQP-encoded fields map for attach properties.</summary>
    public ReadOnlyMemory<byte> Properties { get; init; }

    /// <summary>Sender settle mode (§2.6.6). Defaults to <c>Mixed</c>.</summary>
    public AmqpSenderSettleMode SenderSettleMode { get; init; } = AmqpSenderSettleMode.Mixed;

    /// <summary>Receiver settle mode (§2.6.7). Defaults to <c>First</c>.</summary>
    public AmqpReceiverSettleMode ReceiverSettleMode { get; init; } = AmqpReceiverSettleMode.First;

    /// <summary>
    /// Initial delivery-count for sender links (REQUIRED for senders,
    /// §2.7.3). Receivers leave this null.
    /// </summary>
    public uint? InitialDeliveryCount { get; init; } = 0;

    /// <summary>
    /// Local <c>max-message-size</c> advertised on attach (§2.7.3). When
    /// non-zero, the receiver enforces this cap during multi-frame
    /// reassembly and detaches the link with
    /// <c>amqp:link:message-size-exceeded</c> if the peer over-sends.
    /// Default 1 MiB; pass <c>0</c> for "no limit".
    /// </summary>
    public ulong MaxMessageSize { get; init; } = 1024UL * 1024UL;
}
