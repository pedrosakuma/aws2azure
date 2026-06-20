namespace Aws2Azure.TestSupport.Http;

/// <summary>
/// Response-body stream that mimics Kestrel running with
/// <c>AllowSynchronousIO=false</c>: every synchronous <see cref="Stream.Write(byte[],int,int)"/>
/// or <see cref="Stream.Flush"/> throws, while the async paths are honoured.
/// Captures everything written so a test can assert the full body landed.
/// </summary>
/// <remarks>
/// This is the canonical guard for the repo-wide invariant that response
/// writers must never anchor a <c>Utf8JsonWriter</c>/<c>XmlWriter</c> at
/// <c>context.Response.Body</c>: such a writer auto-flushes synchronously once
/// its internal buffer (~16 KB) fills mid-serialization, which Kestrel rejects
/// after the status line is committed, truncating the response irreversibly.
/// Drive a module's large (&gt;16 KB) response writer through this stream to
/// prove it buffers off-pipe then writes once asynchronously. See issue #449
/// (follow-up to #436/#448).
/// </remarks>
public sealed class SyncThrowingStream : Stream
{
    private const string Message =
        "Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.";

    private readonly MemoryStream _inner = new();

    /// <summary>The bytes written so far via the async paths.</summary>
    public byte[] WrittenBytes => _inner.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() => throw new InvalidOperationException(Message);

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException(Message);

    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException(Message);

    public override void WriteByte(byte value) => throw new InvalidOperationException(Message);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
