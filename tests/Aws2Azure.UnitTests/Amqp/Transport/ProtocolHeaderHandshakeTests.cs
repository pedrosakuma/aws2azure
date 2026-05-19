using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using System.Buffers;

namespace Aws2Azure.UnitTests.Amqp.Transport;

public sealed class ProtocolHeaderHandshakeTests
{
    [Fact]
    public async Task Symmetric_amqp_header_handshake_succeeds()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        var localTask = ProtocolHeaderHandshake.PerformAsync(local, AmqpFrameCodec.AmqpProtocolHeader.ToArray());
        var peerTask = ProtocolHeaderHandshake.PerformAsync(peer, AmqpFrameCodec.AmqpProtocolHeader.ToArray());

        await Task.WhenAll(localTask.AsTask(), peerTask.AsTask());
    }

    [Fact]
    public async Task Symmetric_sasl_header_handshake_succeeds()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        var localTask = ProtocolHeaderHandshake.PerformAsync(local, AmqpFrameCodec.SaslProtocolHeader.ToArray());
        var peerTask = ProtocolHeaderHandshake.PerformAsync(peer, AmqpFrameCodec.SaslProtocolHeader.ToArray());

        await Task.WhenAll(localTask.AsTask(), peerTask.AsTask());
    }

    [Fact]
    public async Task Mismatched_header_throws_with_peer_bytes()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        // We propose AMQP, peer proposes SASL — spec §2.2 negotiation mismatch.
        var localTask = ProtocolHeaderHandshake.PerformAsync(local, AmqpFrameCodec.AmqpProtocolHeader.ToArray());
        var peerTask = ProtocolHeaderHandshake.PerformAsync(peer, AmqpFrameCodec.SaslProtocolHeader.ToArray());

        var ex = await Assert.ThrowsAsync<AmqpProtocolHeaderMismatchException>(() => localTask.AsTask());
        Assert.Equal(8, ex.SentHeader.Length);
        Assert.Equal(8, ex.ReceivedHeader.Length);
        Assert.Equal(AmqpFrameCodec.AmqpProtocolHeader.ToArray(), ex.SentHeader);
        Assert.Equal(AmqpFrameCodec.SaslProtocolHeader.ToArray(), ex.ReceivedHeader);

        // Peer's read also fails (it sent SASL, got AMQP back) — drain to avoid leaking.
        await Assert.ThrowsAsync<AmqpProtocolHeaderMismatchException>(() => peerTask.AsTask());
    }

    [Fact]
    public async Task Peer_closes_before_sending_throws_EndOfStream()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local;

        // Peer completes its output without sending anything.
        await peer.Output.CompleteAsync();

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            ProtocolHeaderHandshake.PerformAsync(local, AmqpFrameCodec.AmqpProtocolHeader.ToArray()).AsTask());
    }

    [Fact]
    public async Task Partial_header_is_accumulated_across_reads()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        var header = AmqpFrameCodec.AmqpProtocolHeader.ToArray();

        // Local sends + waits to read.
        var localTask = ProtocolHeaderHandshake.PerformAsync(local, header);

        // Drain local's outgoing header off peer.Input so it doesn't block local's write.
        var drain = Task.Run(async () =>
        {
            var rented = new byte[8];
            var memory = rented.AsMemory();
            var read = 0;
            while (read < 8)
            {
                var res = await peer.Input.ReadAsync();
                var buf = res.Buffer;
                var slice = buf.Slice(0, Math.Min(buf.Length, 8 - read));
                slice.CopyTo(memory.Span[read..]);
                read += (int)slice.Length;
                peer.Input.AdvanceTo(slice.End);
            }
        });

        // Peer sends header in two chunks with a small delay.
        await peer.Output.WriteAsync(header.AsMemory(0, 3));
        await Task.Delay(20);
        await peer.Output.WriteAsync(header.AsMemory(3, 5));

        await localTask;
        await drain;
    }

    [Fact]
    public async Task Wrong_header_length_throws_ArgumentException()
    {
        var (local, _peer) = PipePairTransport.CreatePair();
        await using var _ = local;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ProtocolHeaderHandshake.PerformAsync(local, new byte[7]).AsTask());
    }
}
