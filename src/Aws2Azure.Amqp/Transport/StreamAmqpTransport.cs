using System.IO.Pipelines;

namespace Aws2Azure.Amqp.Transport;

/// <summary>
/// <see cref="IAmqpTransport"/> backed by an arbitrary duplex
/// <see cref="Stream"/> (typically <see cref="System.Net.Sockets.NetworkStream"/>
/// wrapped in <see cref="System.Net.Security.SslStream"/>).
/// </summary>
/// <remarks>
/// Uses <see cref="PipeReader.Create(Stream, StreamPipeReaderOptions?)"/> and
/// <see cref="PipeWriter.Create(Stream, StreamPipeWriterOptions?)"/> so we
/// inherit pooled-buffer reads without writing a manual ring buffer.
/// AOT-safe: no reflection, no dynamic codegen.
/// </remarks>
internal sealed class StreamAmqpTransport : IAmqpTransport
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private int _disposed;

    public StreamAmqpTransport(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("Stream must support both read and write.", nameof(stream));

        _stream = stream;
        _leaveOpen = leaveOpen;

        // leaveOpen on the pipe wrappers is true because *we* own the stream
        // and close it ourselves in DisposeAsync (or skip on leaveOpen=true).
        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        catch { /* swallow on dispose */ }

        try
        {
            await Input.CompleteAsync().ConfigureAwait(false);
        }
        catch { /* swallow on dispose */ }

        if (!_leaveOpen)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
