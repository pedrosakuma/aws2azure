using System.Buffers.Binary;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Single-byte signed integer represented as the AMQP <c>byte</c> primitive
/// (format code 0x51). The CLR <see cref="sbyte"/> already covers the wire
/// range; this alias exists only to make the intent explicit at call sites
/// that mirror the spec's nomenclature.
/// </summary>
internal static class AmqpPrimitiveWriter
{
    // --- 0x40 family: zero-payload values --------------------------------

    public static void WriteNull(Span<byte> destination, out int written)
    {
        destination[0] = AmqpFormatCode.Null;
        written = 1;
    }

    public static void WriteBoolean(Span<byte> destination, bool value, out int written)
    {
        // §1.6.2: boolean has a dedicated short form (true=0x41, false=0x42)
        // and a 0x56-prefixed long form. The short form is always smaller and
        // always legal, so we emit it unconditionally.
        destination[0] = value ? AmqpFormatCode.BooleanTrue : AmqpFormatCode.BooleanFalse;
        written = 1;
    }

    // --- Unsigned integers ----------------------------------------------

    public static void WriteUByte(Span<byte> destination, byte value, out int written)
    {
        destination[0] = AmqpFormatCode.UByte;
        destination[1] = value;
        written = 2;
    }

    public static void WriteUShort(Span<byte> destination, ushort value, out int written)
    {
        destination[0] = AmqpFormatCode.UShort;
        BinaryPrimitives.WriteUInt16BigEndian(destination[1..], value);
        written = 3;
    }

    public static void WriteUInt(Span<byte> destination, uint value, out int written)
    {
        // §1.6.5: uint has three forms — uint0 (0x43, zero), smalluint
        // (0x52, 1 byte) for values 0..255, and uint (0x70, 4 bytes).
        if (value == 0)
        {
            destination[0] = AmqpFormatCode.UInt0;
            written = 1;
            return;
        }
        if (value <= byte.MaxValue)
        {
            destination[0] = AmqpFormatCode.UIntSmall;
            destination[1] = (byte)value;
            written = 2;
            return;
        }
        destination[0] = AmqpFormatCode.UInt;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], value);
        written = 5;
    }

    public static void WriteULong(Span<byte> destination, ulong value, out int written)
    {
        if (value == 0)
        {
            destination[0] = AmqpFormatCode.ULong0;
            written = 1;
            return;
        }
        if (value <= byte.MaxValue)
        {
            destination[0] = AmqpFormatCode.ULongSmall;
            destination[1] = (byte)value;
            written = 2;
            return;
        }
        destination[0] = AmqpFormatCode.ULong;
        BinaryPrimitives.WriteUInt64BigEndian(destination[1..], value);
        written = 9;
    }

    // --- Signed integers -------------------------------------------------

    public static void WriteByte(Span<byte> destination, sbyte value, out int written)
    {
        destination[0] = AmqpFormatCode.Byte;
        destination[1] = (byte)value;
        written = 2;
    }

    public static void WriteShort(Span<byte> destination, short value, out int written)
    {
        destination[0] = AmqpFormatCode.Short;
        BinaryPrimitives.WriteInt16BigEndian(destination[1..], value);
        written = 3;
    }

    public static void WriteInt(Span<byte> destination, int value, out int written)
    {
        // §1.6.10 lists a smallint variant (0x54, 1 byte). Service Bus
        // implementations consistently expect the long form for negotiated
        // values like ChannelMax / IdleTimeout, so we emit the 4-byte form
        // and skip the conditional. (Wider compatibility cost: 3 bytes per
        // negotiated int — acceptable.)
        destination[0] = AmqpFormatCode.Int;
        BinaryPrimitives.WriteInt32BigEndian(destination[1..], value);
        written = 5;
    }

    public static void WriteLong(Span<byte> destination, long value, out int written)
    {
        destination[0] = AmqpFormatCode.Long;
        BinaryPrimitives.WriteInt64BigEndian(destination[1..], value);
        written = 9;
    }

    // --- Floating point --------------------------------------------------

    public static void WriteFloat(Span<byte> destination, float value, out int written)
    {
        destination[0] = AmqpFormatCode.Float;
        BinaryPrimitives.WriteSingleBigEndian(destination[1..], value);
        written = 5;
    }

    public static void WriteDouble(Span<byte> destination, double value, out int written)
    {
        destination[0] = AmqpFormatCode.Double;
        BinaryPrimitives.WriteDoubleBigEndian(destination[1..], value);
        written = 9;
    }

    // --- Timestamp + UUID -----------------------------------------------

    /// <summary>
    /// Writes an AMQP timestamp: signed 64-bit milliseconds since the
    /// Unix epoch (1970-01-01T00:00:00Z), big-endian. Callers convert
    /// from <see cref="DateTimeOffset"/> via <see cref="DateTimeOffset.ToUnixTimeMilliseconds"/>.
    /// </summary>
    public static void WriteTimestamp(Span<byte> destination, long unixMilliseconds, out int written)
    {
        destination[0] = AmqpFormatCode.TimestampMs;
        BinaryPrimitives.WriteInt64BigEndian(destination[1..], unixMilliseconds);
        written = 9;
    }

    /// <summary>
    /// Writes a 16-byte UUID in the RFC 4122 network byte order. The CLR's
    /// <see cref="Guid.TryWriteBytes(Span{byte})"/> emits the historical
    /// Microsoft layout (mixed-endian on the first three fields); we
    /// re-emit big-endian explicitly here.
    /// </summary>
    public static void WriteUuid(Span<byte> destination, Guid value, out int written)
    {
        destination[0] = AmqpFormatCode.Uuid;
        Span<byte> tmp = stackalloc byte[16];
        if (!value.TryWriteBytes(tmp, bigEndian: true, out _))
        {
            // Defensive: the BCL guarantees TryWriteBytes succeeds for a
            // 16-byte buffer; this branch only protects against a
            // contract change.
            throw new InvalidOperationException("Guid.TryWriteBytes failed.");
        }
        tmp.CopyTo(destination[1..]);
        written = 17;
    }
}
