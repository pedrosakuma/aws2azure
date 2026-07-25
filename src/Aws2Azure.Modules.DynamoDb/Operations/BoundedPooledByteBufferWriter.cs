using System;
using System.Buffers;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal sealed class BoundedPooledByteBufferWriter
    : IBufferWriter<byte>, IDisposable
{
    private readonly int _maximumCapacity;
    private readonly int _maximumScratchSizeHint;
    private byte[] _buffer;
    private byte[]? _scratch;
    private int _index;
    private int _available;
    private bool _usingScratch;
    private BoundedBufferWriterLimitException? _limitException;

    public BoundedPooledByteBufferWriter(
        int maximumCapacity,
        int initialCapacity,
        int maximumScratchSizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumScratchSizeHint,
            maximumCapacity);

        _maximumCapacity = maximumCapacity;
        _maximumScratchSizeHint = maximumScratchSizeHint;
        _buffer = ArrayPool<byte>.Shared.Rent(
            Math.Min(maximumCapacity, Math.Max(initialCapacity, 256)));
    }

    public int MaximumCapacity => _maximumCapacity;

    public ReadOnlyMemory<byte> WrittenMemory =>
        _buffer.AsMemory(0, _index);

    public void Advance(int count)
    {
        if (_limitException is { } previousLimit)
        {
            throw previousLimit;
        }
        if (count < 0 || count > _available)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (_usingScratch)
        {
            var scratch = _scratch!;
            _scratch = null;
            _usingScratch = false;
            _available = 0;
            if (count > _maximumCapacity - _index)
            {
                ArrayPool<byte>.Shared.Return(scratch);
                _limitException = new BoundedBufferWriterLimitException(
                    _maximumCapacity,
                    _index,
                    count);
                throw _limitException;
            }

            EnsureCapacity(count);
            scratch.AsSpan(0, count).CopyTo(_buffer.AsSpan(_index));
            ArrayPool<byte>.Shared.Return(scratch);
            _index += count;
            return;
        }

        _index += count;
        _available = 0;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Prepare(sizeHint);
        return _usingScratch
            ? _scratch!.AsMemory(0, _available)
            : _buffer.AsMemory(_index, _available);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Prepare(sizeHint);
        return _usingScratch
            ? _scratch!.AsSpan(0, _available)
            : _buffer.AsSpan(_index, _available);
    }

    private void Prepare(int sizeHint)
    {
        if (_limitException is { } previousLimit)
        {
            throw previousLimit;
        }
        if (_available != 0 || _usingScratch)
        {
            throw new InvalidOperationException(
                "Advance must be called before requesting another buffer.");
        }

        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        var remaining = _maximumCapacity - _index;
        if (sizeHint <= remaining)
        {
            EnsureCapacity(sizeHint);
            _available = Math.Min(_buffer.Length - _index, remaining);
            return;
        }

        if (sizeHint > _maximumScratchSizeHint)
        {
            _limitException = new BoundedBufferWriterLimitException(
                _maximumCapacity,
                _index,
                sizeHint);
            throw _limitException;
        }

        _scratch = ArrayPool<byte>.Shared.Rent(sizeHint);
        _usingScratch = true;
        _available = sizeHint;
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint <= _buffer.Length - _index)
        {
            return;
        }

        var needed = checked(_index + sizeHint);
        var doubled = _buffer.Length <= _maximumCapacity / 2
            ? _buffer.Length * 2
            : _maximumCapacity;
        var requested = Math.Min(
            _maximumCapacity,
            Math.Max(needed, doubled));
        var next = ArrayPool<byte>.Shared.Rent(requested);
        _buffer.AsSpan(0, _index).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        if (_buffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
        if (_scratch is { } scratch)
        {
            ArrayPool<byte>.Shared.Return(scratch);
            _scratch = null;
        }
        _index = 0;
        _available = 0;
        _usingScratch = false;
        _limitException = null;
    }
}

internal sealed class BoundedBufferWriterLimitException(
    int limit,
    int writtenBytes,
    int requestedBytes) : Exception
{
    public int Limit { get; } = limit;
    public int WrittenBytes { get; } = writtenBytes;
    public int RequestedBytes { get; } = requestedBytes;
}
