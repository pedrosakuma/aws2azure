using System.Buffers;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Connection;

/// <summary>
/// Shared in-process AMQP "broker" helpers used by the link/session
/// regression tests. Encodes the conventions established during
/// Phase 2.5 (broker echoes End/Detach on its own session channel, not
/// on the channel the client used — AMQP channels are asymmetric).
/// </summary>
internal static class AmqpTestBroker
{
    internal delegate void PerfWriter<T>(Span<byte> destination, in T value, out int written);

    internal static async Task SendPerfAsync<T>(IAmqpTransport transport, ushort channel, T value, PerfWriter<T> writer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            writer(rented, in value, out var n);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Amqp, channel, rented.AsMemory(0, n), default, 64 * 1024);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    internal static async Task SendTransferPayloadAsync(
        IAmqpTransport transport, ushort channel, uint handle,
        uint? deliveryId, ReadOnlyMemory<byte> deliveryTag,
        ReadOnlyMemory<byte> payload, bool more, bool aborted = false)
    {
        var transfer = new AmqpTransfer
        {
            Handle = handle,
            DeliveryId = deliveryId,
            DeliveryTag = deliveryTag,
            MessageFormat = deliveryId is null ? null : (uint?)0,
            Settled = false,
            More = more,
            Aborted = aborted ? true : null,
        };
        var perfRented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        AmqpTransfer.Write(perfRented, in transfer, out var tlen);
        var frame = ArrayPool<byte>.Shared.Rent(tlen + payload.Length);
        perfRented.AsSpan(0, tlen).CopyTo(frame);
        payload.Span.CopyTo(frame.AsSpan(tlen));
        await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Amqp, channel, frame.AsMemory(0, tlen + payload.Length));
        ArrayPool<byte>.Shared.Return(frame);
        ArrayPool<byte>.Shared.Return(perfRented);
    }

    internal static async Task ConsumeOpenAsync(IAmqpTransport server, uint maxFrameSize = 8192)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server, (int)Math.Max(maxFrameSize, AmqpFrameIO.InitialMaxFrameSize))) { }
        await SendPerfAsync(server, channel: 0, new AmqpOpen
        {
            ContainerId = "s", MaxFrameSize = maxFrameSize, ChannelMax = 0xFFFF,
        }, AmqpOpen.Write);
    }

    internal static async Task ConsumeBeginAndReply(IAmqpTransport server, ushort peerChannel)
    {
        using var f = await AmqpFrameIO.ReadFrameAsync(server);
        AmqpBegin.Read(f.Body, out var begin, out _);
        await SendPerfAsync(server, peerChannel, new AmqpBegin
        {
            RemoteChannel = f.Header.Channel,
            NextOutgoingId = 0,
            IncomingWindow = begin.OutgoingWindow,
            OutgoingWindow = begin.IncomingWindow,
            HandleMax = 255,
        }, AmqpBegin.Write);
    }

    internal static async Task ConsumeCloseAsync(IAmqpTransport server)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
        await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
    }

    internal static async Task DrainUntilCloseAsync(IAmqpTransport server, ushort peerSessionChannel = 4)
    {
        try
        {
            while (true)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server, 64 * 1024);
                var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                if (kind == PerformativeKind.Close)
                {
                    await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
                    return;
                }
                if (kind == PerformativeKind.End)
                {
                    await SendPerfAsync(server, channel: peerSessionChannel, new AmqpEnd(), AmqpEnd.Write);
                }
                else if (kind == PerformativeKind.Detach)
                {
                    AmqpDetach.Read(f.Body, out var d, out _);
                    await SendPerfAsync(server, channel: peerSessionChannel, new AmqpDetach { Handle = d.Handle, Closed = true }, AmqpDetach.Write);
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (IOException) { }
    }
}
