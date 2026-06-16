namespace Aws2Azure.Modules.S3.Internal;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    public BoundedReadStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_read >= _limit)
        {
            ThrowTooLarge();
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _read += read;
        if (_read > _limit)
        {
            ThrowTooLarge();
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void ThrowTooLarge() =>
        throw new InvalidDataException($"Request body exceeded the {_limit}-byte limit.");
}
