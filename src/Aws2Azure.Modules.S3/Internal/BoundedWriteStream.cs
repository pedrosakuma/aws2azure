namespace Aws2Azure.Modules.S3.Internal;

internal sealed class BoundedWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _written;

    public BoundedWriteStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _written; set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _written += buffer.Length;
        if (_written > _limit)
        {
            ThrowTooLarge();
        }

        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _written += count;
        if (_written > _limit)
        {
            ThrowTooLarge();
        }

        _inner.Write(buffer, offset, count);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void ThrowTooLarge() =>
        throw new InvalidDataException($"Body exceeded the {_limit}-byte limit.");
}
