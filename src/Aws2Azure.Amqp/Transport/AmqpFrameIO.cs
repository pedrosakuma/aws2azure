using System.Buffers;
using System.IO.Pipelines;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Transport;

/// <summary>
/// Pipe-based frame I/O on top of <see cref="IAmqpTransport"/>: writes
/// complete AMQP frames (header + body) and reads one frame at a time.
/// Body buffers are rented from <see cref="ArrayPool{T}"/> and returned
/// when the caller disposes the <see cref="RentedFrame"/>.
/// </summary>
internal static class AmqpFrameIO
{
    /// <summary>
    /// Hard ceiling on a single frame size before SASL/Open negotiates a
    /// real <c>max-frame-size</c>. Spec §2.2 mandates ≥512; we use 4 KiB
    /// which comfortably fits any SASL or Open performative.
    /// </summary>
    public const int InitialMaxFrameSize = 4 * 1024;

    /// <summary>
    /// Encodes a frame with the given type/channel/body and writes it to
    /// the transport. Flushes the underlying pipe. The header+body are
    /// staged in a pooled buffer to avoid heap churn on the hot path.
    /// </summary>
    /// <param name="maxFrameSize">
    /// Maximum outgoing frame size in bytes. Pass
    /// <see cref="InitialMaxFrameSize"/> before <c>open</c> negotiation
    /// completes; after that pass the value derived from the peer's
    /// <c>open.max-frame-size</c>.
    /// </param>
    public static async ValueTask WriteFrameAsync(
        IAmqpTransport transport,
        AmqpFrameType type,
        ushort channel,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default,
        int maxFrameSize = InitialMaxFrameSize)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var total = AmqpFrameCodec.HeaderSize + body.Length;
        if (total > maxFrameSize)
            throw new InvalidOperationException(
                $"Frame size {total} exceeds the {maxFrameSize}-byte max-frame-size.");

        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var written = AmqpFrameCodec.WriteFrame(rented.AsSpan(0, total), type, channel, body.Span);
            var flush = await transport.Output.WriteAsync(rented.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            if (flush.IsCompleted)
                throw new EndOfStreamException("Transport completed during frame write.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reads exactly one frame from the transport. The returned
    /// <see cref="RentedFrame"/> owns a pooled buffer; the caller must
    /// dispose it (use <c>using var</c>).
    /// </summary>
    public static async ValueTask<RentedFrame> ReadFrameAsync(
        IAmqpTransport transport,
        int maxFrameSize = InitialMaxFrameSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var reader = transport.Input;

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length >= AmqpFrameCodec.HeaderSize)
            {
                var header = ParseHeader(buffer);
                var frameSize = header.Size;

                if (frameSize > maxFrameSize)
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    throw new InvalidDataException(
                        $"Incoming frame size {frameSize} exceeds negotiated max-frame-size {maxFrameSize}.");
                }

                if (buffer.Length >= frameSize)
                {
                    var bodyLength = frameSize - header.DataOffset;
                    var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, bodyLength));
                    try
                    {
                        if (bodyLength > 0)
                            buffer.Slice(header.DataOffset, bodyLength).CopyTo(rented.AsSpan(0, bodyLength));
                        reader.AdvanceTo(buffer.GetPosition(frameSize));
                        return new RentedFrame(header, rented, bodyLength);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        throw;
                    }
                }
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException(
                    $"Transport closed mid-frame: got {buffer.Length} bytes (need at least header + body).");
            }

            // Not enough bytes yet — examine everything so the next ReadAsync blocks.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static AmqpFrameHeader ParseHeader(System.Buffers.ReadOnlySequence<byte> buffer)
    {
        var slice = buffer.Slice(0, AmqpFrameCodec.HeaderSize);
        if (slice.IsSingleSegment)
            return AmqpFrameCodec.ReadHeader(slice.First.Span);

        Span<byte> tmp = stackalloc byte[AmqpFrameCodec.HeaderSize];
        slice.CopyTo(tmp);
        return AmqpFrameCodec.ReadHeader(tmp);
    }
}

/// <summary>
/// Owns a pooled byte buffer for the body of an AMQP frame. Dispose to
/// return the buffer to <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
internal sealed class RentedFrame : IDisposable
{
    private byte[]? _rented;
    private readonly int _length;

    internal RentedFrame(AmqpFrameHeader header, byte[] rented, int length)
    {
        Header = header;
        _rented = rented;
        _length = length;
    }

    public AmqpFrameHeader Header { get; }

    /// <summary>The frame body (excludes the 8-byte header and any extended header).</summary>
    public ReadOnlyMemory<byte> Body
    {
        get
        {
            var r = _rented ?? throw new ObjectDisposedException(nameof(RentedFrame));
            return r.AsMemory(0, _length);
        }
    }

    public void Dispose()
    {
        var r = _rented;
        if (r is null) return;
        _rented = null;
        ArrayPool<byte>.Shared.Return(r);
    }
}
