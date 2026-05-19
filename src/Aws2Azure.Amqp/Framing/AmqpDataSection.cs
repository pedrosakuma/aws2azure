using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 <c>data</c> body section (§3.2.7, descriptor 0x75). A
/// described binary that carries opaque message payload bytes.
/// </summary>
internal static class AmqpDataSection
{
    public const ulong Descriptor = MessageSectionDescriptor.Data;

    public static void Write(Span<byte> destination, ReadOnlySpan<byte> payload, out int written)
    {
        int w = 0;
        destination[w++] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(destination[w..], Descriptor, out var descLen);
        w += descLen;
        // Encode payload as binary (binary8 if it fits in 255 bytes, binary32 otherwise).
        if (payload.Length <= 0xFF)
        {
            destination[w++] = AmqpFormatCode.Binary8;
            destination[w++] = (byte)payload.Length;
            payload.CopyTo(destination[w..]);
            w += payload.Length;
        }
        else
        {
            destination[w++] = AmqpFormatCode.Binary32;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(destination[w..], (uint)payload.Length);
            w += 4;
            payload.CopyTo(destination[w..]);
            w += payload.Length;
        }
        written = w;
    }

    public static ReadOnlyMemory<byte> Read(ReadOnlyMemory<byte> source, out int consumed)
    {
        var span = source.Span;
        var offset = AmqpCompoundReader.ReadDescribedHeader(span);
        var descriptor = AmqpPrimitiveReader.ReadULong(span[offset..], out var descLen);
        if (descriptor != Descriptor)
            throw new InvalidDataException(
                $"Expected data section descriptor 0x{Descriptor:X16}, got 0x{descriptor:X16}.");
        offset += descLen;

        var code = span[offset];
        int payloadStart, payloadLen;
        if (code == AmqpFormatCode.Binary8)
        {
            payloadLen = span[offset + 1];
            payloadStart = offset + 2;
        }
        else if (code == AmqpFormatCode.Binary32)
        {
            payloadLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span[(offset + 1)..]);
            payloadStart = offset + 5;
        }
        else
        {
            throw new InvalidDataException($"Expected binary constructor for data section, got 0x{code:X2}.");
        }

        consumed = payloadStart + payloadLen;
        return source.Slice(payloadStart, payloadLen);
    }
}
