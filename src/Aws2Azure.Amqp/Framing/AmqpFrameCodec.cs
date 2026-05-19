using System.Buffers.Binary;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 frame envelope codec (§2.3) and the connection protocol
/// preambles (§2.2). This is the transport-layer boundary: above it,
/// callers deal with performatives; below it, bytes go on the wire.
/// </summary>
internal static class AmqpFrameCodec
{
    /// <summary>The 8-byte frame header is the smallest legal frame.</summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// DOFF (data offset, byte 4) is expressed in 4-byte words. The
    /// minimum legal value is 2, which corresponds to a frame with no
    /// extended header (body starts immediately after the 8-byte
    /// header).
    /// </summary>
    public const byte MinDoff = 2;

    /// <summary>Body offset, in bytes, when DOFF == 2.</summary>
    public const int MinBodyOffset = MinDoff * 4;

    /// <summary>
    /// Protocol preamble for the AMQP 1.0.0 connection profile (§2.2):
    /// the four bytes 'AMQP' followed by protocol-id 0, major 1, minor
    /// 0, revision 0. Exchanged in both directions immediately after
    /// SASL completes (or right after TCP if no SASL is required).
    /// </summary>
    public static ReadOnlySpan<byte> AmqpProtocolHeader => "AMQP\u0000\u0001\u0000\u0000"u8;

    /// <summary>
    /// Protocol preamble for the SASL 1.0 sub-protocol (§5.3.2). Same
    /// shape as the AMQP header but with protocol-id 3, signalling that
    /// the next exchange is a SASL negotiation.
    /// </summary>
    public static ReadOnlySpan<byte> SaslProtocolHeader => "AMQP\u0003\u0001\u0000\u0000"u8;

    // --- writing ---------------------------------------------------------

    /// <summary>
    /// Writes a complete AMQP frame (header + body) into
    /// <paramref name="destination"/>. The frame uses DOFF=2 (no
    /// extended header). For SASL frames the channel field is forced
    /// to zero per spec §2.3.1.
    /// </summary>
    /// <returns>Number of bytes written (always <c>HeaderSize + body.Length</c>).</returns>
    public static int WriteFrame(
        Span<byte> destination,
        AmqpFrameType type,
        ushort channel,
        ReadOnlySpan<byte> body)
    {
        var totalSize = HeaderSize + body.Length;
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)totalSize);
        destination[4] = MinDoff;
        destination[5] = (byte)type;
        // SASL frames: spec §2.3.1 says channel is "ignored", we emit 0
        // to match every other implementation on the wire.
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], type == AmqpFrameType.Sasl ? (ushort)0 : channel);
        body.CopyTo(destination[HeaderSize..]);
        return totalSize;
    }

    // --- reading ---------------------------------------------------------

    /// <summary>
    /// Parses the 8-byte frame header from the start of
    /// <paramref name="source"/>. Throws when the buffer is too short
    /// or the header values are out-of-range. Does not require the
    /// body to be present.
    /// </summary>
    public static AmqpFrameHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"AMQP frame header requires {HeaderSize} bytes, got {source.Length}.");
        }
        var size = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (size < HeaderSize)
        {
            throw new InvalidDataException(
                $"AMQP frame size {size} is below the {HeaderSize}-byte header minimum.");
        }
        if (size > int.MaxValue)
        {
            throw new InvalidDataException("AMQP frame size exceeds Int32 capacity.");
        }
        var doff = source[4];
        if (doff < MinDoff)
        {
            throw new InvalidDataException($"AMQP DOFF {doff} is below the spec minimum of {MinDoff}.");
        }
        var dataOffset = doff * 4;
        if (dataOffset > size)
        {
            throw new InvalidDataException(
                $"AMQP DOFF (data offset {dataOffset}) exceeds frame size {size}.");
        }
        var type = source[5];
        if (type != (byte)AmqpFrameType.Amqp && type != (byte)AmqpFrameType.Sasl)
        {
            throw new InvalidDataException($"Unknown AMQP frame type 0x{type:X2}.");
        }
        var channel = BinaryPrimitives.ReadUInt16BigEndian(source[6..]);
        return new AmqpFrameHeader((int)size, dataOffset, (AmqpFrameType)type, channel);
    }

    /// <summary>
    /// Returns the body slice of <paramref name="frame"/> given a header
    /// previously parsed via <see cref="ReadHeader"/>. The caller is
    /// responsible for ensuring the buffer holds at least
    /// <c>header.Size</c> bytes.
    /// </summary>
    public static ReadOnlySpan<byte> GetBody(ReadOnlySpan<byte> frame, AmqpFrameHeader header)
    {
        if (frame.Length < header.Size)
        {
            throw new InvalidDataException(
                $"Frame buffer is {frame.Length} bytes but header declares {header.Size}.");
        }
        return frame.Slice(header.DataOffset, header.Size - header.DataOffset);
    }
}
