using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Aws2Azure.Benchmarks.DynamoDb.Spike319;

/// <summary>
/// SPIKE (#319) — the subset of the <see cref="Utf8JsonReader"/> token-stream
/// surface that the DynamoDB GetItem envelope transform actually consumes.
///
/// <para>The production transform is written against <c>Utf8JsonReader</c>. The
/// proposed design abstracts the <i>reader</i> behind this interface so the
/// transform can be written ONCE (generic over <c>TReader</c>) and driven by
/// either JSON text (<see cref="Utf8JsonTokenReader"/>) or the CosmosBinary
/// format (<see cref="CosmosBinaryReader"/>) — eliminating the intermediate
/// JSON-text materialization the binary path pays today, with no duplicated
/// transform logic.</para>
///
/// <para>Used with the <c>allows ref struct</c> anti-constraint (C# 13 / .NET 9+)
/// so the generic transform monomorphizes per concrete reader and the JIT
/// devirtualizes these calls — AOT-friendly, no boxing.</para>
/// </summary>
internal interface ITokenReader
{
    JsonTokenType TokenType { get; }
    bool ValueIsEscaped { get; }
    bool HasValueSequence { get; }
    [UnscopedRef] ReadOnlySpan<byte> ValueSpan { get; }
    ReadOnlySequence<byte> ValueSequence { get; }

    bool Read();
    void Skip();
    bool ValueTextEquals(ReadOnlySpan<byte> utf8Text);
    int CopyString(scoped Span<byte> destination);
}

/// <summary>
/// Thin <see cref="ITokenReader"/> adapter over a real <see cref="Utf8JsonReader"/>.
/// Forwards every member 1:1; exists purely so JSON text can drive the same
/// generic transform as the binary reader. Measures the cost of the abstraction
/// itself (vs the concrete-reader production transform) on identical input.
/// </summary>
internal ref struct Utf8JsonTokenReader : ITokenReader
{
    private Utf8JsonReader _reader;

    public Utf8JsonTokenReader(ReadOnlySpan<byte> utf8Json) => _reader = new Utf8JsonReader(utf8Json);

    public readonly JsonTokenType TokenType => _reader.TokenType;
    public readonly bool ValueIsEscaped => _reader.ValueIsEscaped;
    public readonly bool HasValueSequence => _reader.HasValueSequence;
    [UnscopedRef] public readonly ReadOnlySpan<byte> ValueSpan => _reader.ValueSpan;
    public readonly ReadOnlySequence<byte> ValueSequence => _reader.ValueSequence;

    public bool Read() => _reader.Read();
    public void Skip() => _reader.Skip();
    public readonly bool ValueTextEquals(ReadOnlySpan<byte> utf8Text) => _reader.ValueTextEquals(utf8Text);
    public readonly int CopyString(scoped Span<byte> destination) => _reader.CopyString(destination);
}

/// <summary>
/// Forward-only <see cref="ITokenReader"/> over the CosmosBinary format. Yields
/// the SAME token stream a <see cref="Utf8JsonReader"/> would produce walking the
/// equivalent JSON text, so it can drive the unchanged generic transform.
///
/// <para>This is a spike-scope reader: it covers the markers the corpus
/// exercises (inline + 2-byte strings; int32 / double scalars; bool; null;
/// LC1/LC2/LC4 array + object containers). CosmosBinary is itself a length-
/// prefixed token stream, so the walk is a small explicit stack — and
/// <see cref="Skip"/> is O(1) via the container length prefix (vs the
/// re-scan <c>Utf8JsonReader.Skip</c> performs). Binary strings are never
/// JSON-escaped and always contiguous, so <see cref="ValueIsEscaped"/> and
/// <see cref="HasValueSequence"/> are always <c>false</c>.</para>
///
/// <para>Numbers are surfaced as a <see cref="JsonTokenType.Number"/> token whose
/// <see cref="ValueSpan"/> is the canonical digit text (formatted into a
/// caller-provided scratch buffer), matching how the production decoder rendered
/// the number before the transform re-read it. The corpus is integer-only;
/// double canonicalization is out of spike scope.</para>
/// </summary>
internal ref struct CosmosBinaryReader : ITokenReader
{
    private struct Frame
    {
        public int End;
        public bool IsObject;
        public bool NextIsName;
    }

    private const int MaxDepth = 64;
    private const int NumberScratchLen = 32;

    [System.Runtime.CompilerServices.InlineArray(MaxDepth)]
    private struct FrameBuffer
    {
        private Frame _element0;
    }

    [System.Runtime.CompilerServices.InlineArray(NumberScratchLen)]
    private struct NumberBuffer
    {
        private byte _element0;
    }

    private readonly ReadOnlySpan<byte> _data;
    private FrameBuffer _frames;
    private NumberBuffer _numberBuf;
    private int _offset;
    private int _depth;
    private bool _rootConsumed;

    private JsonTokenType _tokenType;
    private ReadOnlySpan<byte> _valueSpan;
    private bool _isNumber;
    private int _numberLen;

    /// <param name="binary">CosmosBinary body (leading 0x80 format byte + root value).</param>
    public CosmosBinaryReader(ReadOnlySpan<byte> binary)
    {
        _data = binary;
        _offset = 1; // skip the 0x80 binary-format marker byte
        _depth = 0;
        _rootConsumed = false;
        _tokenType = JsonTokenType.None;
        _valueSpan = default;
        _isNumber = false;
        _numberLen = 0;
    }

    // Inline-array fields exposed as spans over the live `this`. Reconstructed
    // per access (never stored in a field) so the spans cannot outlive the
    // stack frame — a span pointing into another field of the same ref struct
    // cannot be stored, only returned/consumed.
    [UnscopedRef] private Span<Frame> Stack => _frames;
    [UnscopedRef] private Span<byte> NumberScratch => _numberBuf;
    [UnscopedRef] private readonly ReadOnlySpan<byte> NumberScratchRO => _numberBuf;

    public readonly JsonTokenType TokenType => _tokenType;
    public readonly bool ValueIsEscaped => false;
    public readonly bool HasValueSequence => false;

    // Numbers live in the inline NumberScratch (their span cannot be stored in a
    // field, so it is reconstructed here on read); everything else is a slice of
    // the input buffer captured in _valueSpan.
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> ValueSpan => _isNumber ? NumberScratchRO[.._numberLen] : _valueSpan;

    public readonly ReadOnlySequence<byte> ValueSequence => default;

    public bool Read()
    {
        // Close any container whose payload is exhausted (one End token per Read).
        if (_depth > 0 && _offset >= Stack[_depth - 1].End)
        {
            _depth--;
            _tokenType = Stack[_depth].IsObject ? JsonTokenType.EndObject : JsonTokenType.EndArray;
            return true;
        }

        if (_depth == 0)
        {
            if (_rootConsumed) return false;
            _rootConsumed = true;
            return ReadValue();
        }

        ref Frame top = ref Stack[_depth - 1];
        if (top.IsObject)
        {
            if (top.NextIsName)
            {
                _offset = ReadString(_data, _offset, out _valueSpan);
                _isNumber = false;
                top.NextIsName = false;
                _tokenType = JsonTokenType.PropertyName;
                return true;
            }

            top.NextIsName = true; // value follows; next entry needs a name again
        }

        return ReadValue();
    }

    public void Skip()
    {
        // Mirrors Utf8JsonReader.Skip: on a container start, advance to (and land
        // on) the matching end; on a scalar, no-op. O(1) here via the prefix.
        if (_tokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            _depth--;
            Frame f = Stack[_depth];
            _offset = f.End;
            _tokenType = f.IsObject ? JsonTokenType.EndObject : JsonTokenType.EndArray;
        }
    }

    public readonly bool ValueTextEquals(ReadOnlySpan<byte> utf8Text) => ValueSpan.SequenceEqual(utf8Text);

    public readonly int CopyString(scoped Span<byte> destination)
    {
        ValueSpan.CopyTo(destination);
        return ValueSpan.Length;
    }

    private bool ReadValue()
    {
        byte marker = _data[_offset];

        if (IsInlineString(marker) || marker == 0xC1)
        {
            _offset = ReadString(_data, _offset, out _valueSpan);
            _isNumber = false;
            _tokenType = JsonTokenType.String;
            return true;
        }

        switch (marker)
        {
            case 0xDA: // int32
            {
                int value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_offset + 1));
                Utf8Formatter.TryFormat(value, NumberScratch, out _numberLen);
                _isNumber = true;
                _offset += 5;
                _tokenType = JsonTokenType.Number;
                return true;
            }
            case 0xCC: // double — out of spike scope for byte-identity (corpus is int-only)
            {
                double value = BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_offset + 1));
                Utf8Formatter.TryFormat(value, NumberScratch, out _numberLen);
                _isNumber = true;
                _offset += 9;
                _tokenType = JsonTokenType.Number;
                return true;
            }
            case 0xD2: // true
                _isNumber = false;
                _offset += 1;
                _tokenType = JsonTokenType.True;
                return true;
            case 0xD1: // false
                _isNumber = false;
                _offset += 1;
                _tokenType = JsonTokenType.False;
                return true;
            case 0xD0: // null
                _isNumber = false;
                _offset += 1;
                _tokenType = JsonTokenType.Null;
                return true;
            default:
                _isNumber = false;
                if (IsArrayMarker(marker))
                {
                    PushContainer(isObject: false);
                    _tokenType = JsonTokenType.StartArray;
                    return true;
                }
                if (IsObjectMarker(marker))
                {
                    PushContainer(isObject: true);
                    _tokenType = JsonTokenType.StartObject;
                    return true;
                }
                throw new InvalidOperationException($"spike: unsupported marker 0x{marker:X2}");
        }
    }

    private void PushContainer(bool isObject)
    {
        int end = ContainerDataEnd(_data, _offset);
        _offset = ContainerDataStart(_data, _offset);
        Stack[_depth] = new Frame { End = end, IsObject = isObject, NextIsName = isObject };
        _depth++;
    }

    // ---- Shared binary primitives (kept identical to the production decoder's
    // container math; see CosmosBinaryDecoder.ContainerPrefixLength). ----

    private static int ReadString(ReadOnlySpan<byte> full, int off, out ReadOnlySpan<byte> value)
    {
        byte marker = full[off];
        if (IsInlineString(marker))
        {
            int len = marker - 0x80;
            value = full.Slice(off + 1, len);
            return off + 1 + len;
        }

        if (marker == 0xC1)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(off + 1));
            value = full.Slice(off + 3, len);
            return off + 3 + len;
        }

        throw new InvalidOperationException($"spike: expected string marker, got 0x{marker:X2}");
    }

    private static int ContainerPrefixLength(byte marker) => marker switch
    {
        0xE5 or 0xED => 3,
        0xE6 or 0xEE => 5,
        0xE7 or 0xEF => 9,
        0xE0 or 0xE8 => 1,
        _ => throw new InvalidOperationException($"spike: not a container marker 0x{marker:X2}"),
    };

    private static int ContainerPayloadLength(ReadOnlySpan<byte> full, int off)
    {
        byte marker = full[off];
        return marker switch
        {
            0xE5 or 0xED => full[off + 1],
            0xE6 or 0xEE => BinaryPrimitives.ReadUInt16LittleEndian(full.Slice(off + 1)),
            0xE7 or 0xEF => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(full.Slice(off + 1))),
            0xE0 or 0xE8 => 0,
            _ => throw new InvalidOperationException($"spike: not a container marker 0x{marker:X2}"),
        };
    }

    private static int ContainerDataStart(ReadOnlySpan<byte> full, int off)
        => off + ContainerPrefixLength(full[off]);

    private static int ContainerDataEnd(ReadOnlySpan<byte> full, int off)
        => off + ContainerPrefixLength(full[off]) + ContainerPayloadLength(full, off);

    private static bool IsInlineString(byte marker) => marker is >= 0x80 and < 0xC0;

    private static bool IsArrayMarker(byte marker) => marker is 0xE0 or 0xE5 or 0xE6 or 0xE7;

    private static bool IsObjectMarker(byte marker) => marker is 0xE8 or 0xED or 0xEE or 0xEF;
}
