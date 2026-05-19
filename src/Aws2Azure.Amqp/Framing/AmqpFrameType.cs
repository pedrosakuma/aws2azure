namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 frame type code (§2.3.1, byte at offset 5 of every frame
/// header). Service Bus uses both values: SASL for the authentication
/// handshake, AMQP for everything afterwards.
/// </summary>
internal enum AmqpFrameType : byte
{
    /// <summary>Standard AMQP frame; carries a performative + optional payload.</summary>
    Amqp = 0x00,

    /// <summary>SASL frame; carries a sasl-mechanisms / sasl-init / sasl-outcome.</summary>
    Sasl = 0x01,
}
