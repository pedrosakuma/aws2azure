using System.Buffers.Binary;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Span-based reader for AMQP 1.0 fixed-width primitive types
/// (OASIS AMQP 1.0 §1.6.2–§1.6.16). Each method peeks the format code
/// from <paramref name="source"/>, returns the decoded value, and yields
/// the number of bytes consumed via <c>consumed</c>.
/// </summary>
/// <remarks>
/// Variable-width (binary/string/symbol), compound (list/map) and array
/// readers live in separate files (Slices 1 &amp; 2).
/// </remarks>
internal static class AmqpPrimitiveReader
{
    public static void ReadNull(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.Null);
        consumed = 1;
    }

    public static bool ReadBoolean(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        switch (code)
        {
            case AmqpFormatCode.BooleanTrue:
                consumed = 1;
                return true;
            case AmqpFormatCode.BooleanFalse:
                consumed = 1;
                return false;
            case AmqpFormatCode.Boolean:
                consumed = 2;
                return source[1] != 0;
            default:
                throw UnexpectedCode("boolean", code);
        }
    }

    // --- Unsigned integers ----------------------------------------------

    public static byte ReadUByte(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.UByte);
        consumed = 2;
        return source[1];
    }

    public static ushort ReadUShort(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.UShort);
        consumed = 3;
        return BinaryPrimitives.ReadUInt16BigEndian(source[1..]);
    }

    public static uint ReadUInt(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        switch (code)
        {
            case AmqpFormatCode.UInt0:
                consumed = 1;
                return 0;
            case AmqpFormatCode.UIntSmall:
                consumed = 2;
                return source[1];
            case AmqpFormatCode.UInt:
                consumed = 5;
                return BinaryPrimitives.ReadUInt32BigEndian(source[1..]);
            default:
                throw UnexpectedCode("uint", code);
        }
    }

    public static ulong ReadULong(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        switch (code)
        {
            case AmqpFormatCode.ULong0:
                consumed = 1;
                return 0;
            case AmqpFormatCode.ULongSmall:
                consumed = 2;
                return source[1];
            case AmqpFormatCode.ULong:
                consumed = 9;
                return BinaryPrimitives.ReadUInt64BigEndian(source[1..]);
            default:
                throw UnexpectedCode("ulong", code);
        }
    }

    // --- Signed integers -------------------------------------------------

    public static sbyte ReadByte(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.Byte);
        consumed = 2;
        return (sbyte)source[1];
    }

    public static short ReadShort(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.Short);
        consumed = 3;
        return BinaryPrimitives.ReadInt16BigEndian(source[1..]);
    }

    public static int ReadInt(ReadOnlySpan<byte> source, out int consumed)
    {
        // We accept both the long form (0x71) and the smallint form (0x54),
        // because peers we have no control over (e.g. real Service Bus) MAY
        // emit smallint to save bytes even though we never do.
        var code = source[0];
        if (code == AmqpFormatCode.Int)
        {
            consumed = 5;
            return BinaryPrimitives.ReadInt32BigEndian(source[1..]);
        }
        if (code == 0x54)
        {
            // smallint: signed 1-byte two's complement.
            consumed = 2;
            return (sbyte)source[1];
        }
        throw UnexpectedCode("int", code);
    }

    public static long ReadLong(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        if (code == AmqpFormatCode.Long)
        {
            consumed = 9;
            return BinaryPrimitives.ReadInt64BigEndian(source[1..]);
        }
        // smalllong (0x55): signed 1-byte two's complement.
        if (code == 0x55)
        {
            consumed = 2;
            return (sbyte)source[1];
        }
        throw UnexpectedCode("long", code);
    }

    // --- Floating point --------------------------------------------------

    public static float ReadFloat(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.Float);
        consumed = 5;
        return BinaryPrimitives.ReadSingleBigEndian(source[1..]);
    }

    public static double ReadDouble(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.Double);
        consumed = 9;
        return BinaryPrimitives.ReadDoubleBigEndian(source[1..]);
    }

    // --- Timestamp + UUID -----------------------------------------------

    public static long ReadTimestamp(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.TimestampMs);
        consumed = 9;
        return BinaryPrimitives.ReadInt64BigEndian(source[1..]);
    }

    public static Guid ReadUuid(ReadOnlySpan<byte> source, out int consumed)
    {
        ExpectCode(source, AmqpFormatCode.Uuid);
        consumed = 17;
        return new Guid(source.Slice(1, 16), bigEndian: true);
    }

    // --- helpers ---------------------------------------------------------

    private static void ExpectCode(ReadOnlySpan<byte> source, byte expected)
    {
        if (source.IsEmpty)
        {
            throw new InvalidDataException("AMQP buffer is empty.");
        }
        var actual = source[0];
        if (actual != expected)
        {
            throw UnexpectedCode($"format code 0x{expected:X2}", actual);
        }
    }

    private static InvalidDataException UnexpectedCode(string what, byte actual)
    {
        return new InvalidDataException($"Expected {what}, got AMQP format code 0x{actual:X2}.");
    }
}
