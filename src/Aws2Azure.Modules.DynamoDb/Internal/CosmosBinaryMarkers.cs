using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

internal enum CosmosBinaryValueKind : byte
{
    SmallNumber,
    String,
    Number,
    Null,
    False,
    True,
    GuidString,
    Binary,
    Array,
    Object,
    UniformNumberArray,
    Unsupported,
}

internal static class CosmosBinaryMarkers
{
    private const byte BinaryFormatMarker = 0x80;
    private const int MaxDepth = 256;

    private const int L1 = -1, L2 = -2, L4 = -3, LC1 = -4, LC2 = -5, LC4 = -6;
    private const int CS4L1 = -7, CS7L1 = -8, CS7L2 = -9;
    private const int CS4BL1 = -10, CS5BL1 = -11, CS6BL1 = -12;
    private const int B64L1 = -13, B64L2 = -14;
    private const int Arr1 = -15, Obj1 = -16;
    private const int NC1 = -17, NC2 = -18, ANC1 = -19, ANC2 = -20;

    internal const int RootOffset = 1;
    internal const string LowercaseHex = "0123456789abcdef";
    internal const string UppercaseHex = "0123456789ABCDEF";
    internal const string DateTimeChars = " 0123456789:-.TZ";

    private static readonly int[] Lookup = BuildLookup();

    private static readonly string[] SystemStrings =
    [
        "$s", "$t", "$v", "_attachments", "_etag", "_rid", "_self", "_ts",
        "attachments/", "coordinates", "geometry", "GeometryCollection", "id",
        "url", "Value", "label", "LineString", "link", "MultiLineString",
        "MultiPoint", "MultiPolygon", "name", "Name", "Type", "Point", "Polygon",
        "properties", "type", "value", "Feature", "FeatureCollection", "_id",
    ];

    private static readonly byte[][] SystemStringsUtf8 = BuildSystemStringsUtf8();

    internal static bool IsBinary(ReadOnlySpan<byte> body)
        => body.Length > 0 && body[0] == BinaryFormatMarker;

    internal static string SystemString(int index) => SystemStrings[index];

    internal static ReadOnlySpan<byte> SystemStringUtf8(int index) => SystemStringsUtf8[index];

    internal static CosmosBinaryValueKind GetValueKind(byte marker)
    {
        if (marker < 0x20)
        {
            return CosmosBinaryValueKind.SmallNumber;
        }

        if (IsString(marker))
        {
            return CosmosBinaryValueKind.String;
        }

        if (IsNumber(marker))
        {
            return CosmosBinaryValueKind.Number;
        }

        return marker switch
        {
            0xD0 => CosmosBinaryValueKind.Null,
            0xD1 => CosmosBinaryValueKind.False,
            0xD2 => CosmosBinaryValueKind.True,
            0xD3 => CosmosBinaryValueKind.GuidString,
            0xDD or 0xDE or 0xDF => CosmosBinaryValueKind.Binary,
            >= 0xE0 and <= 0xE7 => CosmosBinaryValueKind.Array,
            >= 0xE8 and <= 0xEF => CosmosBinaryValueKind.Object,
            >= 0xF0 and <= 0xF3 => CosmosBinaryValueKind.UniformNumberArray,
            _ => CosmosBinaryValueKind.Unsupported,
        };
    }

    internal static bool IsString(byte marker)
        => marker is >= 0x20 and < 0x68
            || marker is >= 0x71 and < 0x80
            || marker is >= 0x80 and <= 0xC6;

    internal static int ContainerPrefixLength(byte marker) => marker switch
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

    internal static int UniformNumberItemSize(byte marker)
    {
        int length = Lookup[marker];
        if (length <= 1)
        {
            throw new JsonException($"Invalid uniform number item marker 0x{marker:X2}.");
        }

        return length - 1;
    }

    internal static int ValueLength(ReadOnlySpan<byte> full, int offset)
        => BoundedLength(full, offset, 0);

    internal static int ValueLength(ReadOnlySpan<byte> full, int offset, int depth)
        => BoundedLength(full, offset, depth);

    internal static int Base64LengthBytes(byte marker)
        => marker is 0x71 or 0x73 ? 1 : 2;

    internal static bool IsUrlBase64(byte marker)
        => marker is 0x73 or 0x74;

    internal static string FourBitAlphabet(byte marker)
        => marker switch
        {
            0x78 => LowercaseHex,
            0x79 => UppercaseHex,
            _ => DateTimeChars,
        };

    internal static int PackedStringBits(byte marker)
        => marker switch
        {
            0x7B => 4,
            0x7C => 5,
            0x7D => 6,
            _ => 7,
        };

    internal static bool PackedStringHasBaseChar(byte marker)
        => marker is 0x7B or 0x7C or 0x7D;

    internal static int PackedStringLengthBytes(byte marker)
        => marker == 0x7F ? 2 : 1;

    private static bool IsNumber(byte marker)
        => marker is 0xC7 or 0xC8 or 0xC9 or 0xCA or 0xCB
            or 0xCC or 0xCD or 0xCE or 0xCF
            or 0xD7 or 0xD8 or 0xD9 or 0xDA or 0xDB or 0xDC;

    private static byte[][] BuildSystemStringsUtf8()
    {
        var table = new byte[SystemStrings.Length][];
        for (int i = 0; i < SystemStrings.Length; i++)
        {
            table[i] = Encoding.UTF8.GetBytes(SystemStrings[i]);
        }

        return table;
    }

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
                int item = BoundedLength(full, offset + 1, depth + 1);
                return 1L + item;
            }
            case Obj1:
            {
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

    private static byte GetBase64Padding(byte padding)
        => padding > 2 ? (byte)~padding : padding;

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
