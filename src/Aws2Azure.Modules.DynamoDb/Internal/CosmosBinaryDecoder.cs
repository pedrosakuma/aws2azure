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
    private const byte BinaryFormatMarker = 0x80;
    private const int MaxDepth = 256;

    private const int L1 = -1, L2 = -2, L4 = -3, LC1 = -4, LC2 = -5, LC4 = -6;
    private const int CS4L1 = -7, CS7L1 = -8, CS7L2 = -9;
    private const int CS4BL1 = -10, CS5BL1 = -11, CS6BL1 = -12;
    private const int B64L1 = -13, B64L2 = -14;
    private const int Arr1 = -15, Obj1 = -16;
    private const int NC1 = -17, NC2 = -18, ANC1 = -19, ANC2 = -20;

    private static readonly int[] Lookup = BuildLookup();

    private static readonly string[] SystemStrings =
    [
        "$s", "$t", "$v", "_attachments", "_etag", "_rid", "_self", "_ts",
        "attachments/", "coordinates", "geometry", "GeometryCollection", "id",
        "url", "Value", "label", "LineString", "link", "MultiLineString",
        "MultiPoint", "MultiPolygon", "name", "Name", "Type", "Point", "Polygon",
        "properties", "type", "value", "Feature", "FeatureCollection", "_id",
    ];

    private const string LowercaseHex = "0123456789abcdef";
    private const string UppercaseHex = "0123456789ABCDEF";
    private const string DateTimeChars = " 0123456789:-.TZ";

    public static bool IsBinary(ReadOnlySpan<byte> body)
        => body.Length > 0 && body[0] == BinaryFormatMarker;

    /// <summary>The first data byte (root value) offset, immediately after the
    /// <c>0x80</c> binary-format marker. Shared with the streaming
    /// <see cref="CosmosBinaryReader"/>.</summary>
    internal const int RootOffset = 1;

    /// <summary>System-string dictionary as UTF-8 (all entries are ASCII), so the
    /// streaming reader can surface a dictionary-referenced name/value without a
    /// per-token <see cref="string"/> allocation. Index range is [0, 32).</summary>
    private static readonly byte[][] SystemStringsUtf8 = BuildSystemStringsUtf8();

    private static byte[][] BuildSystemStringsUtf8()
    {
        var table = new byte[SystemStrings.Length][];
        for (int i = 0; i < SystemStrings.Length; i++)
        {
            table[i] = Encoding.UTF8.GetBytes(SystemStrings[i]);
        }
        return table;
    }

    internal static ReadOnlySpan<byte> SystemStringUtf8(int index) => SystemStringsUtf8[index];

    internal static bool IsStringMarkerInternal(byte marker) => IsStringMarker(marker);

    internal static int ContainerPrefixLengthInternal(byte marker) => ContainerPrefixLength(marker);

    internal static int UniformNumberItemSizeInternal(byte marker) => UniformNumberItemSize(marker);

    /// <summary>Byte length of the value at <paramref name="offset"/>, bounds-
    /// validated against <paramref name="full"/> (depth 0). Shared with
    /// <see cref="CosmosBinaryReader"/> so the streaming walk uses the exact same
    /// length authority as the recursive decoder.</summary>
    internal static int ValueLength(ReadOnlySpan<byte> full, int offset)
        => BoundedLength(full, offset, 0);

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
        WriteValue(writer, body, 1, userStringDictionary, depth: 0);
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

        if (marker < 0x20)
        {
            writer.WriteNumberValue(marker);
            return;
        }

        if (IsStringMarker(marker))
        {
            WriteStringToken(writer, full, offset, dictionary, propertyName: false, depth);
            return;
        }

        switch (marker)
        {
            case 0xC7:
                EnsureAvailable(full, offset, 9);
                writer.WriteNumberValue(BinaryPrimitives.ReadUInt64LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xC8:
                EnsureAvailable(full, offset, 2);
                writer.WriteNumberValue(full[offset + 1]);
                return;
            case 0xC9:
                EnsureAvailable(full, offset, 3);
                writer.WriteNumberValue(BinaryPrimitives.ReadInt16LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCA:
                EnsureAvailable(full, offset, 5);
                writer.WriteNumberValue(BinaryPrimitives.ReadInt32LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCB:
                EnsureAvailable(full, offset, 9);
                writer.WriteNumberValue(BinaryPrimitives.ReadInt64LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCC:
            case 0xCE:
                EnsureAvailable(full, offset, 9);
                writer.WriteNumberValue(BinaryPrimitives.ReadDoubleLittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCD:
                EnsureAvailable(full, offset, 5);
                writer.WriteNumberValue(BinaryPrimitives.ReadSingleLittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCF:
                EnsureAvailable(full, offset, 3);
                writer.WriteNumberValue((double)BinaryPrimitives.ReadHalfLittleEndian(full.Slice(offset + 1)));
                return;
            case 0xD0:
                writer.WriteNullValue();
                return;
            case 0xD1:
                writer.WriteBooleanValue(false);
                return;
            case 0xD2:
                writer.WriteBooleanValue(true);
                return;
            case 0xD3:
                EnsureAvailable(full, offset, 17);
                WriteGuidString(writer, full.Slice(offset + 1, 16), propertyName: false, upper: false, quoted: false);
                return;
            case 0xD7:
                EnsureAvailable(full, offset, 2);
                writer.WriteNumberValue(full[offset + 1]);
                return;
            case 0xD8:
                EnsureAvailable(full, offset, 2);
                writer.WriteNumberValue((sbyte)full[offset + 1]);
                return;
            case 0xD9:
                EnsureAvailable(full, offset, 3);
                writer.WriteNumberValue(BinaryPrimitives.ReadInt16LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xDA:
                EnsureAvailable(full, offset, 5);
                writer.WriteNumberValue(BinaryPrimitives.ReadInt32LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xDB:
                EnsureAvailable(full, offset, 9);
                writer.WriteNumberValue(BinaryPrimitives.ReadInt64LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xDC:
                EnsureAvailable(full, offset, 5);
                writer.WriteNumberValue(BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xDD:
            case 0xDE:
            case 0xDF:
                WriteBinaryValue(writer, full, offset, marker);
                return;
        }

        if (marker is >= 0xE0 and <= 0xE7)
        {
            WriteArray(writer, full, offset, marker, dictionary, depth + 1);
            return;
        }

        if (marker is >= 0xE8 and <= 0xEF)
        {
            WriteObject(writer, full, offset, marker, dictionary, depth + 1);
            return;
        }

        if (marker is >= 0xF0 and <= 0xF3)
        {
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
            int dataStart = offset + ContainerPrefixLength(marker);
            int dataEnd = offset + BoundedLength(full, offset, depth);
            EnsureBounds(full, dataStart, dataEnd);
            int current = dataStart;
            while (current < dataEnd)
            {
                // Validate the child fits inside the container's declared payload
                // BEFORE decoding it, so a malformed container cannot consume
                // bytes past dataEnd from the surrounding buffer.
                int childLength = BoundedLength(full, current, depth);
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
            int dataStart = offset + ContainerPrefixLength(marker);
            int dataEnd = offset + BoundedLength(full, offset, depth);
            EnsureBounds(full, dataStart, dataEnd);
            int current = dataStart;
            while (current < dataEnd)
            {
                if (!IsStringMarker(full[current]))
                {
                    throw new JsonException("Cosmos binary JSON object property name is not a string.");
                }
                // Bound the property name within the container payload first.
                int nameLength = BoundedLength(full, current, depth);
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
                int valueLength = BoundedLength(full, current, depth);
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
        int itemSize = UniformNumberItemSize(itemMarker);
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
        int itemSize = UniformNumberItemSize(itemMarker);
        EnsureAvailable(payload, 0, checked(itemSize * count));
        for (int i = 0; i < count; i++)
        {
            WriteNumberPayload(writer, payload.Slice(i * itemSize, itemSize), itemMarker);
        }
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

    private static int UniformNumberItemSize(byte marker)
    {
        int length = Lookup[marker];
        if (length <= 1)
        {
            throw new JsonException($"Invalid uniform number item marker 0x{marker:X2}.");
        }
        return length - 1;
    }

    private static bool IsStringMarker(byte marker)
        => marker is >= 0x20 and < 0x68
            || marker is >= 0x71 and < 0x80
            || marker is >= 0x80 and <= 0xC6;

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
            WriteString(writer, SystemStrings[marker - 0x20], propertyName);
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
                WriteString(writer, DecodeBase64(full, offset, marker is 0x71 or 0x73 ? 1 : 2, marker is 0x73 or 0x74), propertyName);
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
                WriteString(writer, Decode4Bit(full, offset, LowercaseHex), propertyName);
                return;
            case 0x79:
                WriteString(writer, Decode4Bit(full, offset, UppercaseHex), propertyName);
                return;
            case 0x7A:
                WriteString(writer, Decode4Bit(full, offset, DateTimeChars), propertyName);
                return;
            case 0x7B:
                WriteString(writer, DecodePacked(full, offset, bits: 4, hasBaseChar: true, lenBytes: 1), propertyName);
                return;
            case 0x7C:
                WriteString(writer, DecodePacked(full, offset, bits: 5, hasBaseChar: true, lenBytes: 1), propertyName);
                return;
            case 0x7D:
                WriteString(writer, DecodePacked(full, offset, bits: 6, hasBaseChar: true, lenBytes: 1), propertyName);
                return;
            case 0x7E:
                WriteString(writer, DecodePacked(full, offset, bits: 7, hasBaseChar: false, lenBytes: 1), propertyName);
                return;
            case 0x7F:
                WriteString(writer, DecodePacked(full, offset, bits: 7, hasBaseChar: false, lenBytes: 2), propertyName);
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
        string hex = upper ? UppercaseHex : LowercaseHex;
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

    private static int ContainerPrefixLength(byte marker) => marker switch
    {
        0xE1 or 0xE9 => 1,
        0xE2 or 0xEA => 2,
        0xE3 or 0xEB => 3,
        0xE4 or 0xEC => 5,
        0xE5 or 0xED => 3,
        0xE6 or 0xEE => 5,
        0xE7 or 0xEF => 9,
        _ => throw new JsonException($"Invalid Cosmos binary JSON container marker 0x{marker:X2}."),
    };

    private static long GetValueLength(ReadOnlySpan<byte> full, int offset, int depth)
    {
        EnsureDepth(depth);
        EnsureAvailable(full, offset, 1);
        ReadOnlySpan<byte> buffer = full.Slice(offset);
        long length = Lookup[buffer[0]];
        if (length > 0)
        {
            return length;
        }
        if (length == 0)
        {
            throw new JsonException($"Invalid Cosmos binary JSON marker 0x{buffer[0]:X2}.");
        }

        switch (length)
        {
            case L1:
                EnsureAvailable(buffer, 0, 2);
                return 2L + buffer[1];
            case L2:
                EnsureAvailable(buffer, 0, 3);
                return 3L + BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));
            case L4:
                EnsureAvailable(buffer, 0, 5);
                return 5L + BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(1));
            case LC1:
                EnsureAvailable(buffer, 0, 3);
                return 3L + buffer[1];
            case LC2:
                EnsureAvailable(buffer, 0, 5);
                return 5L + BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));
            case LC4:
                EnsureAvailable(buffer, 0, 9);
                return 9L + BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(1));
            case Arr1:
            {
                // Single-item array: 1 marker byte + the (validated) item. Routed
                // through BoundedLength at depth+1 so a chain of 0xE1 markers
                // cannot recurse past MaxDepth (no stack overflow) and an
                // out-of-range item length surfaces as JsonException.
                int item = BoundedLength(full, offset + 1, depth + 1);
                return 1L + item;
            }
            case Obj1:
            {
                // Single-property object: 1 marker byte + name + value, each
                // bounded against the buffer at depth+1.
                int name = BoundedLength(full, offset + 1, depth + 1);
                int value = BoundedLength(full, offset + 1 + name, depth + 1);
                return 1L + name + value;
            }
            case B64L1:
                EnsureAvailable(buffer, 0, 3);
                return 3L + GetBase64ByteCount(buffer[1], buffer[2]);
            case B64L2:
                EnsureAvailable(buffer, 0, 4);
                return 4L + GetBase64ByteCount(BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1)), buffer[3]);
            case CS4L1:
                EnsureAvailable(buffer, 0, 2);
                return 2L + CompressedLength(buffer[1], 4);
            case CS7L1:
                EnsureAvailable(buffer, 0, 2);
                return 2L + CompressedLength(buffer[1], 7);
            case CS7L2:
                EnsureAvailable(buffer, 0, 3);
                return 3L + CompressedLength(BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1)), 7);
            case CS4BL1:
                EnsureAvailable(buffer, 0, 3);
                return 3L + CompressedLength(buffer[1], 4);
            case CS5BL1:
                EnsureAvailable(buffer, 0, 3);
                return 3L + CompressedLength(buffer[1], 5);
            case CS6BL1:
                EnsureAvailable(buffer, 0, 3);
                return 3L + CompressedLength(buffer[1], 6);
            case NC1:
                EnsureAvailable(buffer, 0, 3);
                return 3L + (UniformNumberItemSize(buffer[1]) * buffer[2]);
            case NC2:
                EnsureAvailable(buffer, 0, 4);
                return 4L + (UniformNumberItemSize(buffer[1]) * BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2)));
            case ANC1:
                EnsureAvailable(buffer, 0, 5);
                return 5L + (UniformNumberItemSize(buffer[2]) * buffer[3] * buffer[4]);
            case ANC2:
                EnsureAvailable(buffer, 0, 7);
                return 7L + (UniformNumberItemSize(buffer[2]) * BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(3)) * BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(5)));
            default:
                throw new JsonException($"Invalid Cosmos binary JSON length sentinel {length}.");
        }
    }

    /// <summary>
    /// Returns the byte length of the value at <paramref name="offset"/>,
    /// validated to be strictly positive and to fit inside the remaining buffer.
    /// Converts oversized/overflowing length fields (e.g. an <c>ArrL4</c>
    /// claiming <c>0xFFFFFFFF</c> bytes) into a <see cref="JsonException"/>
    /// rather than an <see cref="OverflowException"/>, and guarantees the
    /// returned <see cref="int"/> can be added to <paramref name="offset"/>
    /// without overflow (since the buffer length is itself an <see cref="int"/>).
    /// </summary>
    private static int BoundedLength(ReadOnlySpan<byte> full, int offset, int depth)
    {
        long length = GetValueLength(full, offset, depth);
        if (length <= 0 || offset < 0 || offset > full.Length || length > full.Length - offset)
        {
            throw new JsonException("Cosmos binary JSON value length is out of range.");
        }

        return (int)length;
    }

    private static int CompressedLength(int length, int bits)
        => (length * bits + 7) / 8;

    private static int GetBase64ByteCount(int lengthDiv4, byte padding)
        => (lengthDiv4 * 4 - GetBase64Padding(padding)) * 3 / 4;

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

    private static int[] BuildLookup()
    {
        var table = new int[256];
        for (int i = 0x00; i < 0x60; i++) table[i] = 1;
        for (int i = 0x60; i < 0x68; i++) table[i] = 2;
        table[0x71] = B64L1; table[0x72] = B64L2; table[0x73] = B64L1; table[0x74] = B64L2;
        table[0x75] = 17; table[0x76] = 17; table[0x77] = 17;
        table[0x78] = CS4L1; table[0x79] = CS4L1; table[0x7A] = CS4L1;
        table[0x7B] = CS4BL1; table[0x7C] = CS5BL1; table[0x7D] = CS6BL1; table[0x7E] = CS7L1; table[0x7F] = CS7L2;
        for (int i = 0; i < 64; i++) table[0x80 + i] = i + 1;
        table[0xC0] = L1; table[0xC1] = L2; table[0xC2] = L4;
        table[0xC3] = 2; table[0xC4] = 3; table[0xC5] = 4; table[0xC6] = 5;
        table[0xC7] = 9;
        table[0xC8] = 2; table[0xC9] = 3; table[0xCA] = 5; table[0xCB] = 9;
        table[0xCC] = 9; table[0xCD] = 5; table[0xCE] = 9; table[0xCF] = 3;
        table[0xD0] = 1; table[0xD1] = 1; table[0xD2] = 1; table[0xD3] = 17;
        table[0xD7] = 2; table[0xD8] = 2; table[0xD9] = 3; table[0xDA] = 5;
        table[0xDB] = 9; table[0xDC] = 5;
        table[0xDD] = L1; table[0xDE] = L2; table[0xDF] = L4;
        table[0xE0] = 1; table[0xE1] = Arr1; table[0xE2] = L1; table[0xE3] = L2;
        table[0xE4] = L4; table[0xE5] = LC1; table[0xE6] = LC2; table[0xE7] = LC4;
        table[0xE8] = 1; table[0xE9] = Obj1; table[0xEA] = L1; table[0xEB] = L2;
        table[0xEC] = L4; table[0xED] = LC1; table[0xEE] = LC2; table[0xEF] = LC4;
        table[0xF0] = NC1; table[0xF1] = NC2; table[0xF2] = ANC1; table[0xF3] = ANC2;
        return table;
    }
}
