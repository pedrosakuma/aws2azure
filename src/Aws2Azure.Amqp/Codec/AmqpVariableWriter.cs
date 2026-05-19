using System.Buffers.Binary;
using System.Text;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Span-based encoders for AMQP 1.0 variable-width primitives
/// (OASIS AMQP 1.0 §1.6.17–§1.6.21):
/// <list type="bullet">
///   <item><description>binary  — 0xA0 (size byte) / 0xB0 (size uint).</description></item>
///   <item><description>string  — 0xA1 / 0xB1, UTF-8 encoded.</description></item>
///   <item><description>symbol  — 0xA3 / 0xB3, 7-bit ASCII only.</description></item>
/// </list>
/// All encoders choose the short form when the payload length fits in a
/// single byte (≤ 255); otherwise the long form. The encoded constructor
/// + size prefix + payload is written into <paramref name="destination"/>
/// and the total byte count is returned via <c>written</c>.
/// </summary>
internal static class AmqpVariableWriter
{
    /// <summary>Threshold above which we switch from the 8-bit (0xA_)
    /// to the 32-bit (0xB_) form. Per spec the short form encodes a
    /// length in a single <see cref="byte"/>, so 255 is inclusive.</summary>
    private const int ShortFormCap = byte.MaxValue;

    // --- binary ----------------------------------------------------------

    public static void WriteBinary(Span<byte> destination, ReadOnlySpan<byte> payload, out int written)
    {
        if (payload.Length <= ShortFormCap)
        {
            destination[0] = AmqpFormatCode.Binary8;
            destination[1] = (byte)payload.Length;
            payload.CopyTo(destination[2..]);
            written = 2 + payload.Length;
            return;
        }
        destination[0] = AmqpFormatCode.Binary32;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)payload.Length);
        payload.CopyTo(destination[5..]);
        written = 5 + payload.Length;
    }

    // --- string (UTF-8) --------------------------------------------------

    public static void WriteString(Span<byte> destination, string value, out int written)
    {
        ArgumentNullException.ThrowIfNull(value);

        // We compute the UTF-8 byte count once and then encode in place to
        // avoid a second allocation. For short strings (most of the AMQP
        // wire), the stackalloc path keeps us allocation-free entirely.
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= ShortFormCap)
        {
            destination[0] = AmqpFormatCode.String8Utf8;
            destination[1] = (byte)byteCount;
            Encoding.UTF8.GetBytes(value, destination[2..(2 + byteCount)]);
            written = 2 + byteCount;
            return;
        }
        destination[0] = AmqpFormatCode.String32Utf8;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)byteCount);
        Encoding.UTF8.GetBytes(value, destination[5..(5 + byteCount)]);
        written = 5 + byteCount;
    }

    // --- symbol (ASCII) --------------------------------------------------

    /// <summary>
    /// Writes an AMQP symbol. Symbols are 7-bit ASCII (spec §1.6.21); any
    /// non-ASCII code point triggers an <see cref="ArgumentException"/>
    /// at encode time rather than producing a wire value the peer cannot
    /// decode.
    /// </summary>
    public static void WriteSymbol(Span<byte> destination, string value, out int written)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Symbols are bounded — even the longest descriptor we use
        // ("amqp:transfer:list") is ~32 bytes — so this hot path stays
        // on the stack.
        if (!IsAscii(value))
        {
            throw new ArgumentException("AMQP symbols must be 7-bit ASCII.", nameof(value));
        }

        var len = value.Length;
        if (len <= ShortFormCap)
        {
            destination[0] = AmqpFormatCode.Symbol8;
            destination[1] = (byte)len;
            for (var i = 0; i < len; i++) destination[2 + i] = (byte)value[i];
            written = 2 + len;
            return;
        }
        destination[0] = AmqpFormatCode.Symbol32;
        BinaryPrimitives.WriteUInt32BigEndian(destination[1..], (uint)len);
        for (var i = 0; i < len; i++) destination[5 + i] = (byte)value[i];
        written = 5 + len;
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F) return false;
        }
        return true;
    }
}
