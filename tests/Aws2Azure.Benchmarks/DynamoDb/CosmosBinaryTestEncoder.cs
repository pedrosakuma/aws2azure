using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb;

/// <summary>
/// Test-only CosmosBinary encoder used to fabricate the binary input the
/// benchmark feeds to the production <c>CosmosBinaryDecoder</c>. The proxy
/// never encodes CosmosBinary in production (it only requests and decodes it),
/// so this lives in the benchmark project as a fixture, not shipped code.
/// Encoding happens during <c>[GlobalSetup]</c>, outside the measured window.
/// Mirrors <c>CosmosBinaryTestEncoder</c> in the unit-test suite.
/// </summary>
internal static class CosmosBinaryTestEncoder
{
    public static byte[] Encode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var writer = new ArrayBufferWriter<byte>();
        WriteByte(writer, 0x80);
        WriteElement(doc.RootElement, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteElement(JsonElement element, ArrayBufferWriter<byte> writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, writer);
                break;
            case JsonValueKind.Array:
                WriteArray(element, writer);
                break;
            case JsonValueKind.String:
                WriteString(element.GetString()!, writer);
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt32(out int intValue))
                {
                    WriteByte(writer, 0xDA);
                    Span<byte> bytes = writer.GetSpan(4);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, intValue);
                    writer.Advance(4);
                }
                else
                {
                    WriteByte(writer, 0xCC);
                    Span<byte> bytes = writer.GetSpan(8);
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes, element.GetDouble());
                    writer.Advance(8);
                }

                break;
            case JsonValueKind.True:
                WriteByte(writer, 0xD2);
                break;
            case JsonValueKind.False:
                WriteByte(writer, 0xD1);
                break;
            case JsonValueKind.Null:
                WriteByte(writer, 0xD0);
                break;
        }
    }

    private static void WriteObject(JsonElement element, ArrayBufferWriter<byte> writer)
    {
        var payload = new ArrayBufferWriter<byte>();
        int count = 0;
        foreach (var property in element.EnumerateObject())
        {
            WriteString(property.Name, payload);
            WriteElement(property.Value, payload);
            count++;
        }

        WriteContainer(writer, payload.WrittenSpan, count, obj: true);
    }

    private static void WriteArray(JsonElement element, ArrayBufferWriter<byte> writer)
    {
        var payload = new ArrayBufferWriter<byte>();
        int count = 0;
        foreach (var item in element.EnumerateArray())
        {
            WriteElement(item, payload);
            count++;
        }

        WriteContainer(writer, payload.WrittenSpan, count, obj: false);
    }

    private static void WriteContainer(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> payload, int count, bool obj)
    {
        if (payload.Length <= byte.MaxValue && count <= byte.MaxValue)
        {
            // LC1: marker + 1-byte length + 1-byte count (0xE5 array / 0xED object).
            WriteByte(writer, obj ? (byte)0xED : (byte)0xE5);
            WriteByte(writer, (byte)payload.Length);
            WriteByte(writer, (byte)count);
        }
        else if (payload.Length <= ushort.MaxValue && count <= ushort.MaxValue)
        {
            // LC2: marker + 2-byte length + 2-byte count (0xE6 array / 0xEE object).
            WriteByte(writer, obj ? (byte)0xEE : (byte)0xE6);
            Span<byte> prefix = writer.GetSpan(4);
            BinaryPrimitives.WriteUInt16LittleEndian(prefix, (ushort)payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(prefix.Slice(2), (ushort)count);
            writer.Advance(4);
        }
        else
        {
            // LC4: marker + 4-byte length + 4-byte count (0xE7 array / 0xEF object).
            // Required once a container payload exceeds 64 KiB.
            WriteByte(writer, obj ? (byte)0xEF : (byte)0xE7);
            Span<byte> prefix = writer.GetSpan(8);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.Slice(4), (uint)count);
            writer.Advance(8);
        }

        writer.Write(payload);
    }

    private static void WriteString(string value, ArrayBufferWriter<byte> writer)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length < 64)
        {
            WriteByte(writer, (byte)(0x80 + utf8.Length));
        }
        else
        {
            WriteByte(writer, 0xC1);
            Span<byte> length = writer.GetSpan(2);
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)utf8.Length));
            writer.Advance(2);
        }

        writer.Write(utf8);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }
}
