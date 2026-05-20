using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// A message delivered on a Service Bus receiver link. Wraps the raw
/// AMQP <see cref="AmqpIncomingDelivery"/> plus its parsed bare-message
/// sections so callers do not have to re-walk the payload.
/// <para>
/// SB-specific metadata (sequence-number, locked-until, enqueued-time,
/// dead-letter source) lives in the <c>message-annotations</c> section
/// and is surfaced via <see cref="Annotations"/>. The redelivery counter
/// comes from the <c>header.delivery-count</c> field
/// (<see cref="DeliveryCount"/>). The lock token is the raw AMQP
/// delivery-tag — SB stamps a 16-byte GUID there so disposition frames
/// can target it (<see cref="LockToken"/>).
/// </para>
/// </summary>
internal sealed class ServiceBusReceivedMessage
{
    private AmqpMessage? _parsed;

    internal ServiceBusReceivedMessage(AmqpIncomingDelivery delivery)
    {
        Delivery = delivery;
    }

    internal AmqpIncomingDelivery Delivery { get; }

    /// <summary>AMQP delivery-id — opaque correlation between transfer and disposition.</summary>
    public uint DeliveryId => Delivery.DeliveryId;

    /// <summary>AMQP delivery-tag the sender stamped on this delivery.</summary>
    public ReadOnlyMemory<byte> DeliveryTag => Delivery.DeliveryTag;

    /// <summary>True if the sender already settled the delivery (no disposition required).</summary>
    public bool SenderSettled => Delivery.Settled;

    /// <summary>Raw encoded bare-message bytes (all sections, in spec order).</summary>
    public ReadOnlyMemory<byte> RawPayload => Delivery.Payload;

    /// <summary>Lazily-parsed message sections (header / properties / annotations / application-properties / body).</summary>
    public AmqpMessage Message => _parsed ??= Delivery.ToMessage();

    /// <summary>Convenience accessor for the body bytes.</summary>
    public ReadOnlyMemory<byte> Body => Message.Body;

    /// <summary>Convenience accessor for <c>properties.message-id</c> (string variant).</summary>
    public string? MessageId => Message.Properties.MessageId;

    /// <summary>
    /// SB-specific message annotations (sequence-number, locked-until,
    /// enqueued-time, etc). Null when the broker omitted the section —
    /// callers should treat absence as "not delivered by SB" rather than
    /// "default to 0".
    /// </summary>
    public AmqpMessageAnnotations? Annotations => Message.MessageAnnotations;

    /// <summary>
    /// AMQP redelivery count (<c>header.delivery-count</c>). Zero on the
    /// first delivery; SB increments on each redelivery after a lock
    /// expiry or explicit abandon. Null when the broker omitted the
    /// header section. To map onto SQS <c>ApproximateReceiveCount</c>
    /// (which counts the current delivery), callers add one.
    /// </summary>
    public uint? DeliveryCount => Message.Header?.DeliveryCount;

    /// <summary>
    /// The lock token — for Service Bus this is exactly the 16-byte
    /// delivery-tag interpreted as a little-endian GUID (matching the
    /// MS-MQ wire convention). Null when the delivery was sender-settled
    /// (no lock to take) or when the tag isn't 16 bytes.
    /// </summary>
    public Guid? LockToken
    {
        get
        {
            var tag = Delivery.DeliveryTag;
            if (tag is null || tag.Length != 16) return null;
            return new Guid(tag);
        }
    }
}
