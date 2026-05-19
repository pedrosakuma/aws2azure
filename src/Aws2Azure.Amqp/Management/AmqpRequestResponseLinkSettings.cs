using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Management;

/// <summary>
/// Settings for a paired sender/receiver request/response link, as
/// used by AMQP node addresses such as <c>$cbs</c> and
/// <c>$management</c>.
/// </summary>
internal sealed class AmqpRequestResponseLinkSettings
{
    /// <summary>The node address (sender target + receiver source). E.g. <c>$cbs</c>.</summary>
    public required string Address { get; init; }

    /// <summary>
    /// The reply-to address advertised on outgoing requests and used
    /// as the receiver's target address. Must be unique enough for
    /// the broker to route responses back; defaults to a GUID suffix
    /// when not specified.
    /// </summary>
    public string? ReplyToAddress { get; init; }

    /// <summary>Sender link name. Defaults to <c>"&lt;address&gt;-sender:&lt;guid&gt;"</c>.</summary>
    public string? SenderName { get; init; }

    /// <summary>Receiver link name. Defaults to <c>"&lt;address&gt;-receiver:&lt;guid&gt;"</c>.</summary>
    public string? ReceiverName { get; init; }

    /// <summary>
    /// Initial link-credit granted to the receiver. Most management
    /// nodes return one response per request, so a small standing
    /// credit (default 50) is plenty.
    /// </summary>
    public uint InitialReceiverCredit { get; init; } = 50;
}
