using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Result of an outgoing transfer's disposition (§3.4). Slice 5c
/// reports the four terminal outcomes; modified/received are folded
/// into <see cref="Unknown"/> until a workload needs them.
/// </summary>
internal enum AmqpDispositionOutcome
{
    Unknown = 0,
    Accepted = 1,
    Rejected = 2,
    Released = 3,
    Modified = 4,
}

/// <summary>
/// An incoming transfer queued on a receiver link. The bare-message
/// bytes are pool-free (copied out of the frame buffer) so callers may
/// hold them across await points.
/// </summary>
internal sealed class AmqpIncomingDelivery
{
    public AmqpIncomingDelivery(uint deliveryId, byte[] deliveryTag, bool settled, byte[] payload)
    {
        DeliveryId = deliveryId;
        DeliveryTag = deliveryTag;
        Settled = settled;
        Payload = payload;
    }

    public uint DeliveryId { get; }
    public byte[] DeliveryTag { get; }
    public bool Settled { get; }
    public byte[] Payload { get; }

    public AmqpMessage ToMessage() => AmqpMessage.Parse(Payload);
}

internal static class AmqpDispositionOutcomeExtractor
{
    public static AmqpDispositionOutcome From(ReadOnlyMemory<byte> state)
    {
        if (state.IsEmpty) return AmqpDispositionOutcome.Unknown;
        var kind = DeliveryState.PeekKind(state.Span, out _);
        return kind switch
        {
            DeliveryStateKind.Accepted => AmqpDispositionOutcome.Accepted,
            DeliveryStateKind.Rejected => AmqpDispositionOutcome.Rejected,
            DeliveryStateKind.Released => AmqpDispositionOutcome.Released,
            DeliveryStateKind.Modified => AmqpDispositionOutcome.Modified,
            _ => AmqpDispositionOutcome.Unknown,
        };
    }
}
