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

    /// <summary>Address of the target terminus.</summary>
    public string? TargetAddress { get; init; }

    /// <summary>Sender settle mode (§2.6.6). Defaults to <c>Mixed</c>.</summary>
    public AmqpSenderSettleMode SenderSettleMode { get; init; } = AmqpSenderSettleMode.Mixed;

    /// <summary>Receiver settle mode (§2.6.7). Defaults to <c>First</c>.</summary>
    public AmqpReceiverSettleMode ReceiverSettleMode { get; init; } = AmqpReceiverSettleMode.First;

    /// <summary>
    /// Initial delivery-count for sender links (REQUIRED for senders,
    /// §2.7.3). Receivers leave this null.
    /// </summary>
    public uint? InitialDeliveryCount { get; init; } = 0;
}
