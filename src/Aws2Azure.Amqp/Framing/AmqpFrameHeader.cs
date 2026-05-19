namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// Decoded view of an AMQP 1.0 frame header (§2.3.1, 8 bytes).
/// </summary>
internal readonly struct AmqpFrameHeader
{
    public AmqpFrameHeader(int size, int dataOffset, AmqpFrameType type, ushort channel)
    {
        Size = size;
        DataOffset = dataOffset;
        Type = type;
        Channel = channel;
    }

    /// <summary>
    /// Total frame size in bytes including the 8-byte header itself
    /// and any extended header words. Wire form is big-endian uint32 at
    /// offset 0.
    /// </summary>
    public int Size { get; }

    /// <summary>
    /// Offset of the frame body in bytes from the start of the frame.
    /// Equal to <c>DOFF × 4</c> where DOFF is the byte at offset 4.
    /// Minimum permitted value is 8 (no extended header).
    /// </summary>
    public int DataOffset { get; }

    /// <summary>AMQP (0x00) or SASL (0x01); byte at offset 5.</summary>
    public AmqpFrameType Type { get; }

    /// <summary>
    /// Channel number for AMQP frames (big-endian uint16 at offset 6);
    /// the spec mandates 0 for SASL frames.
    /// </summary>
    public ushort Channel { get; }
}
