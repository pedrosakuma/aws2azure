using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Result of an outgoing transfer's disposition (§3.4). Reports the
/// terminal outcomes the proxy acts on; the AMQP <c>received</c>
/// (partial) outcome is folded into <see cref="Unknown"/>.
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

    /// <summary>
    /// Parses a delivery state into its outcome and, when the outcome is
    /// <see cref="AmqpDispositionOutcome.Rejected"/> and the broker
    /// included an <c>error</c>, the AMQP condition + description
    /// strings. Other outcomes return null condition/description.
    /// Defensive against malformed payloads — parse failures degrade to
    /// (outcome, null, null) rather than propagating exceptions out of
    /// the disposition dispatch path.
    /// </summary>
    public static (AmqpDispositionOutcome Outcome, string? Condition, string? Description) FromWithError(
        ReadOnlyMemory<byte> state)
    {
        var outcome = From(state);
        if (outcome != AmqpDispositionOutcome.Rejected || state.IsEmpty)
            return (outcome, null, null);

        try
        {
            Rejected.Read(state, out var rej, out _);
            if (rej.Error.IsEmpty) return (outcome, null, null);
            AmqpError.Read(rej.Error, out var err, out _);
            return (outcome, err.Condition, err.Description);
        }
        catch
        {
            return (outcome, null, null);
        }
    }
}
