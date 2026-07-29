using Aws2Azure.Amqp.Codec;
using Aws2Azure.Core.Buffers;
using System.Text;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 <c>application-properties</c> message section (§3.2.5,
/// descriptor 0x74). A described map whose keys are strings and whose
/// values are AMQP primitive types. Slice 5c models the subset CBS
/// uses: <see cref="string"/>, <see cref="bool"/>, 32/64-bit integers,
/// <see cref="uint"/>, and <see cref="double"/> values.
/// Other value types are surfaced as <c>null</c> on read so a peer
/// extension won't crash the decoder.
/// </summary>
internal static class AmqpApplicationProperties
{
    public const ulong Descriptor = MessageSectionDescriptor.ApplicationProperties;

    /// <summary>
    /// Writes a described map. <paramref name="pairs"/> values must be
    /// <see cref="string"/>, boxed <see cref="bool"/>, boxed integers,
    /// boxed <see cref="uint"/>, or boxed <see cref="double"/>. Unsupported types throw
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static void Write(
        Span<byte> destination,
        IReadOnlyCollection<KeyValuePair<string, object?>> pairs,
        out int written)
    {
        using var body = new PooledByteBufferWriter(Math.Max(256, pairs.Count * 64));
        foreach (var kv in pairs)
        {
            var keySpan = body.GetSpan(Math.Max(8, kv.Key.Length * 4));
            AmqpVariableWriter.WriteString(keySpan, kv.Key, out var keyLength);
            body.Advance(keyLength);

            var valueSpan = body.GetSpan(GetValueSizeHint(kv.Value));
            WriteValue(valueSpan, kv.Value, out var valueLength);
            body.Advance(valueLength);
        }

        using var mapBytes = new PooledByteBufferWriter(Math.Max(256, body.WrittenMemory.Length + 16));
        var mapSpan = mapBytes.GetSpan(body.WrittenMemory.Length + 16);
        AmqpCompoundWriter.WriteMap(mapSpan, body.WrittenMemory.Span, pairs.Count, out var mapLen);
        mapBytes.Advance(mapLen);

        // Encode the described type manually: 0x00 + descriptor (ulong) + map.
        int w = 0;
        destination[w++] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(destination[w..], Descriptor, out var descLen);
        w += descLen;
        mapBytes.WrittenMemory.Span.CopyTo(destination[w..]);
        w += mapLen;
        written = w;
    }

    private static int GetValueSizeHint(object? value)
        => value switch
        {
            null => 1,
            string s => Math.Max(8, Encoding.UTF8.GetByteCount(s) + 5),
            bool => 2,
            int => 5,
            uint => 5,
            long => 9,
            double => 9,
            _ => 32,
        };

    /// <summary>
    /// Reads a described map. Returns a dictionary with string and int
    /// values; other primitive types yield a <c>null</c> entry value.
    /// </summary>
    public static Dictionary<string, object?> Read(ReadOnlyMemory<byte> source, out int consumed)
    {
        var span = source.Span;
        var offset = AmqpCompoundReader.ReadDescribedHeader(span);
        var descriptor = AmqpPrimitiveReader.ReadULong(span[offset..], out var descLen);
        if (descriptor != Descriptor)
            throw new InvalidDataException(
                $"Expected application-properties descriptor 0x{Descriptor:X16}, got 0x{descriptor:X16}.");
        offset += descLen;

        var view = AmqpCompoundReader.ReadMap(span[offset..], out var mapLen);
        consumed = offset + mapLen;

        var els = view.Elements;
        var pairCount = view.Count / 2;
        var dict = new Dictionary<string, object?>(pairCount, StringComparer.Ordinal);
        int o = 0;
        for (int i = 0; i < pairCount; i++)
        {
            var key = AmqpVariableReader.ReadString(els[o..], out var kl); o += kl;
            var value = ReadValue(els, ref o);
            dict[key] = value;
        }
        return dict;
    }

    private static void WriteValue(Span<byte> destination, object? value, out int written)
    {
        switch (value)
        {
            case null:
                PerformativeCodec.WriteNullField(destination, out written);
                return;
            case string s:
                AmqpVariableWriter.WriteString(destination, s, out written);
                return;
            case bool b:
                AmqpPrimitiveWriter.WriteBoolean(destination, b, out written);
                return;
            case int i:
                AmqpPrimitiveWriter.WriteInt(destination, i, out written);
                return;
            case long l:
                AmqpPrimitiveWriter.WriteLong(destination, l, out written);
                return;
            case uint u:
                AmqpPrimitiveWriter.WriteUInt(destination, u, out written);
                return;
            case double d:
                AmqpPrimitiveWriter.WriteDouble(destination, d, out written);
                return;
            default:
                throw new ArgumentException(
                    $"Unsupported application-property value type {value.GetType()}.");
        }
    }

    private static object? ReadValue(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        switch (fc)
        {
            case AmqpFormatCode.String8Utf8:
            case AmqpFormatCode.String32Utf8:
            {
                var s = AmqpVariableReader.ReadString(els[o..], out var len);
                o += len;
                return s;
            }
            case AmqpFormatCode.Int:
            case 0x54: // smallint
            {
                var n = AmqpPrimitiveReader.ReadInt(els[o..], out var len);
                o += len;
                return n;
            }
            case AmqpFormatCode.Boolean:
            case AmqpFormatCode.BooleanTrue:
            case AmqpFormatCode.BooleanFalse:
            {
                var b = AmqpPrimitiveReader.ReadBoolean(els[o..], out var len);
                o += len;
                return b;
            }
            case AmqpFormatCode.Short:
            {
                var n = AmqpPrimitiveReader.ReadShort(els[o..], out var len);
                o += len;
                return (int)n;
            }
            case AmqpFormatCode.UShort:
            {
                var n = AmqpPrimitiveReader.ReadUShort(els[o..], out var len);
                o += len;
                return (int)n;
            }
            case AmqpFormatCode.UInt:
            case AmqpFormatCode.UIntSmall:
            case AmqpFormatCode.UInt0:
            {
                var n = AmqpPrimitiveReader.ReadUInt(els[o..], out var len);
                o += len;
                return n;
            }
            case AmqpFormatCode.Long:
            case 0x55: // smalllong
            {
                var n = AmqpPrimitiveReader.ReadLong(els[o..], out var len);
                o += len;
                return n;
            }
            case AmqpFormatCode.Double:
            {
                var d = AmqpPrimitiveReader.ReadDouble(els[o..], out var len);
                o += len;
                return d;
            }
            default:
            {
                var len = AmqpValueScanner.Measure(els[o..]);
                o += len;
                return null;
            }
        }
    }
}
