using System.Buffers.Binary;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Computes the encoded length of any AMQP 1.0 value starting at offset
/// zero of <paramref name="source"/> without decoding its payload. Used
/// by the performative reader to capture opaque field slices (source,
/// target, error, delivery-state, properties maps, capability arrays)
/// whose typed representation is the responsibility of later slices.
/// </summary>
/// <remarks>
/// The high-nibble dispatch mirrors the table in
/// <see cref="AmqpFormatCode"/>. Described types (0x00) recursively
/// measure the descriptor and value. The function never allocates and
/// never reads beyond the length it returns.
/// </remarks>
internal static class AmqpValueScanner
{
    public static int Measure(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            throw new InvalidDataException("Cannot measure an empty AMQP value.");
        }
        var code = source[0];
        if (code == AmqpFormatCode.Described)
        {
            // 0x00 + descriptor value + value value.
            var descLen = Measure(source[1..]);
            var valLen = Measure(source[(1 + descLen)..]);
            return 1 + descLen + valLen;
        }
        switch (code >> 4)
        {
            case 0x4: return 1;
            case 0x5: return 2;
            case 0x6: return 3;
            case 0x7: return 5;
            case 0x8: return 9;
            case 0x9: return 17;
            case 0xA:
            case 0xC:
            case 0xE:
                EnsureLength(source, 2);
                return 2 + source[1];
            case 0xB:
            case 0xD:
            case 0xF:
                EnsureLength(source, 5);
                var size = BinaryPrimitives.ReadUInt32BigEndian(source[1..]);
                if (size > int.MaxValue - 5)
                {
                    throw new InvalidDataException("AMQP value size exceeds Int32 capacity.");
                }
                return 5 + (int)size;
            default:
                throw new InvalidDataException(
                    $"Unknown AMQP format code high nibble 0x{(code >> 4):X1} (full code 0x{code:X2}).");
        }
    }

    private static void EnsureLength(ReadOnlySpan<byte> source, int minimum)
    {
        if (source.Length < minimum)
        {
            throw new InvalidDataException(
                $"Truncated AMQP value: need {minimum} bytes for size prefix, got {source.Length}.");
        }
    }
}
