using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Forward-only <see cref="ITokenReader"/> over the Cosmos binary JSON format.
/// Yields the SAME token stream a <see cref="Utf8JsonReader"/> would produce
/// walking the equivalent JSON text, so it drives the unchanged generic GetItem
/// envelope transform directly — skipping the intermediate decode-to-text
/// materialization the binary response path otherwise pays.
///
/// <para>The format is a length-prefixed token stream, so the walk is a small
/// explicit frame stack (no recursion) and <see cref="Skip"/> is O(1) via the
/// container length prefix. Every value's byte length, container prefix length,
/// string-marker classification and uniform-number item size come from
/// <see cref="CosmosBinaryDecoder"/> so the streaming walk uses the exact same
/// format authority as the recursive decoder.</para>
///
/// <para>Strings stored inline (and length-prefixed UTF-8) are surfaced as a
/// zero-copy slice of the input. Synthesized values — numbers, GUIDs, base64
/// blobs, hex / datetime / packed-compressed strings and system-dictionary
/// names — are rendered into a pooled scratch buffer that is byte-identical to
/// what the recursive decoder wrote (numbers via <see cref="Utf8Formatter"/>,
/// verified equal to <see cref="Utf8JsonWriter.WriteNumberValue(double)"/>;
/// strings via the same arithmetic as <see cref="CosmosBinaryDecoder"/>). Binary
/// strings are never JSON-escaped, so <see cref="ValueIsEscaped"/> and
/// <see cref="HasValueSequence"/> are always <c>false</c>.</para>
///
/// <para>Holds a rented frame stack and scratch buffer; the caller MUST
/// <see cref="Dispose"/> it (a <c>using</c> declaration) to return both to the
/// pool.</para>
/// </summary>
internal ref struct CosmosBinaryReader : ITokenReader, IDisposable
{
    private enum FrameKind : byte
    {
        Container,
        UniformNumbers,
        UniformOuter,
    }

    private struct Frame
    {
        public int End;            // absolute offset past this container; _offset is restored here on pop
        public FrameKind Kind;
        public bool IsObject;      // Container: object vs array
        public bool NextIsName;    // Container/object: the next read is a property name
        public byte ItemMarker;    // uniform: element number marker
        public int ItemSize;       // uniform: element byte size
        public int Remaining;      // UniformNumbers: numbers left; UniformOuter: inner arrays left
        public int PayloadOffset;  // uniform: cursor into the packed payload
        public int NumberCount;    // UniformOuter: numbers per inner array
    }

    private const int MaxDepth = 256;
    private const int StringRefMaxDepth = 64;

    private const string LowercaseHex = "0123456789abcdef";
    private const string UppercaseHex = "0123456789ABCDEF";
    private const string DateTimeChars = " 0123456789:-.TZ";

    private readonly ReadOnlySpan<byte> _data;
    private Frame[] _frames;
    private byte[]? _scratch;

    private int _offset;
    private int _depth;
    private bool _rootConsumed;

    private JsonTokenType _tokenType;
    private ReadOnlySpan<byte> _valueSlice; // zero-copy slice of _data
    private bool _synthesized;              // value lives in _scratch[.._scratchLen]
    private int _scratchLen;

    /// <param name="binary">Cosmos binary body (leading <c>0x80</c> format byte + root value).</param>
    public CosmosBinaryReader(ReadOnlySpan<byte> binary)
    {
        _data = binary;
        _frames = ArrayPool<Frame>.Shared.Rent(MaxDepth);
        _scratch = null;
        _offset = CosmosBinaryDecoder.RootOffset;
        _depth = 0;
        _rootConsumed = false;
        _tokenType = JsonTokenType.None;
        _valueSlice = default;
        _synthesized = false;
        _scratchLen = 0;
    }

    public void Dispose()
    {
        if (_frames is not null)
        {
            ArrayPool<Frame>.Shared.Return(_frames);
            _frames = null!;
        }
        if (_scratch is not null)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = null;
        }
    }

    public readonly JsonTokenType TokenType => _tokenType;
    public readonly bool ValueIsEscaped => false;
    public readonly bool HasValueSequence => false;

    [UnscopedRef]
    public readonly ReadOnlySpan<byte> ValueSpan
        => _synthesized ? _scratch.AsSpan(0, _scratchLen) : _valueSlice;

    public readonly ReadOnlySequence<byte> ValueSequence => default;

    public readonly bool ValueTextEquals(ReadOnlySpan<byte> utf8Text) => ValueSpan.SequenceEqual(utf8Text);

    public readonly int CopyString(scoped Span<byte> destination)
    {
        ReadOnlySpan<byte> value = ValueSpan;
        value.CopyTo(destination);
        return value.Length;
    }

    public bool Read()
    {
        if (_depth == 0)
        {
            if (_rootConsumed)
            {
                return false;
            }
            _rootConsumed = true;
            ReadValue(_offset);
            return true;
        }

        ref Frame top = ref _frames[_depth - 1];
        switch (top.Kind)
        {
            case FrameKind.Container:
                if (_offset >= top.End)
                {
                    return PopContainer(ref top);
                }
                if (top.IsObject && top.NextIsName)
                {
                    ReadStringToken(_offset, depth: 0);
                    _tokenType = JsonTokenType.PropertyName;
                    _offset += CosmosBinaryDecoder.ValueLength(_data, _offset);
                    top.NextIsName = false;
                    return true;
                }
                if (top.IsObject)
                {
                    top.NextIsName = true; // value consumed below; next entry needs a name
                }
                ReadValue(_offset);
                return true;

            case FrameKind.UniformNumbers:
                if (top.Remaining == 0)
                {
                    return PopUniform(ref top);
                }
                EmitNumber(top.ItemMarker, top.PayloadOffset);
                top.PayloadOffset += top.ItemSize;
                top.Remaining--;
                return true;

            default: // UniformOuter (array of uniform-number arrays)
                if (top.Remaining == 0)
                {
                    return PopUniform(ref top);
                }
                top.Remaining--;
                PushUniformNumbers(top.ItemMarker, top.ItemSize, top.NumberCount, top.PayloadOffset, end: top.PayloadOffset + (top.ItemSize * top.NumberCount));
                top.PayloadOffset += top.ItemSize * top.NumberCount;
                _tokenType = JsonTokenType.StartArray;
                return true;
        }
    }

    public void Skip()
    {
        if (_tokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            _depth--;
            ref Frame f = ref _frames[_depth];
            _offset = f.End;
            _tokenType = f.Kind == FrameKind.Container
                ? (f.IsObject ? JsonTokenType.EndObject : JsonTokenType.EndArray)
                : JsonTokenType.EndArray;
        }
    }

    private bool PopContainer(ref Frame top)
    {
        _depth--;
        _offset = top.End;
        _tokenType = top.IsObject ? JsonTokenType.EndObject : JsonTokenType.EndArray;
        return true;
    }

    private bool PopUniform(ref Frame top)
    {
        _depth--;
        _offset = top.End;
        _tokenType = JsonTokenType.EndArray;
        return true;
    }

    // Positions the reader on the value at <paramref name="offset"/>, setting the
    // token type and (for non-container values) advancing <see cref="_offset"/>
    // past it. Container values push a frame and leave _offset at the payload.
    private void ReadValue(int offset)
    {
        byte marker = _data[offset];

        if (marker < 0x20)
        {
            Span<byte> dst = EnsureScratch(8);
            Utf8Formatter.TryFormat((int)marker, dst, out _scratchLen);
            _synthesized = true;
            _tokenType = JsonTokenType.Number;
            _offset = offset + 1;
            return;
        }

        if (CosmosBinaryDecoder.IsStringMarkerInternal(marker))
        {
            ReadStringToken(offset, depth: 0);
            _tokenType = JsonTokenType.String;
            _offset = offset + CosmosBinaryDecoder.ValueLength(_data, offset);
            return;
        }

        switch (marker)
        {
            case 0xC7:
            case 0xC8:
            case 0xC9:
            case 0xCA:
            case 0xCB:
            case 0xCC:
            case 0xCD:
            case 0xCE:
            case 0xCF:
            case 0xD7:
            case 0xD8:
            case 0xD9:
            case 0xDA:
            case 0xDB:
            case 0xDC:
                EmitNumber(marker, offset + 1);
                _offset = offset + CosmosBinaryDecoder.ValueLength(_data, offset);
                return;

            case 0xD0:
                _synthesized = false;
                _valueSlice = default;
                _tokenType = JsonTokenType.Null;
                _offset = offset + 1;
                return;
            case 0xD1:
                _synthesized = false;
                _valueSlice = default;
                _tokenType = JsonTokenType.False;
                _offset = offset + 1;
                return;
            case 0xD2:
                _synthesized = false;
                _valueSlice = default;
                _tokenType = JsonTokenType.True;
                _offset = offset + 1;
                return;

            case 0xD3:
                SetSynthLength(WriteGuid(_data.Slice(offset + 1, 16), upper: false, quoted: false, EnsureScratch(40)));
                _tokenType = JsonTokenType.String;
                _offset = offset + 17;
                return;

            case 0xDD:
            case 0xDE:
            case 0xDF:
                ReadBinaryValue(offset, marker);
                _tokenType = JsonTokenType.String;
                _offset = offset + CosmosBinaryDecoder.ValueLength(_data, offset);
                return;
        }

        if (marker is >= 0xE0 and <= 0xE7)
        {
            PushContainer(offset, marker, isObject: false);
            _tokenType = JsonTokenType.StartArray;
            return;
        }

        if (marker is >= 0xE8 and <= 0xEF)
        {
            PushContainer(offset, marker, isObject: true);
            _tokenType = JsonTokenType.StartObject;
            return;
        }

        if (marker is >= 0xF0 and <= 0xF3)
        {
            PushUniformArray(offset, marker);
            _tokenType = JsonTokenType.StartArray;
            return;
        }

        throw new JsonException($"Unsupported Cosmos binary JSON marker 0x{marker:X2}.");
    }

    private void PushContainer(int offset, byte marker, bool isObject)
    {
        EnsureDepth();
        int end = offset + CosmosBinaryDecoder.ValueLength(_data, offset);
        int dataStart = marker is 0xE0 or 0xE8
            ? offset + 1
            : offset + CosmosBinaryDecoder.ContainerPrefixLengthInternal(marker);

        _frames[_depth] = new Frame
        {
            End = end,
            Kind = FrameKind.Container,
            IsObject = isObject,
            NextIsName = isObject,
        };
        _depth++;
        _offset = dataStart;
    }

    private void PushUniformArray(int offset, byte marker)
    {
        EnsureDepth();
        int end = offset + CosmosBinaryDecoder.ValueLength(_data, offset);

        switch (marker)
        {
            case 0xF0:
            {
                byte itemMarker = _data[offset + 1];
                int count = _data[offset + 2];
                PushUniformNumbers(itemMarker, CosmosBinaryDecoder.UniformNumberItemSizeInternal(itemMarker), count, offset + 3, end);
                return;
            }
            case 0xF1:
            {
                byte itemMarker = _data[offset + 1];
                int count = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 2));
                PushUniformNumbers(itemMarker, CosmosBinaryDecoder.UniformNumberItemSizeInternal(itemMarker), count, offset + 4, end);
                return;
            }
            case 0xF2:
            {
                if (_data[offset + 1] != 0xF0) throw new JsonException("Invalid nested uniform array marker.");
                byte itemMarker = _data[offset + 2];
                int numberCount = _data[offset + 3];
                int arrayCount = _data[offset + 4];
                PushUniformOuter(itemMarker, numberCount, arrayCount, offset + 5, end);
                return;
            }
            default: // 0xF3
            {
                if (_data[offset + 1] != 0xF1) throw new JsonException("Invalid nested uniform array marker.");
                byte itemMarker = _data[offset + 2];
                int numberCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 3));
                int arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 5));
                PushUniformOuter(itemMarker, numberCount, arrayCount, offset + 7, end);
                return;
            }
        }
    }

    private void PushUniformNumbers(byte itemMarker, int itemSize, int count, int payloadOffset, int end)
    {
        EnsureDepth();
        _frames[_depth] = new Frame
        {
            End = end,
            Kind = FrameKind.UniformNumbers,
            ItemMarker = itemMarker,
            ItemSize = itemSize,
            Remaining = count,
            PayloadOffset = payloadOffset,
        };
        _depth++;
    }

    private void PushUniformOuter(byte itemMarker, int numberCount, int arrayCount, int payloadOffset, int end)
    {
        EnsureDepth();
        _frames[_depth] = new Frame
        {
            End = end,
            Kind = FrameKind.UniformOuter,
            ItemMarker = itemMarker,
            ItemSize = CosmosBinaryDecoder.UniformNumberItemSizeInternal(itemMarker),
            NumberCount = numberCount,
            Remaining = arrayCount,
            PayloadOffset = payloadOffset,
        };
        _depth++;
    }

    private void EmitNumber(byte marker, int payloadOffset)
    {
        ReadOnlySpan<byte> p = _data.Slice(payloadOffset);
        Span<byte> dst = EnsureScratch(40);
        bool ok;
        int written;
        switch (marker)
        {
            case 0xC7:
                ok = Utf8Formatter.TryFormat(BinaryPrimitives.ReadUInt64LittleEndian(p), dst, out written);
                break;
            case 0xC8:
            case 0xD7:
                ok = Utf8Formatter.TryFormat((int)p[0], dst, out written);
                break;
            case 0xD8:
                ok = Utf8Formatter.TryFormat((int)(sbyte)p[0], dst, out written);
                break;
            case 0xC9:
            case 0xD9:
                ok = Utf8Formatter.TryFormat((int)BinaryPrimitives.ReadInt16LittleEndian(p), dst, out written);
                break;
            case 0xCA:
            case 0xDA:
                ok = Utf8Formatter.TryFormat(BinaryPrimitives.ReadInt32LittleEndian(p), dst, out written);
                break;
            case 0xCB:
            case 0xDB:
                ok = Utf8Formatter.TryFormat(BinaryPrimitives.ReadInt64LittleEndian(p), dst, out written);
                break;
            case 0xDC:
                ok = Utf8Formatter.TryFormat(BinaryPrimitives.ReadUInt32LittleEndian(p), dst, out written);
                break;
            case 0xCC:
            case 0xCE:
                ok = Utf8Formatter.TryFormat(BinaryPrimitives.ReadDoubleLittleEndian(p), dst, out written);
                break;
            case 0xCD:
                ok = Utf8Formatter.TryFormat(BinaryPrimitives.ReadSingleLittleEndian(p), dst, out written);
                break;
            default: // 0xCF half — decoder casts to double before formatting
                ok = Utf8Formatter.TryFormat((double)BinaryPrimitives.ReadHalfLittleEndian(p), dst, out written);
                break;
        }
        if (!ok) throw new JsonException("Cosmos binary JSON number could not be formatted.");
        _scratchLen = written;
        _synthesized = true;
        _tokenType = JsonTokenType.Number;
    }

    // Surfaces the string token at <paramref name="offset"/> in <see cref="_valueSlice"/>
    // (zero-copy) or <see cref="_scratch"/> (synthesized). Does NOT advance _offset
    // or set _tokenType (callers do, since the same routine serves names and values).
    private void ReadStringToken(int offset, int depth)
    {
        if (depth > StringRefMaxDepth)
        {
            throw new JsonException("Cosmos binary JSON string reference nesting is too deep.");
        }

        byte marker = _data[offset];

        // System-string dictionary (0x20-0x3F).
        if (marker is >= 0x20 and < 0x40)
        {
            ReadOnlySpan<byte> sys = CosmosBinaryDecoder.SystemStringUtf8(marker - 0x20);
            Span<byte> dst = EnsureScratch(sys.Length);
            sys.CopyTo(dst);
            SetSynthLength(sys.Length);
            return;
        }

        // User-string dictionary (0x40-0x67): never supplied for response bodies
        // (matches CosmosBinaryDecoder, which throws). Surfaces as a fallback.
        if (marker is >= 0x40 and < 0x68)
        {
            throw new JsonException("Cosmos binary JSON body references an external user-string dictionary entry that was not supplied.");
        }

        // Inline UTF-8 (0x80-0xBF) — zero-copy slice.
        if (marker is >= 0x80 and < 0xC0)
        {
            int len = marker - 0x80;
            SetSlice(_data.Slice(offset + 1, len));
            return;
        }

        switch (marker)
        {
            case 0xC0:
                SetSlice(_data.Slice(offset + 2, _data[offset + 1]));
                return;
            case 0xC1:
                SetSlice(_data.Slice(offset + 3, BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 1))));
                return;
            case 0xC2:
            {
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(offset + 1));
                if (raw > int.MaxValue) throw new JsonException("Cosmos binary JSON string is too large.");
                SetSlice(_data.Slice(offset + 5, (int)raw));
                return;
            }
            case 0xC3:
                ReadStringToken(_data[offset + 1], depth + 1);
                return;
            case 0xC4:
                ReadStringToken(BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 1)), depth + 1);
                return;
            case 0xC5:
                ReadStringToken(_data[offset + 1] | (_data[offset + 2] << 8) | (_data[offset + 3] << 16), depth + 1);
                return;
            case 0xC6:
            {
                uint reference = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(offset + 1));
                if (reference > int.MaxValue) throw new JsonException("Cosmos binary JSON string reference is too large.");
                ReadStringToken((int)reference, depth + 1);
                return;
            }
            case 0x71:
            case 0x72:
            case 0x73:
            case 0x74:
                SetSynthLength(WriteBase64(offset, marker is 0x71 or 0x73 ? 1 : 2, url: marker is 0x73 or 0x74));
                return;
            case 0x75:
                SetSynthLength(WriteGuid(_data.Slice(offset + 1, 16), upper: false, quoted: false, EnsureScratch(40)));
                return;
            case 0x76:
                SetSynthLength(WriteGuid(_data.Slice(offset + 1, 16), upper: true, quoted: false, EnsureScratch(40)));
                return;
            case 0x77:
                SetSynthLength(WriteGuid(_data.Slice(offset + 1, 16), upper: false, quoted: true, EnsureScratch(40)));
                return;
            case 0x78:
                SetSynthLength(Write4Bit(offset, LowercaseHex));
                return;
            case 0x79:
                SetSynthLength(Write4Bit(offset, UppercaseHex));
                return;
            case 0x7A:
                SetSynthLength(Write4Bit(offset, DateTimeChars));
                return;
            case 0x7B:
                SetSynthLength(WritePacked(offset, bits: 4, hasBaseChar: true, lenBytes: 1));
                return;
            case 0x7C:
                SetSynthLength(WritePacked(offset, bits: 5, hasBaseChar: true, lenBytes: 1));
                return;
            case 0x7D:
                SetSynthLength(WritePacked(offset, bits: 6, hasBaseChar: true, lenBytes: 1));
                return;
            case 0x7E:
                SetSynthLength(WritePacked(offset, bits: 7, hasBaseChar: false, lenBytes: 1));
                return;
            case 0x7F:
                SetSynthLength(WritePacked(offset, bits: 7, hasBaseChar: false, lenBytes: 2));
                return;
        }

        throw new JsonException($"Invalid Cosmos binary JSON string marker 0x{marker:X2}.");
    }

    private void ReadBinaryValue(int offset, byte marker)
    {
        int prefix;
        int length;
        switch (marker)
        {
            case 0xDD:
                prefix = 2;
                length = _data[offset + 1];
                break;
            case 0xDE:
                prefix = 3;
                length = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 1));
                break;
            default: // 0xDF
                prefix = 5;
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(offset + 1));
                if (raw > int.MaxValue) throw new JsonException("Cosmos binary JSON blob is too large.");
                length = (int)raw;
                break;
        }

        ReadOnlySpan<byte> source = _data.Slice(offset + prefix, length);
        Span<byte> dst = EnsureScratch(Base64.GetMaxEncodedToUtf8Length(length));
        Base64.EncodeToUtf8(source, dst, out _, out int written);
        SetSynthLength(written);
    }

    // ---- Synthesized-string renderers (byte-identical to CosmosBinaryDecoder). ----

    private int WriteBase64(int offset, int lengthBytes, bool url)
    {
        int lengthDiv4 = lengthBytes == 1
            ? _data[offset + 1]
            : BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 1));
        byte padding = _data[offset + 1 + lengthBytes];
        int prefix = 1 + lengthBytes + 1;
        int byteCount = checked((lengthDiv4 * 4 - GetBase64Padding(padding)) * 3 / 4);
        ReadOnlySpan<byte> source = _data.Slice(offset + prefix, byteCount);

        Span<byte> dst = EnsureScratch(Base64.GetMaxEncodedToUtf8Length(byteCount));
        Base64.EncodeToUtf8(source, dst, out _, out int encoded);

        int outputLength = lengthDiv4 * 4;
        if (padding > 2)
        {
            outputLength -= GetBase64Padding(padding);
        }
        if (url)
        {
            for (int i = 0; i < encoded; i++)
            {
                if (dst[i] == (byte)'+') dst[i] = (byte)'-';
                else if (dst[i] == (byte)'/') dst[i] = (byte)'_';
            }
        }
        return encoded > outputLength ? outputLength : encoded;
    }

    private static byte GetBase64Padding(byte padding)
        => padding > 2 ? (byte)~padding : padding;

    private int Write4Bit(int offset, string alphabet)
    {
        int charCount = _data[offset + 1];
        int byteCount = (charCount * 4 + 7) / 8;
        ReadOnlySpan<byte> encoded = _data.Slice(offset + 2, byteCount);
        Span<byte> dst = EnsureScratch(charCount);
        for (int i = 0; i < charCount; i++)
        {
            byte b = encoded[i / 2];
            dst[i] = (byte)alphabet[(i & 1) == 0 ? b & 0x0F : b >> 4];
        }
        return charCount;
    }

    private int WritePacked(int offset, int bits, bool hasBaseChar, int lenBytes)
    {
        int charCount = lenBytes == 1
            ? _data[offset + 1]
            : BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset + 1));
        byte baseChar = hasBaseChar ? _data[offset + 1 + lenBytes] : (byte)0;
        int prefix = 1 + lenBytes + (hasBaseChar ? 1 : 0);
        int byteCount = (charCount * bits + 7) / 8;
        ReadOnlySpan<byte> encoded = _data.Slice(offset + prefix, byteCount);

        Span<byte> dst = EnsureScratch(charCount);
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
                dst[produced++] = (byte)((value & mask) + baseChar);
                value >>= bits;
            }
            sourceOffset += bits;
        }
        return charCount;
    }

    private static int WriteGuid(ReadOnlySpan<byte> guidBytes, bool upper, bool quoted, Span<byte> dst)
    {
        string hex = upper ? UppercaseHex : LowercaseHex;
        int ci = 0;
        if (quoted) dst[ci++] = (byte)'"';
        for (int i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10) dst[ci++] = (byte)'-';
            byte b = guidBytes[i];
            dst[ci++] = (byte)hex[b & 0x0F];
            dst[ci++] = (byte)hex[b >> 4];
        }
        if (quoted) dst[ci++] = (byte)'"';
        return ci;
    }

    // ---- value-slot bookkeeping ----

    private void SetSlice(ReadOnlySpan<byte> slice)
    {
        _valueSlice = slice;
        _synthesized = false;
    }

    private void SetSynthLength(int length)
    {
        _scratchLen = length;
        _synthesized = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> EnsureScratch(int min)
    {
        byte[]? buf = _scratch;
        if (buf is null || buf.Length < min)
        {
            if (buf is not null)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            buf = ArrayPool<byte>.Shared.Rent(min);
            _scratch = buf;
        }
        return buf;
    }

    private readonly void EnsureDepth()
    {
        if (_depth >= MaxDepth)
        {
            throw new JsonException("Cosmos binary JSON nesting exceeds the supported maximum depth.");
        }
    }
}
