using System.Buffers;
using System.Text;

namespace Aws2Azure.Core.SigV4;

/// <summary>
/// A minimal growable byte buffer backed by <see cref="ArrayPool{T}"/>. Used by
/// the SigV4 hot path to assemble the canonical request and string-to-sign as
/// UTF-8 bytes without materializing intermediate <see cref="string"/>s.
/// </summary>
/// <remarks>
/// This is a <c>ref struct</c>: it must live entirely on the stack and the
/// caller MUST <see cref="Dispose"/> it (ideally via <c>using</c>) to return the
/// rented array. Encoding is performed with the shared UTF-8 encoder, so the
/// produced bytes are identical to <c>Encoding.UTF8.GetBytes(wholeString)</c>
/// provided each contiguous char span is written in a single call (so surrogate
/// pairs are never split across calls).
/// </remarks>
internal ref struct PooledByteWriter
{
    private byte[] _buffer;
    private int _position;

    public PooledByteWriter(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _position = 0;
    }

    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    /// <summary>UTF-8 encode <paramref name="chars"/> in one shot (surrogate-safe).</summary>
    public void WriteUtf8(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return;
        }

        EnsureCapacity(Encoding.UTF8.GetMaxByteCount(chars.Length));
        _position += Encoding.UTF8.GetBytes(chars, _buffer.AsSpan(_position));
    }

    /// <summary>Append the lowercase hexadecimal of <paramref name="bytes"/>.</summary>
    public void WriteLowerHex(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length * 2);
        var dest = _buffer.AsSpan(_position);
        WriteLowerHex(bytes, dest);
        _position += bytes.Length * 2;
    }

    /// <summary>Write the lowercase hexadecimal of <paramref name="bytes"/> into <paramref name="dest"/>.</summary>
    public static void WriteLowerHex(ReadOnlySpan<byte> bytes, Span<byte> dest)
    {
        ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            dest[i * 2] = hex[b >> 4];
            dest[i * 2 + 1] = hex[b & 0xF];
        }
    }

    private void EnsureCapacity(int additional)
    {
        if (_position + additional <= _buffer.Length)
        {
            return;
        }

        var required = _position + additional;
        var newSize = Math.Max(_buffer.Length * 2, required);
        var next = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _position).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        var rented = _buffer;
        _buffer = [];
        _position = 0;
        if (rented.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
