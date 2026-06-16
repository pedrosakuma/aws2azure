using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Converts Cosmos DB binary JSON response bodies to canonical UTF-8 JSON.
/// The format marker is the first byte (<c>0x80</c>); the root JSON value
/// starts at offset 1. String-reference offsets are absolute from offset 0.
/// </summary>
internal static class CosmosBinaryDecoder
{
    private const int MaxDepth = 256;

    public static bool IsBinary(ReadOnlySpan<byte> body)
        => CosmosBinaryMarkers.IsBinary(body);

    /// <summary>Byte length of the value at <paramref name="offset"/>, bounds-
    /// validated against <paramref name="full"/> (depth 0). Shared with
    /// <see cref="CosmosBinaryReader"/> so the streaming walk uses the exact same
    /// length authority as the recursive decoder.</summary>
    internal static int ValueLength(ReadOnlySpan<byte> full, int offset)
        => CosmosBinaryMarkers.ValueLength(full, offset);

    public static void Decode(ReadOnlySpan<byte> body, IBufferWriter<byte> output)
        => Decode(body, output, default);

    public static void Decode(
        ReadOnlySpan<byte> body,
        IBufferWriter<byte> output,
        ReadOnlySpan<string?> userStringDictionary)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!IsBinary(body))
        {
            throw new JsonException("Cosmos response body is not binary JSON.");
        }
        if (body.Length < 2)
        {
            throw new JsonException("Cosmos binary JSON body is missing a root value.");
        }

        using var writer = new Utf8JsonWriter(output);
        WriteValue(writer, body, CosmosBinaryMarkers.RootOffset, userStringDictionary, depth: 0);
        writer.Flush();
    }

    private static void WriteValue(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> full,
        int offset,
        ReadOnlySpan<string?> dictionary,
        int depth)
    {
        EnsureDepth(depth);
        EnsureAvailable(full, offset, 1);
        byte marker = full[offset];

        switch (CosmosBinaryMarkers.GetValueKind(marker))
        {
            case CosmosBinaryValueKind.SmallNumber:
                writer.WriteNumberValue(marker);
                return;
            case CosmosBinaryValueKind.String:
                WriteStringToken(writer, full, offset, dictionary, propertyName: false, depth);
                return;
            case CosmosBinaryValueKind.Number:
                WriteNumberValue(writer, full, offset, marker);
                return;
            case CosmosBinaryValueKind.Null:
                writer.WriteNullValue();
                return;
            case CosmosBinaryValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case CosmosBinaryValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case CosmosBinaryValueKind.GuidString:
                EnsureAvailable(full, offset, 17);
                WriteGuidString(writer, full.Slice(offset + 1, 16), propertyName: false, upper: false, quoted: false);
                return;
            case CosmosBinaryValueKind.Binary:
                WriteBinaryValue(writer, full, offset, marker);
                return;
            case CosmosBinaryValueKind.Array:
                WriteArray(writer, full, offset, marker, dictionary, depth + 1);
                return;
            case CosmosBinaryValueKind.Object:
                WriteObject(writer, full, offset, marker, dictionary, depth + 1);
                return;
            case CosmosBinaryValueKind.UniformNumberArray:
                WriteUniformNumberArray(writer, full, offset, marker);
                return;
        }

        throw new JsonException($"Unsupported Cosmos binary JSON marker 0x{marker:X2}.");
    }

    private static void WriteArray(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> full,
        int offset,
        byte marker,
        ReadOnlySpan<string?> dictionary,
        int depth)
    {
        EnsureDepth(depth);
        writer.WriteStartArray();
        if (marker != 0xE0)
        {
            int dataStart = offset + CosmosBinaryMarkers.ContainerPrefixLength(marker);
            int dataEnd = offset + CosmosBinaryMarkers.ValueLength(full, offset, depth);
            EnsureBounds(full, dataStart, dataEnd);
            int current = dataStart;
            while (current < dataEnd)
            {
                // Validate the child fits inside the container's declared payload
                // BEFORE decoding it, so a malformed container cannot consume
                // bytes past dataEnd from the surrounding buffer.
                int childLength = CosmosBinaryMarkers.ValueLength(full, current, depth);
                if (childLength > dataEnd - current)
                {
                    throw new JsonException("Cosmos binary JSON array element exceeds container bounds.");
                }
                WriteValue(writer, full, current, dictionary, depth);
                current += childLength;
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteObject(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> full,
        int offset,
        byte marker,
        ReadOnlySpan<string?> dictionary,
        int depth)
    {
        EnsureDepth(depth);
        writer.WriteStartObject();
        if (marker != 0xE8)
        {
            int dataStart = offset + CosmosBinaryMarkers.ContainerPrefixLength(marker);
            int dataEnd = offset + CosmosBinaryMarkers.ValueLength(full, offset, depth);
            EnsureBounds(full, dataStart, dataEnd);
            int current = dataStart;
            while (current < dataEnd)
            {
                if (!CosmosBinaryMarkers.IsString(full[current]))
                {
                    throw new JsonException("Cosmos binary JSON object property name is not a string.");
                }
                // Bound the property name within the container payload first.
                int nameLength = CosmosBinaryMarkers.ValueLength(full, current, depth);
                if (nameLength > dataEnd - current)
                {
                    throw new JsonException("Cosmos binary JSON object property name exceeds container bounds.");
                }
                WriteStringToken(writer, full, current, dictionary, propertyName: true, depth);
                current += nameLength;

                if (current >= dataEnd)
                {
                    throw new JsonException("Cosmos binary JSON object property is missing its value.");
                }
                // Bound the property value within the remaining container payload.
                int valueLength = CosmosBinaryMarkers.ValueLength(full, current, depth);
                if (valueLength > dataEnd - current)
                {
                    throw new JsonException("Cosmos binary JSON object property value exceeds container bounds.");
                }
                WriteValue(writer, full, current, dictionary, depth);
                current += valueLength;
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteUniformNumberArray(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> full,
        int offset,
        byte marker)
    {
        writer.WriteStartArray();
        switch (marker)
        {
            case 0xF0:
            {
                EnsureAvailable(full, offset, 3);
                byte itemMarker = full[offset + 1];
                int count = full[offset + 2];
                WriteUniformNumbers(writer, full.Slice(offset + 3), itemMarker, count);
                break;
            }
            case 0xF1:
            {
                EnsureAvailable(full, offset, 4);
                byte itemMarker = full[offset + 1];
                int count = BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 2));
                WriteUniformNumbers(writer, full.Slice(offset + 4), itemMarker, count);
                break;
            }
            case 0xF2:
            {
                EnsureAvailable(full, offset, 5);
                byte nestedMarker = full[offset + 1];
                if (nestedMarker != 0xF0) throw new JsonException("Invalid nested uniform array marker.");
                byte itemMarker = full[offset + 2];
                int numberCount = full[offset + 3];
                int arrayCount = full[offset + 4];
                WriteUniformNumberArrays(writer, full.Slice(offset + 5), itemMarker, numberCount, arrayCount);
                break;
            }
            case 0xF3:
            {
                EnsureAvailable(full, offset, 7);
                byte nestedMarker = full[offset + 1];
                if (nestedMarker != 0xF1) throw new JsonException("Invalid nested uniform array marker.");
                byte itemMarker = full[offset + 2];
                int numberCount = BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 3));
                int arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 5));
                WriteUniformNumberArrays(writer, full.Slice(offset + 7), itemMarker, numberCount, arrayCount);
                break;
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteUniformNumberArrays(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> payload,
        byte itemMarker,
        int numberCount,
        int arrayCount)
    {
        int itemSize = CosmosBinaryMarkers.UniformNumberItemSize(itemMarker);
        int arraySize = checked(itemSize * numberCount);
        EnsureAvailable(payload, 0, checked(arraySize * arrayCount));
        for (int i = 0; i < arrayCount; i++)
        {
            writer.WriteStartArray();
            WriteUniformNumbers(writer, payload.Slice(i * arraySize, arraySize), itemMarker, numberCount);
            writer.WriteEndArray();
        }
    }

    private static void WriteUniformNumbers(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> payload,
        byte itemMarker,
        int count)
    {
        int itemSize = CosmosBinaryMarkers.UniformNumberItemSize(itemMarker);
        EnsureAvailable(payload, 0, checked(itemSize * count));
        for (int i = 0; i < count; i++)
        {
            WriteNumberPayload(writer, payload.Slice(i * itemSize, itemSize), itemMarker);
        }
    }

    private static void WriteNumberValue(Utf8JsonWriter writer, ReadOnlySpan<byte> full, int offset, byte marker)
    {
        int itemSize = CosmosBinaryMarkers.UniformNumberItemSize(marker);
        EnsureAvailable(full, offset + 1, itemSize);
        WriteNumberPayload(writer, full.Slice(offset + 1, itemSize), marker);
    }

    private static void WriteNumberPayload(Utf8JsonWriter writer, ReadOnlySpan<byte> payload, byte marker)
    {
        switch (marker)
        {
            case 0xC8:
            case 0xD7:
                writer.WriteNumberValue(payload[0]);
                return;
            case 0xD8:
                writer.WriteNumberValue((sbyte)payload[0]);
                return;
            case 0xC9:
            case 0xD9:
                writer.WriteNumberValue(BinaryPrimitives.ReadInt16LittleEndian(payload));
                return;
            case 0xCA:
            case 0xDA:
                writer.WriteNumberValue(BinaryPrimitives.ReadInt32LittleEndian(payload));
                return;
            case 0xCB:
            case 0xDB:
                writer.WriteNumberValue(BinaryPrimitives.ReadInt64LittleEndian(payload));
                return;
            case 0xC7:
                writer.WriteNumberValue(BinaryPrimitives.ReadUInt64LittleEndian(payload));
                return;
            case 0xDC:
                writer.WriteNumberValue(BinaryPrimitives.ReadUInt32LittleEndian(payload));
                return;
            case 0xCC:
            case 0xCE:
                writer.WriteNumberValue(BinaryPrimitives.ReadDoubleLittleEndian(payload));
                return;
            case 0xCD:
                writer.WriteNumberValue(BinaryPrimitives.ReadSingleLittleEndian(payload));
                return;
            case 0xCF:
                writer.WriteNumberValue((double)BinaryPrimitives.ReadHalfLittleEndian(payload));
                return;
            default:
                throw new JsonException($"Invalid uniform number item marker 0x{marker:X2}.");
        }
    }

    private static void WriteStringToken(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> full,
        int offset,
        ReadOnlySpan<string?> dictionary,
        bool propertyName,
        int depth)
    {
        EnsureDepth(depth);
        EnsureAvailable(full, offset, 1);
        byte marker = full[offset];

        if (marker is >= 0x20 and < 0x40)
        {
            WriteString(writer, CosmosBinaryMarkers.SystemString(marker - 0x20), propertyName);
            return;
        }

        if (marker is >= 0x40 and < 0x68)
        {
            if (marker >= 0x60)
            {
                EnsureAvailable(full, offset, 2);
            }
            int id = marker < 0x60
                ? marker - 0x40
                : 32 + full[offset + 1] + ((marker - 0x60) * 256);
            if ((uint)id >= (uint)dictionary.Length || dictionary[id] is not { } value)
            {
                throw new JsonException("Cosmos binary JSON body references an external user-string dictionary entry that was not supplied.");
            }
            WriteString(writer, value, propertyName);
            return;
        }

        if (marker is >= 0x80 and < 0xC0)
        {
            int length = marker - 0x80;
            EnsureAvailable(full, offset + 1, length);
            WriteUtf8String(writer, full.Slice(offset + 1, length), propertyName);
            return;
        }

        switch (marker)
        {
            case 0xC0:
            {
                EnsureAvailable(full, offset, 2);
                int length = full[offset + 1];
                EnsureAvailable(full, offset + 2, length);
                WriteUtf8String(writer, full.Slice(offset + 2, length), propertyName);
                return;
            }
            case 0xC1:
            {
                EnsureAvailable(full, offset, 3);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1));
                EnsureAvailable(full, offset + 3, length);
                WriteUtf8String(writer, full.Slice(offset + 3, length), propertyName);
                return;
            }
            case 0xC2:
            {
                EnsureAvailable(full, offset, 5);
                uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(offset + 1));
                if (rawLength > int.MaxValue) throw new JsonException("Cosmos binary JSON string is too large.");
                int length = (int)rawLength;
                EnsureAvailable(full, offset + 5, length);
                WriteUtf8String(writer, full.Slice(offset + 5, length), propertyName);
                return;
            }
            case 0xC3:
                EnsureAvailable(full, offset, 2);
                WriteStringToken(writer, full, full[offset + 1], dictionary, propertyName, depth + 1);
                return;
            case 0xC4:
                EnsureAvailable(full, offset, 3);
                WriteStringToken(writer, full, BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1)), dictionary, propertyName, depth + 1);
                return;
            case 0xC5:
                EnsureAvailable(full, offset, 4);
                WriteStringToken(writer, full, full[offset + 1] | (full[offset + 2] << 8) | (full[offset + 3] << 16), dictionary, propertyName, depth + 1);
                return;
            case 0xC6:
                EnsureAvailable(full, offset, 5);
                uint reference = BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(offset + 1));
                if (reference > int.MaxValue) throw new JsonException("Cosmos binary JSON string reference is too large.");
                WriteStringToken(writer, full, (int)reference, dictionary, propertyName, depth + 1);
                return;
            case 0x71:
            case 0x72:
            case 0x73:
            case 0x74:
                WriteString(writer, DecodeBase64(full, offset, CosmosBinaryMarkers.Base64LengthBytes(marker), CosmosBinaryMarkers.IsUrlBase64(marker)), propertyName);
                return;
            case 0x75:
                EnsureAvailable(full, offset, 17);
                WriteGuidString(writer, full.Slice(offset + 1, 16), propertyName, upper: false, quoted: false);
                return;
            case 0x76:
                EnsureAvailable(full, offset, 17);
                WriteGuidString(writer, full.Slice(offset + 1, 16), propertyName, upper: true, quoted: false);
                return;
            case 0x77:
                EnsureAvailable(full, offset, 17);
                WriteGuidString(writer, full.Slice(offset + 1, 16), propertyName, upper: false, quoted: true);
                return;
            case 0x78:
            case 0x79:
            case 0x7A:
                WriteString(writer, Decode4Bit(full, offset, CosmosBinaryMarkers.FourBitAlphabet(marker)), propertyName);
                return;
            case 0x7B:
            case 0x7C:
            case 0x7D:
            case 0x7E:
            case 0x7F:
                WriteString(
                    writer,
                    DecodePacked(
                        full,
                        offset,
                        CosmosBinaryMarkers.PackedStringBits(marker),
                        CosmosBinaryMarkers.PackedStringHasBaseChar(marker),
                        CosmosBinaryMarkers.PackedStringLengthBytes(marker)),
                    propertyName);
                return;
        }

        throw new JsonException($"Invalid Cosmos binary JSON string marker 0x{marker:X2}.");
    }

    private static void WriteBinaryValue(Utf8JsonWriter writer, ReadOnlySpan<byte> full, int offset, byte marker)
    {
        int prefix;
        int length;
        switch (marker)
        {
            case 0xDD:
                EnsureAvailable(full, offset, 2);
                prefix = 2;
                length = full[offset + 1];
                break;
            case 0xDE:
                EnsureAvailable(full, offset, 3);
                prefix = 3;
                length = BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1));
                break;
            default:
                EnsureAvailable(full, offset, 5);
                prefix = 5;
                uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(offset + 1));
                if (rawLength > int.MaxValue) throw new JsonException("Cosmos binary JSON blob is too large.");
                length = (int)rawLength;
                break;
        }
        EnsureAvailable(full, offset + prefix, length);
        writer.WriteBase64StringValue(full.Slice(offset + prefix, length));
    }

    private static void WriteUtf8String(Utf8JsonWriter writer, ReadOnlySpan<byte> utf8, bool propertyName)
    {
        if (propertyName)
        {
            writer.WritePropertyName(utf8);
        }
        else
        {
            writer.WriteStringValue(utf8);
        }
    }

    private static void WriteString(Utf8JsonWriter writer, string value, bool propertyName)
    {
        if (propertyName)
        {
            writer.WritePropertyName(value);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static void WriteGuidString(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> guidBytes,
        bool propertyName,
        bool upper,
        bool quoted)
    {
        string hex = upper ? CosmosBinaryMarkers.UppercaseHex : CosmosBinaryMarkers.LowercaseHex;
        Span<char> chars = stackalloc char[quoted ? 38 : 36];
        int ci = 0;
        if (quoted) chars[ci++] = '"';
        for (int i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10) chars[ci++] = '-';
            byte b = guidBytes[i];
            chars[ci++] = hex[b & 0x0F];
            chars[ci++] = hex[b >> 4];
        }
        if (quoted) chars[ci] = '"';

        if (propertyName)
        {
            writer.WritePropertyName(chars);
        }
        else
        {
            writer.WriteStringValue(chars);
        }
    }

    private static string DecodeBase64(ReadOnlySpan<byte> full, int offset, int lengthBytes, bool url)
    {
        EnsureAvailable(full, offset, 1 + lengthBytes + 1);
        int lengthDiv4 = lengthBytes == 1
            ? full[offset + 1]
            : BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1));
        byte padding = full[offset + 1 + lengthBytes];
        int prefix = 1 + lengthBytes + 1;
        int byteCount = checked((lengthDiv4 * 4 - GetBase64Padding(padding)) * 3 / 4);
        EnsureAvailable(full, offset + prefix, byteCount);

        string value = Convert.ToBase64String(full.Slice(offset + prefix, byteCount));
        int outputLength = lengthDiv4 * 4;
        if (padding > 2)
        {
            outputLength -= GetBase64Padding(padding);
        }
        if (url)
        {
            value = value.Replace('+', '-').Replace('/', '_');
        }
        return value.Length > outputLength ? value[..outputLength] : value;
    }

    private static byte GetBase64Padding(byte padding)
        => padding > 2 ? (byte)~padding : padding;

    private static string Decode4Bit(ReadOnlySpan<byte> full, int offset, string alphabet)
    {
        EnsureAvailable(full, offset, 2);
        int charCount = full[offset + 1];
        int byteCount = (charCount * 4 + 7) / 8;
        EnsureAvailable(full, offset + 2, byteCount);
        ReadOnlySpan<byte> encoded = full.Slice(offset + 2, byteCount);
        Span<char> chars = charCount <= 256 ? stackalloc char[charCount] : new char[charCount];
        for (int i = 0; i < charCount; i++)
        {
            byte b = encoded[i / 2];
            chars[i] = alphabet[(i & 1) == 0 ? b & 0x0F : b >> 4];
        }
        return new string(chars);
    }

    private static string DecodePacked(
        ReadOnlySpan<byte> full,
        int offset,
        int bits,
        bool hasBaseChar,
        int lenBytes)
    {
        EnsureAvailable(full, offset, 1 + lenBytes + (hasBaseChar ? 1 : 0));
        int charCount = lenBytes == 1
            ? full[offset + 1]
            : BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1));
        byte baseChar = hasBaseChar ? full[offset + 1 + lenBytes] : (byte)0;
        int prefix = 1 + lenBytes + (hasBaseChar ? 1 : 0);
        int byteCount = (charCount * bits + 7) / 8;
        EnsureAvailable(full, offset + prefix, byteCount);

        ReadOnlySpan<byte> encoded = full.Slice(offset + prefix, byteCount);
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(charCount, 1));
        try
        {
            Span<byte> output = rented.AsSpan(0, charCount);
            long mask = 0xFF >> (8 - bits);
            Span<byte> packed = stackalloc byte[8];
            int produced = 0;
            int sourceOffset = 0;
            while (produced < charCount)
            {
                packed.Clear();
                int available = Math.Min(bits, encoded.Length - sourceOffset);
                encoded.Slice(sourceOffset, available).CopyTo(packed);
                long value = BinaryPrimitives.ReadInt64LittleEndian(packed);
                int take = Math.Min(8, charCount - produced);
                for (int i = 0; i < take; i++)
                {
                    output[produced++] = (byte)((value & mask) + baseChar);
                    value >>= bits;
                }
                sourceOffset += bits;
            }
            return Encoding.ASCII.GetString(output);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void EnsureDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new JsonException("Cosmos binary JSON nesting exceeds the supported maximum depth.");
        }
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> span, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > span.Length - length)
        {
            throw new JsonException("Cosmos binary JSON body is truncated or malformed.");
        }
    }

    private static void EnsureBounds(ReadOnlySpan<byte> span, int start, int end)
    {
        if (start < 0 || end < start || end > span.Length)
        {
            throw new JsonException("Cosmos binary JSON container length is invalid.");
        }
    }


}
