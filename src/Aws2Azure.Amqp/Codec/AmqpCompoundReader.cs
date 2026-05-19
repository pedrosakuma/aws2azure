using System.Buffers.Binary;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Decoded view over an AMQP compound or array envelope. The
/// <see cref="Elements"/> span borrows from the input buffer; it stays
/// valid for as long as that buffer does. <see cref="Count"/> is the
/// wire count (twice the pair count for maps).
/// </summary>
internal readonly ref struct AmqpCompoundView
{
    public AmqpCompoundView(int count, ReadOnlySpan<byte> elements)
    {
        Count = count;
        Elements = elements;
    }

    public int Count { get; }
    public ReadOnlySpan<byte> Elements { get; }
}

/// <summary>
/// Decoded view over an AMQP array envelope: count + shared element
/// constructor (as a single format-code byte) + element data. We
/// intentionally do not support multi-byte described element
/// constructors at this layer — none of the SQS-over-SB performatives
/// require them.
/// </summary>
internal readonly ref struct AmqpArrayView
{
    public AmqpArrayView(int count, byte elementConstructor, ReadOnlySpan<byte> elementData)
    {
        Count = count;
        ElementConstructor = elementConstructor;
        ElementData = elementData;
    }

    public int Count { get; }
    public byte ElementConstructor { get; }
    public ReadOnlySpan<byte> ElementData { get; }
}

/// <summary>
/// Decoders for AMQP 1.0 compound, array, and described-type envelopes.
/// </summary>
internal static class AmqpCompoundReader
{
    // --- list ------------------------------------------------------------

    public static AmqpCompoundView ReadList(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        switch (code)
        {
            case AmqpFormatCode.List0:
                consumed = 1;
                return new AmqpCompoundView(0, ReadOnlySpan<byte>.Empty);

            case AmqpFormatCode.List8:
            {
                int size = source[1];
                int count = source[2];
                consumed = 2 + size;
                if (consumed > source.Length)
                {
                    throw new InvalidDataException("Truncated AMQP list8.");
                }
                // Size includes the count byte; elements = size - 1.
                return new AmqpCompoundView(count, source.Slice(3, size - 1));
            }

            case AmqpFormatCode.List32:
            {
                var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[1..]));
                var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[5..]));
                consumed = 5 + size;
                if (consumed > source.Length)
                {
                    throw new InvalidDataException("Truncated AMQP list32.");
                }
                // Size includes the 4-byte count; elements = size - 4.
                return new AmqpCompoundView(count, source.Slice(9, size - 4));
            }

            default:
                throw UnexpectedCode("list", code);
        }
    }

    // --- map -------------------------------------------------------------

    /// <summary>
    /// Reads a map and exposes the same view shape as <see cref="ReadList"/>.
    /// <see cref="AmqpCompoundView.Count"/> is the wire count (twice the
    /// pair count). The caller iterates as alternating key/value pairs.
    /// </summary>
    public static AmqpCompoundView ReadMap(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        switch (code)
        {
            case AmqpFormatCode.Map8:
            {
                int size = source[1];
                int count = source[2];
                if ((count & 1) != 0)
                {
                    throw new InvalidDataException("AMQP map element count must be even.");
                }
                consumed = 2 + size;
                if (consumed > source.Length)
                {
                    throw new InvalidDataException("Truncated AMQP map8.");
                }
                return new AmqpCompoundView(count, source.Slice(3, size - 1));
            }

            case AmqpFormatCode.Map32:
            {
                var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[1..]));
                var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[5..]));
                if ((count & 1) != 0)
                {
                    throw new InvalidDataException("AMQP map element count must be even.");
                }
                consumed = 5 + size;
                if (consumed > source.Length)
                {
                    throw new InvalidDataException("Truncated AMQP map32.");
                }
                return new AmqpCompoundView(count, source.Slice(9, size - 4));
            }

            default:
                throw UnexpectedCode("map", code);
        }
    }

    // --- array -----------------------------------------------------------

    /// <summary>
    /// Reads an array assuming a single-byte element constructor.
    /// Sufficient for SASL <c>sasl-server-mechanisms</c> (array of symbol)
    /// and other arrays we encounter in the SB profile. Multi-byte
    /// described element constructors would surface here as the first
    /// data byte being 0x00 and the constructor length being unknown —
    /// the caller can detect and handle that explicitly.
    /// </summary>
    public static AmqpArrayView ReadArray(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        int size, count, headerLen;
        switch (code)
        {
            case AmqpFormatCode.Array8:
                size = source[1];
                count = source[2];
                headerLen = 3;
                break;
            case AmqpFormatCode.Array32:
                size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[1..]));
                count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[5..]));
                headerLen = 9;
                break;
            default:
                throw UnexpectedCode("array", code);
        }
        consumed = (code == AmqpFormatCode.Array8 ? 2 : 5) + size;
        if (consumed > source.Length)
        {
            throw new InvalidDataException("Truncated AMQP array.");
        }
        if (size < (code == AmqpFormatCode.Array8 ? 2 : 5))
        {
            // size must cover at least the count field + the 1-byte constructor.
            throw new InvalidDataException("AMQP array size is too small for its constructor.");
        }
        var elementConstructor = source[headerLen];
        var elementDataStart = headerLen + 1;
        var elementDataLen = consumed - elementDataStart;
        return new AmqpArrayView(count, elementConstructor, source.Slice(elementDataStart, elementDataLen));
    }

    // --- described type --------------------------------------------------

    /// <summary>
    /// Validates that <paramref name="source"/> begins with the described-type
    /// constructor byte (0x00) and returns the offset to the descriptor —
    /// in practice always 1. The caller decodes the descriptor (typically a
    /// ulong via <see cref="AmqpPrimitiveReader.ReadULong"/>) and then the
    /// value at the resulting offset.
    /// </summary>
    public static int ReadDescribedHeader(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty || source[0] != AmqpFormatCode.Described)
        {
            throw new InvalidDataException(
                $"Expected described-type constructor (0x00), got 0x{(source.IsEmpty ? 0 : source[0]):X2}.");
        }
        return 1;
    }

    private static InvalidDataException UnexpectedCode(string what, byte actual)
    {
        return new InvalidDataException($"Expected {what}, got AMQP format code 0x{actual:X2}.");
    }
}
