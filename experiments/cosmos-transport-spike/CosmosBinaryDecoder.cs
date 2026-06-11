using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

// =============================================================================
// CosmosBinaryDecoder (issue #265 spike)
//
// A from-scratch, dependency-free decoder for the Cosmos "binary JSON" body
// format negotiated via the `x-ms-cosmos-supported-serialization-formats:
// CosmosBinary` request header. It exists to PROVE the format is decodable from
// the open-source spec (the SDK's Microsoft.Azure.Cosmos/src/Json/
// JsonBinaryEncoding.* files) without taking any binary/proprietary dependency
// (unlike rntbd), and to measure the parse cost.
//
// Faithful port of the relevant subset of JsonBinaryEncoding:
//   * value-length navigation (ValueLengths.Lookup, exact copy)
//   * the 32-entry SystemStrings table
//   * scalars: encoded literal ints, fixed numbers, null/true/false, GUID
//   * strings: encoded-length, StrL1/2/4, StrR1-4 (references), system strings,
//     base64 (0x71-0x74), GUID strings (0x75-0x77), 4-bit compressed hex /
//     datetime (0x78-0x7A), packed 4/5/6/7-bit (0x7B-0x7F)
//   * containers: Arr0/Arr1/ArrL*/ArrLC*, Obj0/Obj1/ObjL*/ObjLC*
//
// NOT ported (throws NotSupportedException so the spike surfaces exactly what
// extra work each would need): 1-byte/2-byte *user* strings (need an external
// string dictionary that point-read bodies do not carry), uniform number arrays
// (0xF0-0xF3), binary blobs (0xDD-0xDF). None appear in normal Cosmos document
// point-read bodies.
//
// References resolved against the FULL buffer including the leading 0x80 format
// marker (StrR offsets are absolute from byte 0 - see JsonReader.JsonBinaryReader).
// =============================================================================

internal static class CosmosBinaryDecoder
{
    private const byte BinaryFormatMarker = 0x80;

    // Exact copy of JsonBinaryEncoding.ValueLengths.Lookup. Positive = total
    // length incl. type marker; negative = sentinel (length encoded in buffer).
    private const int L1 = -1, L2 = -2, L4 = -3, LC1 = -4, LC2 = -5, LC4 = -6;
    private const int CS4L1 = -7, CS7L1 = -8, CS7L2 = -9;
    private const int CS4BL1 = -10, CS5BL1 = -11, CS6BL1 = -12;
    private const int B64L1 = -13, B64L2 = -14;
    private const int Arr1Sentinel = -15, Obj1Sentinel = -16;
    private const int NC1 = -17, NC2 = -18, ANC1 = -19, ANC2 = -20;

    private static readonly int[] Lookup = BuildLookup();

    private static readonly string[] SystemStrings =
    {
        "$s", "$t", "$v", "_attachments", "_etag", "_rid", "_self", "_ts",
        "attachments/", "coordinates", "geometry", "GeometryCollection", "id",
        "url", "Value", "label", "LineString", "link", "MultiLineString",
        "MultiPoint", "MultiPolygon", "name", "Name", "Type", "Point", "Polygon",
        "properties", "type", "value", "Feature", "FeatureCollection", "_id",
    };

    private const string LowercaseHex = "0123456789abcdef";
    private const string UppercaseHex = "0123456789ABCDEF";
    private const string DateTimeChars = " 0123456789:-.TZ";

    /// <summary>Returns true if the body looks like a Cosmos binary JSON blob.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> body) => body.Length > 0 && body[0] == BinaryFormatMarker;

    /// <summary>Decodes a binary JSON body to canonical UTF-8 JSON text.</summary>
    public static byte[] DecodeToJsonUtf8(ReadOnlySpan<byte> body)
    {
        if (!IsBinary(body))
        {
            throw new ArgumentException($"not a binary JSON body (first byte 0x{(body.Length > 0 ? body[0] : 0):X2}, expected 0x80)");
        }

        var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            // Root value is at offset 1; StrR references are absolute from offset 0.
            WriteValue(w, body, offset: 1);
        }
        return ms.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter w, ReadOnlySpan<byte> full, int offset)
    {
        byte m = full[offset];

        // [0x00, 0x20): encoded literal integer
        if (m < 0x20)
        {
            w.WriteNumberValue(m);
            return;
        }

        // [0x20, 0x40): 1-byte system string
        if (m is >= 0x20 and < 0x40)
        {
            w.WriteStringValue(SystemStrings[m - 0x20]);
            return;
        }

        // [0x40, 0x68): user strings (need external dictionary)
        if (m is >= 0x40 and < 0x68)
        {
            throw new NotSupportedException($"user-string marker 0x{m:X2} (requires external JsonStringDictionary)");
        }

        // String families that decode to a string value.
        if (IsStringMarker(m))
        {
            w.WriteStringValue(DecodeString(full, offset));
            return;
        }

        switch (m)
        {
            case 0xC7: // NumberUInt64
                w.WriteNumberValue(BinaryPrimitives.ReadUInt64LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xC8: // NumberUInt8
                w.WriteNumberValue(full[offset + 1]);
                return;
            case 0xC9: // NumberInt16
                w.WriteNumberValue(BinaryPrimitives.ReadInt16LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCA: // NumberInt32
                w.WriteNumberValue(BinaryPrimitives.ReadInt32LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCB: // NumberInt64
                w.WriteNumberValue(BinaryPrimitives.ReadInt64LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCC: // NumberDouble
            case 0xCE: // Float64
                w.WriteNumberValue(BinaryPrimitives.ReadDoubleLittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCD: // Float32
                w.WriteNumberValue(BinaryPrimitives.ReadSingleLittleEndian(full.Slice(offset + 1)));
                return;
            case 0xCF: // Float16
                w.WriteNumberValue((double)BinaryPrimitives.ReadHalfLittleEndian(full.Slice(offset + 1)));
                return;
            case 0xD0: // Null
                w.WriteNullValue();
                return;
            case 0xD1: // False
                w.WriteBooleanValue(false);
                return;
            case 0xD2: // True
                w.WriteBooleanValue(true);
                return;
            case 0xD3: // Guid value
                w.WriteStringValue(DecodeGuid(full.Slice(offset + 1, 16), upper: false));
                return;
            case 0xD7: // UInt8
                w.WriteNumberValue(full[offset + 1]);
                return;
            case 0xD8: // Int8
                w.WriteNumberValue((sbyte)full[offset + 1]);
                return;
            case 0xD9: // Int16
                w.WriteNumberValue(BinaryPrimitives.ReadInt16LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xDA: // Int32
                w.WriteNumberValue(BinaryPrimitives.ReadInt32LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xDB: // Int64
                w.WriteNumberValue(BinaryPrimitives.ReadInt64LittleEndian(full.Slice(offset + 1)));
                return;
            case 0xDC: // UInt32
                w.WriteNumberValue(BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(offset + 1)));
                return;
        }

        // Arrays [0xE0, 0xE8)
        if (m is >= 0xE0 and < 0xE8)
        {
            WriteArray(w, full, offset, m);
            return;
        }

        // Objects [0xE8, 0xF0)
        if (m is >= 0xE8 and < 0xF0)
        {
            WriteObject(w, full, offset, m);
            return;
        }

        throw new NotSupportedException($"value marker 0x{m:X2}");
    }

    private static void WriteArray(Utf8JsonWriter w, ReadOnlySpan<byte> full, int offset, byte m)
    {
        w.WriteStartArray();
        if (m != 0xE0) // not Arr0
        {
            int dataStart = offset + ContainerPrefixLength(m);
            int dataEnd = offset + (int)GetValueLength(full, offset);
            int p = dataStart;
            while (p < dataEnd)
            {
                WriteValue(w, full, p);
                p += (int)GetValueLength(full, p);
            }
        }
        w.WriteEndArray();
    }

    private static void WriteObject(Utf8JsonWriter w, ReadOnlySpan<byte> full, int offset, byte m)
    {
        w.WriteStartObject();
        if (m != 0xE8) // not Obj0
        {
            int dataStart = offset + ContainerPrefixLength(m);
            int dataEnd = offset + (int)GetValueLength(full, offset);
            int p = dataStart;
            while (p < dataEnd)
            {
                string name = DecodeString(full, p);
                p += (int)GetValueLength(full, p);
                w.WritePropertyName(name);
                WriteValue(w, full, p);
                p += (int)GetValueLength(full, p);
            }
        }
        w.WriteEndObject();
    }

    // Bytes between the type marker and the first child for a container marker.
    private static int ContainerPrefixLength(byte m) => m switch
    {
        0xE1 or 0xE9 => 1,                       // Arr1 / Obj1 (single child, no length prefix)
        0xE2 or 0xEA => 1 + 1,                   // L1
        0xE3 or 0xEB => 1 + 2,                   // L2
        0xE4 or 0xEC => 1 + 4,                   // L4
        0xE5 or 0xED => 1 + 1 + 1,               // LC1
        0xE6 or 0xEE => 1 + 2 + 2,               // LC2
        0xE7 or 0xEF => 1 + 4 + 4,               // LC4
        _ => throw new NotSupportedException($"container marker 0x{m:X2}"),
    };

    private static bool IsStringMarker(byte m) =>
        (m is >= 0x68 and < 0x80)   // base64 / guid / compressed / packed
        || (m is >= 0x80 and < 0xC0) // encoded-length strings
        || (m is >= 0xC0 and <= 0xC6); // StrL1/2/4, StrR1-4

    private static string DecodeString(ReadOnlySpan<byte> full, int offset)
    {
        byte m = full[offset];

        // 1-byte system string
        if (m is >= 0x20 and < 0x40)
        {
            return SystemStrings[m - 0x20];
        }

        // Encoded-length UTF-8 string [0x80, 0xC0): length = marker - 0x80
        if (m is >= 0x80 and < 0xC0)
        {
            int len = m - 0x80;
            return Encoding.UTF8.GetString(full.Slice(offset + 1, len));
        }

        switch (m)
        {
            case 0xC0: // StrL1
            {
                int len = full[offset + 1];
                return Encoding.UTF8.GetString(full.Slice(offset + 2, len));
            }
            case 0xC1: // StrL2
            {
                int len = BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1));
                return Encoding.UTF8.GetString(full.Slice(offset + 3, len));
            }
            case 0xC2: // StrL4
            {
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(offset + 1));
                return Encoding.UTF8.GetString(full.Slice(offset + 5, len));
            }
            case 0xC3: // StrR1 - reference at 1-byte offset (absolute from buffer start)
                return DecodeString(full, full[offset + 1]);
            case 0xC4: // StrR2
                return DecodeString(full, BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1)));
            case 0xC5: // StrR3
                return DecodeString(full, full[offset + 1] | (full[offset + 2] << 8) | (full[offset + 3] << 16));
            case 0xC6: // StrR4
                return DecodeString(full, (int)BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(offset + 1)));
        }

        // Compact string encodings [0x68, 0x80)
        return DecodeCompactString(full, offset, m);
    }

    private static string DecodeCompactString(ReadOnlySpan<byte> full, int offset, byte m)
    {
        switch (m)
        {
            case 0x71: // Base64StringLength1 (std)
                return DecodeBase64(full, offset, lenBytes: 1, url: false);
            case 0x72: // Base64StringLength2 (std)
                return DecodeBase64(full, offset, lenBytes: 2, url: false);
            case 0x73: // Base64UrlStringLength1
                return DecodeBase64(full, offset, lenBytes: 1, url: true);
            case 0x74: // Base64UrlStringLength2
                return DecodeBase64(full, offset, lenBytes: 2, url: true);
            case 0x75: // LowercaseGuidString
                return DecodeGuid(full.Slice(offset + 1, 16), upper: false);
            case 0x76: // UppercaseGuidString
                return DecodeGuid(full.Slice(offset + 1, 16), upper: true);
            case 0x77: // DoubleQuotedLowercaseGuidString
                return "\"" + DecodeGuid(full.Slice(offset + 1, 16), upper: false) + "\"";
            case 0x78: // CompressedLowercaseHexString
                return Decode4Bit(full, offset, LowercaseHex);
            case 0x79: // CompressedUppercaseHexString
                return Decode4Bit(full, offset, UppercaseHex);
            case 0x7A: // CompressedDateTimeString
                return Decode4Bit(full, offset, DateTimeChars);
            case 0x7B: // Packed4BitString
                return DecodePacked(full, offset, bits: 4, hasBaseChar: true);
            case 0x7C: // Packed5BitString
                return DecodePacked(full, offset, bits: 5, hasBaseChar: true);
            case 0x7D: // Packed6BitString
                return DecodePacked(full, offset, bits: 6, hasBaseChar: true);
            case 0x7E: // Packed7BitStringLength1
                return DecodePacked(full, offset, bits: 7, hasBaseChar: false, lenBytes: 1);
            case 0x7F: // Packed7BitStringLength2
                return DecodePacked(full, offset, bits: 7, hasBaseChar: false, lenBytes: 2);
            default:
                throw new NotSupportedException($"string marker 0x{m:X2}");
        }
    }

    private static string DecodeGuid(ReadOnlySpan<byte> g16, bool upper)
    {
        string hex = upper ? UppercaseHex : LowercaseHex;
        Span<char> c = stackalloc char[36];
        int ci = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10)
            {
                c[ci++] = '-';
            }
            byte b = g16[i];
            c[ci++] = hex[b & 0x0F];
            c[ci++] = hex[b >> 4];
        }
        return new string(c);
    }

    private static string DecodeBase64(ReadOnlySpan<byte> full, int offset, int lenBytes, bool url)
    {
        int lenDiv4 = lenBytes == 1
            ? full[offset + 1]
            : BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1));
        byte padding = full[offset + 1 + lenBytes];
        int prefix = 1 + lenBytes + 1;

        int outLen = ComputeBase64StringLength(lenDiv4, padding);
        int byteCount = (lenDiv4 * 4 - GetBase64Padding(padding)) * 3 / 4;

        ReadOnlySpan<byte> raw = full.Slice(offset + prefix, byteCount);
        string b64 = Convert.ToBase64String(raw); // standard, padded, length multiple of 4
        if (url)
        {
            b64 = b64.Replace('+', '-').Replace('/', '_');
        }
        return b64.Length > outLen ? b64[..outLen] : b64;
    }

    private static byte GetBase64Padding(byte padding) => padding > 2 ? (byte)~padding : padding;

    private static int ComputeBase64StringLength(int lenDiv4, byte padding)
    {
        int len = lenDiv4 * 4;
        if (padding > 2)
        {
            len -= GetBase64Padding(padding);
        }
        return len;
    }

    // 4-bit fixed-alphabet decode (hex / datetime). Each encoded byte packs two
    // characters: the first (even index) in the LOW nibble, the second (odd index)
    // in the HIGH nibble; an odd trailing char occupies the low nibble alone.
    private static string Decode4Bit(ReadOnlySpan<byte> full, int offset, string alphabet)
    {
        int charCount = full[offset + 1];
        ReadOnlySpan<byte> enc = full.Slice(offset + 2, (charCount * 4 + 7) / 8);
        Span<char> c = charCount <= 256 ? stackalloc char[charCount] : new char[charCount];
        for (int i = 0; i < charCount; i++)
        {
            byte b = enc[i / 2];
            int nibble = (i % 2 == 0) ? (b & 0x0F) : (b >> 4);
            c[i] = alphabet[nibble];
        }
        return new string(c);
    }

    // Packed N-bit decode (4/5/6/7 bits per char, optionally relative to a base
    // char). Faithful port of JsonBinaryEncoding.DecodeCompressedStringValue.
    private static string DecodePacked(ReadOnlySpan<byte> full, int offset, int bits, bool hasBaseChar, int lenBytes = 1)
    {
        int charCount = lenBytes == 1
            ? full[offset + 1]
            : BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(offset + 1));
        byte baseChar = hasBaseChar ? full[offset + 1 + lenBytes] : (byte)0;
        int prefix = 1 + lenBytes + (hasBaseChar ? 1 : 0);
        ReadOnlySpan<byte> enc = full.Slice(offset + prefix, (charCount * bits + 7) / 8);

        var dst = new byte[charCount];
        long mask = 0x000000FF >> (8 - bits);
        var dstSpan = dst.AsSpan();
        int full8 = charCount / 8 * 8;
        int produced = 0;
        Span<byte> packed = stackalloc byte[8];
        while (produced < full8)
        {
            packed.Clear();
            enc.Slice(0, bits).CopyTo(packed);
            long pv = BinaryPrimitives.ReadInt64LittleEndian(packed);
            for (int k = 0; k < 8; k++)
            {
                dstSpan[produced + k] = (byte)((byte)(pv & mask) + baseChar);
                pv >>= bits;
            }
            enc = enc.Slice(bits);
            produced += 8;
        }
        if (produced < charCount)
        {
            packed.Clear();
            enc.CopyTo(packed);
            long pv = BinaryPrimitives.ReadInt64LittleEndian(packed);
            for (int k = 0; produced + k < charCount; k++)
            {
                dstSpan[produced + k] = (byte)((byte)(pv & mask) + baseChar);
                pv >>= bits;
            }
        }
        return Encoding.ASCII.GetString(dst);
    }

    // Total byte length of the value at offset (incl. type marker). Mirror of
    // JsonBinaryEncoding.ValueLengths.GetValueLength.
    private static long GetValueLength(ReadOnlySpan<byte> full, int offset)
    {
        ReadOnlySpan<byte> buffer = full.Slice(offset);
        long length = Lookup[buffer[0]];
        if (length >= 0)
        {
            return length;
        }

        switch (length)
        {
            case L1: return 1 + 1 + buffer[1];
            case L2: return 1 + 2 + BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));
            case L4: return 1 + 4 + BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(1));
            case LC1: return 1 + 1 + 1 + buffer[1];
            case LC2: return 1 + 2 + 2 + BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1));
            case LC4: return 1 + 4 + 4 + BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(1));
            case Arr1Sentinel:
            {
                long item = GetValueLength(full, offset + 1);
                return item == 0 ? 0 : 1 + item;
            }
            case Obj1Sentinel:
            {
                long nameLen = GetValueLength(full, offset + 1);
                if (nameLen == 0)
                {
                    return 0;
                }
                long valLen = GetValueLength(full, offset + 1 + (int)nameLen);
                return 1 + nameLen + valLen;
            }
            case B64L1: return 1 + 1 + 1 + GetBase64ByteCount(buffer[1], buffer[2]);
            case B64L2: return 1 + 2 + 1 + GetBase64ByteCount(BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1)), buffer[3]);
            case CS4L1: return 1 + 1 + CompressedLen(buffer[1], 4);
            case CS7L1: return 1 + 1 + CompressedLen(buffer[1], 7);
            case CS7L2: return 1 + 2 + CompressedLen(BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(1)), 7);
            case CS4BL1: return 1 + 1 + 1 + CompressedLen(buffer[1], 4);
            case CS5BL1: return 1 + 1 + 1 + CompressedLen(buffer[1], 5);
            case CS6BL1: return 1 + 1 + 1 + CompressedLen(buffer[1], 6);
            default:
                throw new NotSupportedException($"variable-length sentinel {length} for marker 0x{buffer[0]:X2}");
        }
    }

    private static int CompressedLen(int length, int bits) => (length * bits + 7) / 8;

    private static int GetBase64ByteCount(int lenDiv4, byte padding) =>
        (lenDiv4 * 4 - GetBase64Padding(padding)) * 3 / 4;

    private static int[] BuildLookup()
    {
        var t = new int[256];
        // [0x00,0x20) literal ints, [0x20,0x40) sys-string, [0x40,0x60) user-string-1: all length 1
        for (int i = 0x00; i < 0x60; i++) t[i] = 1;
        // [0x60,0x68) 2-byte user string
        for (int i = 0x60; i < 0x68; i++) t[i] = 2;
        // [0x68,0x70) empty
        // [0x70,0x78) string values
        t[0x71] = B64L1; t[0x72] = B64L2; t[0x73] = B64L1; t[0x74] = B64L2;
        t[0x75] = 17; t[0x76] = 17; t[0x77] = 17;
        // [0x78,0x80) compressed
        t[0x78] = CS4L1; t[0x79] = CS4L1; t[0x7A] = CS4L1;
        t[0x7B] = CS4BL1; t[0x7C] = CS5BL1; t[0x7D] = CS6BL1; t[0x7E] = CS7L1; t[0x7F] = CS7L2;
        // [0x80,0xC0) encoded string length 1..64
        for (int i = 0; i < 64; i++) t[0x80 + i] = i + 1;
        // Variable length strings
        t[0xC0] = L1; t[0xC1] = L2; t[0xC2] = L4;
        t[0xC3] = 2; t[0xC4] = 3; t[0xC5] = 4; t[0xC6] = 5;
        t[0xC7] = 9; // NumberUInt64
        // Numbers
        t[0xC8] = 2; t[0xC9] = 3; t[0xCA] = 5; t[0xCB] = 9; t[0xCC] = 9; t[0xCD] = 5; t[0xCE] = 9; t[0xCF] = 3;
        // Other value types
        t[0xD0] = 1; t[0xD1] = 1; t[0xD2] = 1; t[0xD3] = 17;
        t[0xD7] = 2; t[0xD8] = 2; t[0xD9] = 3; t[0xDA] = 5; t[0xDB] = 9; t[0xDC] = 5;
        t[0xDD] = L1; t[0xDE] = L2; t[0xDF] = L4;
        // Arrays
        t[0xE0] = 1; t[0xE1] = Arr1Sentinel; t[0xE2] = L1; t[0xE3] = L2; t[0xE4] = L4;
        t[0xE5] = LC1; t[0xE6] = LC2; t[0xE7] = LC4;
        // Objects
        t[0xE8] = 1; t[0xE9] = Obj1Sentinel; t[0xEA] = L1; t[0xEB] = L2; t[0xEC] = L4;
        t[0xED] = LC1; t[0xEE] = LC2; t[0xEF] = LC4;
        // Uniform number arrays (unsupported by this spike decoder)
        t[0xF0] = NC1; t[0xF1] = NC2; t[0xF2] = ANC1; t[0xF3] = ANC2;
        return t;
    }
}
