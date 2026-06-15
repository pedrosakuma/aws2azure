using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// <see cref="ITokenWriter"/> that emits the Cosmos DB <b>binary</b> JSON format
/// (the encode analog of <see cref="CosmosBinaryDecoder"/>). The body starts
/// with the <c>0x80</c> format marker; the root value follows at offset 1.
///
/// <para>Containers use the length-and-count (<c>LC4</c>) framing
/// (<c>0xEF</c> object / <c>0xE7</c> array) with an 8-byte prefix — a
/// little-endian <c>uint32</c> payload length followed by a <c>uint32</c>
/// element count — reserved on open and backpatched on close, so the walk
/// stays single-pass and streaming (no pre-counting of children).</para>
///
/// <para>Strings are written verbatim (UTF-8, not JSON-escaped) using the
/// narrowest length marker (<c>0x80+len</c> inline &lt; 64, else <c>0xC0</c>
/// uint8 / <c>0xC1</c> uint16 / <c>0xC2</c> uint32). Bare numbers are
/// re-encoded from canonical decimal text to the narrowest exact marker
/// (<c>0xCA</c> int32 / <c>0xCB</c> int64 / <c>0xCC</c> double).</para>
///
/// <para>The document is assembled in a pooled scratch buffer (backpatching
/// needs random access) and copied to the destination
/// <see cref="IBufferWriter{T}"/> on <see cref="Flush"/>; <see cref="Dispose"/>
/// returns the pooled arrays.</para>
/// </summary>
internal sealed class CosmosBinaryWriter : ITokenWriter, IDisposable
{
    private const byte BinaryFormatMarker = 0x80;

    private readonly IBufferWriter<byte> _output;
    private byte[] _buffer;
    private int _position;

    private Frame[] _frames;
    private int _depth;
    private bool _disposed;

    public CosmosBinaryWriter(IBufferWriter<byte> output, int initialCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 16));
        _frames = ArrayPool<Frame>.Shared.Rent(16);
        WriteByte(BinaryFormatMarker);
    }

    public void WriteStartObject()
    {
        OnBeforeValue();
        WriteByte(0xEF);
        PushFrame(isObject: true);
    }

    public void WriteEndObject() => PopFrame();

    public void WriteStartArray()
    {
        OnBeforeValue();
        WriteByte(0xE7);
        PushFrame(isObject: false);
    }

    public void WriteEndArray() => PopFrame();

    public void WritePropertyName(in TokenName name)
    {
        CountProperty();
        WriteStringToken(name.Utf8Raw);
    }

    public void WritePropertyName(string name)
    {
        CountProperty();
        WriteStringToken(name);
    }

    public void WriteString(in TokenName name, string? value)
    {
        WritePropertyName(name);
        WriteStringValue(value);
    }

    public void WriteString(in TokenName name, in TokenName value)
    {
        WritePropertyName(name);
        OnBeforeValue();
        WriteStringToken(value.Utf8Raw);
    }

    public void WriteStringValue(string? value)
    {
        OnBeforeValue();
        WriteStringToken(value ?? string.Empty);
    }

    public void WriteBooleanValue(bool value)
    {
        OnBeforeValue();
        WriteByte(value ? (byte)0xD2 : (byte)0xD1);
    }

    public void WriteNullValue()
    {
        OnBeforeValue();
        WriteByte(0xD0);
    }

    public void WriteNumberRaw(string canonicalDecimal)
    {
        OnBeforeValue();
        ReadOnlySpan<char> text = canonicalDecimal;

        // Canonical DDB form has no exponent (TryNormalizeDdbNumber expands it),
        // so an integer is exactly the absence of a decimal point.
        if (text.IndexOf('.') < 0
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            if (integer is >= int.MinValue and <= int.MaxValue)
            {
                WriteByte(0xCA);
                BinaryPrimitives.WriteInt32LittleEndian(Take(4), (int)integer);
            }
            else
            {
                WriteByte(0xCB);
                BinaryPrimitives.WriteInt64LittleEndian(Take(8), integer);
            }

            return;
        }

        double value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        WriteByte(0xCC);
        BinaryPrimitives.WriteDoubleLittleEndian(Take(8), value);
    }

    public void Flush()
    {
        if (_depth != 0)
        {
            throw new InvalidOperationException("CosmosBinaryWriter flushed with unbalanced containers.");
        }

        _output.Write(_buffer.AsSpan(0, _position));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<Frame>.Shared.Return(_frames);
    }

    // -- structural framing -------------------------------------------------

    private void PushFrame(bool isObject)
    {
        int prefixOffset = _position;
        Take(8); // LC4 prefix: uint32 payload length + uint32 count, backpatched on close.
        if (_depth == _frames.Length)
        {
            GrowFrames();
        }

        _frames[_depth++] = new Frame(prefixOffset, _position, isObject);
    }

    private void PopFrame()
    {
        Frame frame = _frames[--_depth];
        int payloadLength = _position - frame.PayloadStart;
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(frame.PrefixOffset), (uint)payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(frame.PrefixOffset + 4), (uint)frame.Count);
    }

    private void OnBeforeValue()
    {
        // A value written directly inside an array is a counted element. A value
        // written inside an object is a property value already counted by its
        // name (see CountProperty); the root value (depth 0) is uncounted.
        if (_depth > 0 && !_frames[_depth - 1].IsObject)
        {
            _frames[_depth - 1].Count++;
        }
    }

    private void CountProperty() => _frames[_depth - 1].Count++;

    // -- primitives ---------------------------------------------------------

    private void WriteStringToken(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteStringHeader(byteCount);
        Encoding.UTF8.GetBytes(value, Take(byteCount));
    }

    private void WriteStringToken(ReadOnlySpan<byte> utf8)
    {
        WriteStringHeader(utf8.Length);
        utf8.CopyTo(Take(utf8.Length));
    }

    private void WriteStringHeader(int length)
    {
        if (length < 64)
        {
            WriteByte((byte)(0x80 + length));
        }
        else if (length <= byte.MaxValue)
        {
            WriteByte(0xC0);
            WriteByte((byte)length);
        }
        else if (length <= ushort.MaxValue)
        {
            WriteByte(0xC1);
            BinaryPrimitives.WriteUInt16LittleEndian(Take(2), (ushort)length);
        }
        else
        {
            WriteByte(0xC2);
            BinaryPrimitives.WriteUInt32LittleEndian(Take(4), (uint)length);
        }
    }

    private void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    private Span<byte> Take(int count)
    {
        EnsureCapacity(count);
        Span<byte> span = _buffer.AsSpan(_position, count);
        _position += count;
        return span;
    }

    private void EnsureCapacity(int additional)
    {
        if (_position + additional <= _buffer.Length)
        {
            return;
        }

        int newSize = Math.Max(_buffer.Length * 2, _position + additional);
        byte[] next = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _position).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    private void GrowFrames()
    {
        Frame[] next = ArrayPool<Frame>.Shared.Rent(_frames.Length * 2);
        Array.Copy(_frames, next, _depth);
        ArrayPool<Frame>.Shared.Return(_frames);
        _frames = next;
    }

    private struct Frame(int prefixOffset, int payloadStart, bool isObject)
    {
        public readonly int PrefixOffset = prefixOffset;
        public readonly int PayloadStart = payloadStart;
        public readonly bool IsObject = isObject;
        public int Count = 0;
    }
}
