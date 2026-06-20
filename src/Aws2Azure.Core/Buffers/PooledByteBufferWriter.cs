using System;
using System.Buffers;

namespace Aws2Azure.Core.Buffers;

/// <summary>
/// Minimal growable <see cref="IBufferWriter{T}"/> backed by
/// <see cref="ArrayPool{T}"/>, so a response transform allocates no per-request
/// output array. Not thread-safe; rent-use-dispose within a single request.
/// Mirrors the (internal) <c>PooledByteBufferWriter</c> shape
/// <c>System.Text.Json</c> uses for the same purpose.
///
/// <para>Shared so every module can honour the "error wall": build the whole
/// response into this scratch buffer and only then hand it to
/// <c>HttpResponse.BodyWriter</c> in a single
/// <see cref="System.IO.Pipelines.PipeWriter.WriteAsync"/>. Writing a
/// <see cref="System.Text.Json.Utf8JsonWriter"/> straight at the response
/// stream is unsafe: a <see cref="System.IO.Stream"/>-backed writer issues
/// blocking flushes once its internal buffer fills, which Kestrel rejects
/// (<c>AllowSynchronousIO=false</c>) after the status line is already
/// committed, truncating the response.</para>
/// </summary>
public sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _index;

    public PooledByteBufferWriter(int initialCapacity = 1024)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 256));
        _index = 0;
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _index);

    public void Advance(int count)
    {
        if (count < 0 || _index > _buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (sizeHint <= _buffer.Length - _index) return;

        int needed = _index + sizeHint;
        int newSize = Math.Max(needed, _buffer.Length * 2);
        byte[] next = ArrayPool<byte>.Shared.Rent(newSize);
        Array.Copy(_buffer, next, _index);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        if (_buffer.Length == 0) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _index = 0;
    }
}
