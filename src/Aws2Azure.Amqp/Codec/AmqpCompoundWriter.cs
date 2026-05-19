using System.Buffers.Binary;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Encoders for AMQP 1.0 compound and described types
/// (OASIS AMQP 1.0 §1.6.22–§1.6.25 and §1.3):
/// <list type="bullet">
///   <item><description><c>list0</c> (0x45) — empty list, 1 byte total.</description></item>
///   <item><description><c>list8</c> (0xC0) / <c>list32</c> (0xD0) — count + concatenated element values.</description></item>
///   <item><description><c>map8</c> (0xC1) / <c>map32</c> (0xD1) — count + concatenated key/value alternating values (count is the total element count, i.e. 2× the pair count).</description></item>
///   <item><description><c>array8</c> (0xE0) / <c>array32</c> (0xF0) — count + element constructor + element data.</description></item>
///   <item><description>Described type (0x00 + descriptor + value) — used by every AMQP performative and message section.</description></item>
/// </list>
/// These encoders accept pre-encoded element bytes so callers can compose
/// arbitrary content using the primitive writers; the higher-level
/// performative encoder (Slice 3) builds on top.
/// </summary>
internal static class AmqpCompoundWriter
{
    private const int ShortFormCap = byte.MaxValue;

    // --- list ------------------------------------------------------------

    /// <summary>
    /// Writes <c>list0</c> (a single byte, 0x45). Distinct from
    /// list8/list32 with zero elements — the spec mandates this short
    /// form for empty lists.
    /// </summary>
    public static void WriteList0(Span<byte> destination, out int written)
    {
        destination[0] = AmqpFormatCode.List0;
        written = 1;
    }

    /// <summary>
    /// Writes a list, selecting <c>list0</c>, <c>list8</c>, or
    /// <c>list32</c> based on count and payload size. <paramref name="elements"/>
    /// is the concatenation of <paramref name="count"/> pre-encoded AMQP
    /// values (constructor + body each).
    /// </summary>
    public static void WriteList(Span<byte> destination, ReadOnlySpan<byte> elements, int count, out int written)
    {
        if (count == 0 && elements.IsEmpty)
        {
            WriteList0(destination, out written);
            return;
        }

        // §1.6.22 — short form: size and count both fit in 1 byte AND
        // the size field includes the count byte but excludes the size
        // byte itself. That gives an effective payload limit of 254.
        var shortSize = 1 + elements.Length;
        if (shortSize <= ShortFormCap && count <= ShortFormCap)
        {
            destination[0] = AmqpFormatCode.List8;
            destination[1] = (byte)shortSize;
            destination[2] = (byte)count;
            elements.CopyTo(destination[3..]);
            written = 3 + elements.Length;
            return;
        }

        // Long form: size = 4-byte count field + elements.
        var longSize = 4 + elements.Length;
        destination[0] = AmqpFormatCode.List32;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)longSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination[5..], (uint)count);
        elements.CopyTo(destination[9..]);
        written = 9 + elements.Length;
    }

    // --- map -------------------------------------------------------------

    /// <summary>
    /// Writes a map. <paramref name="pairCount"/> is the number of
    /// key-value pairs; the wire <c>count</c> is twice that. There is
    /// no <c>map0</c> short form in the spec — an empty map is encoded
    /// as <c>map8</c> with size=1, count=0.
    /// </summary>
    public static void WriteMap(Span<byte> destination, ReadOnlySpan<byte> elements, int pairCount, out int written)
    {
        var elementCount = checked(pairCount * 2);
        var shortSize = 1 + elements.Length;
        if (shortSize <= ShortFormCap && elementCount <= ShortFormCap)
        {
            destination[0] = AmqpFormatCode.Map8;
            destination[1] = (byte)shortSize;
            destination[2] = (byte)elementCount;
            elements.CopyTo(destination[3..]);
            written = 3 + elements.Length;
            return;
        }

        var longSize = 4 + elements.Length;
        destination[0] = AmqpFormatCode.Map32;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)longSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination[5..], (uint)elementCount);
        elements.CopyTo(destination[9..]);
        written = 9 + elements.Length;
    }

    // --- array -----------------------------------------------------------

    /// <summary>
    /// Writes an array. Unlike list/map, all elements share a single
    /// <paramref name="elementConstructor"/> (the format-code byte —
    /// or a multi-byte described constructor encoded by the caller) and
    /// the element-data slice carries each element's body only, with no
    /// per-element constructor.
    /// </summary>
    public static void WriteArray(
        Span<byte> destination,
        ReadOnlySpan<byte> elementConstructor,
        ReadOnlySpan<byte> elementData,
        int count,
        out int written)
    {
        // size includes the count field, the constructor bytes, and the
        // element data — but not the size field itself.
        var shortSize = 1 + elementConstructor.Length + elementData.Length;
        if (shortSize <= ShortFormCap && count <= ShortFormCap)
        {
            destination[0] = AmqpFormatCode.Array8;
            destination[1] = (byte)shortSize;
            destination[2] = (byte)count;
            elementConstructor.CopyTo(destination[3..]);
            elementData.CopyTo(destination[(3 + elementConstructor.Length)..]);
            written = 3 + elementConstructor.Length + elementData.Length;
            return;
        }

        var longSize = 4 + elementConstructor.Length + elementData.Length;
        destination[0] = AmqpFormatCode.Array32;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)longSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination[5..], (uint)count);
        elementConstructor.CopyTo(destination[9..]);
        elementData.CopyTo(destination[(9 + elementConstructor.Length)..]);
        written = 9 + elementConstructor.Length + elementData.Length;
    }

    // --- described type --------------------------------------------------

    /// <summary>
    /// Writes a described type: the 0x00 constructor byte followed by the
    /// caller-encoded descriptor and value. Used by every AMQP performative
    /// (descriptor = ulong code per §1.3) and message section.
    /// </summary>
    public static void WriteDescribed(
        Span<byte> destination,
        ReadOnlySpan<byte> descriptor,
        ReadOnlySpan<byte> value,
        out int written)
    {
        destination[0] = AmqpFormatCode.Described;
        descriptor.CopyTo(destination[1..]);
        value.CopyTo(destination[(1 + descriptor.Length)..]);
        written = 1 + descriptor.Length + value.Length;
    }
}
