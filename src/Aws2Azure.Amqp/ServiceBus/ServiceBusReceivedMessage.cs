using Aws2Azure.Amqp.Connection;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// A message delivered on a Service Bus receiver link. Wraps the raw
/// AMQP <see cref="AmqpIncomingDelivery"/> plus its parsed bare-message
/// sections so callers do not have to re-walk the payload.
/// <para>
/// SB-specific metadata (lock-token, sequence-number, locked-until,
/// enqueued-time, delivery-count) lives in the <c>message-annotations</c>
/// section. Slice 8a does NOT decode annotations: the
/// <see cref="AmqpMessage"/> parser models properties /
/// application-properties / body only, and SB happily delivers (and
/// settles) messages without us decoding the annotation map. Slice 8b
/// adds an annotations parser when wiring SQS receipt-handle generation
/// and ChangeMessageVisibility.
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

    /// <summary>Lazily-parsed message sections (properties / application-properties / body).</summary>
    public AmqpMessage Message => _parsed ??= Delivery.ToMessage();

    /// <summary>Convenience accessor for the body bytes.</summary>
    public ReadOnlyMemory<byte> Body => Message.Body;

    /// <summary>Convenience accessor for <c>properties.message-id</c> (string variant).</summary>
    public string? MessageId => Message.Properties.MessageId;
}
