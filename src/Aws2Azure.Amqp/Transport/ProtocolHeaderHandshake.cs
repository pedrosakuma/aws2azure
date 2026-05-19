using System.Buffers;
using System.IO.Pipelines;

namespace Aws2Azure.Amqp.Transport;

/// <summary>
/// Implements the AMQP §2.2 protocol-header exchange: both peers send
/// their proposed 8-byte protocol header immediately on connect, then read
/// the peer's header before any framed traffic.
/// </summary>
/// <remarks>
/// Used twice on a Service Bus connection: once with the SASL header
/// (<see cref="Framing.AmqpFrameCodec.SaslProtocolHeader"/>) to negotiate
/// the security layer, and again with the AMQP header
/// (<see cref="Framing.AmqpFrameCodec.AmqpProtocolHeader"/>) after the
/// SASL outcome to enter the AMQP 1.0 protocol proper.
/// </remarks>
internal static class ProtocolHeaderHandshake
{
    /// <summary>
    /// Writes <paramref name="localHeader"/> to <paramref name="transport"/>,
    /// flushes, then reads exactly 8 bytes from the peer and validates they
    /// match. On mismatch throws
    /// <see cref="AmqpProtocolHeaderMismatchException"/> carrying the peer
    /// bytes for diagnostics.
    /// </summary>
    public static async ValueTask PerformAsync(
        IAmqpTransport transport,
        ReadOnlyMemory<byte> localHeader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (localHeader.Length != 8)
            throw new ArgumentException("Protocol header must be exactly 8 bytes.", nameof(localHeader));

        // Per spec the two writes can be concurrent (both peers send first
        // before reading), but for client-initiated TCP the linear order is
        // fine and simpler to reason about.
        var flush = await transport.Output.WriteAsync(localHeader, cancellationToken).ConfigureAwait(false);
        if (flush.IsCompleted)
            throw new EndOfStreamException("Transport completed before peer protocol header could be sent.");

        // 8 bytes — cheap to rent from the pool; can't stackalloc across await.
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(8);
        try
        {
            var received = rented.AsMemory(0, 8);
            await ReadExactlyAsync(transport.Input, received, cancellationToken).ConfigureAwait(false);

            if (!received.Span.SequenceEqual(localHeader.Span))
                throw new AmqpProtocolHeaderMismatchException(localHeader, received.ToArray());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reads exactly <c>destination.Length</c> bytes from the pipe,
    /// honouring cancellation. Throws <see cref="EndOfStreamException"/> if
    /// the peer completes the stream before enough bytes arrive.
    /// </summary>
    private static async ValueTask ReadExactlyAsync(
        PipeReader reader,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var needed = destination.Length;
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length >= needed)
            {
                var slice = buffer.Slice(0, needed);
                slice.CopyTo(destination.Span);
                reader.AdvanceTo(slice.End);
                return;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException(
                    $"Transport closed before {needed} bytes of protocol header were received (got {buffer.Length}).");
            }

            // Not enough bytes yet — examine everything so the next ReadAsync blocks.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

}
