using System.Buffers;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Transport;

public sealed class AmqpFrameIOTests
{
    [Fact]
    public async Task Write_then_read_roundtrips_single_frame()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        var body = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        await AmqpFrameIO.WriteFrameAsync(local, AmqpFrameType.Amqp, channel: 7, body);

        using var frame = await AmqpFrameIO.ReadFrameAsync(peer);
        Assert.Equal(AmqpFrameType.Amqp, frame.Header.Type);
        Assert.Equal((ushort)7, frame.Header.Channel);
        Assert.Equal(8 + body.Length, frame.Header.Size);
        Assert.Equal(body, frame.Body.ToArray());
    }

    [Fact]
    public async Task Sasl_frame_channel_is_forced_to_zero()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        await AmqpFrameIO.WriteFrameAsync(local, AmqpFrameType.Sasl, channel: 42, ReadOnlyMemory<byte>.Empty);
        using var frame = await AmqpFrameIO.ReadFrameAsync(peer);
        Assert.Equal(AmqpFrameType.Sasl, frame.Header.Type);
        Assert.Equal((ushort)0, frame.Header.Channel);
        Assert.Equal(0, frame.Body.Length);
    }

    [Fact]
    public async Task Read_assembles_frame_from_fragmented_writes()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        // Encode a 12-byte frame (8 header + 4 body) manually.
        var body = new byte[] { 1, 2, 3, 4 };
        var full = new byte[12];
        AmqpFrameCodec.WriteFrame(full, AmqpFrameType.Amqp, channel: 0, body);

        var readTask = AmqpFrameIO.ReadFrameAsync(peer).AsTask();

        // Write in 3 chunks across boundaries (header split, then body split).
        await local.Output.WriteAsync(full.AsMemory(0, 3));
        await Task.Delay(15);
        await local.Output.WriteAsync(full.AsMemory(3, 6));
        await Task.Delay(15);
        await local.Output.WriteAsync(full.AsMemory(9, 3));

        using var frame = await readTask;
        Assert.Equal(body, frame.Body.ToArray());
    }

    [Fact]
    public async Task Oversized_frame_rejected_on_read()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        // Hand-craft an 8-byte header declaring a 16 MiB frame.
        var header = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 16 * 1024 * 1024);
        header[4] = AmqpFrameCodec.MinDoff;
        header[5] = (byte)AmqpFrameType.Amqp;
        await local.Output.WriteAsync(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => AmqpFrameIO.ReadFrameAsync(peer).AsTask());
    }

    [Fact]
    public async Task Premature_eof_during_header_throws_EndOfStream()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        // Only 3 bytes then complete.
        await local.Output.WriteAsync(new byte[] { 0, 0, 0 });
        await local.Output.CompleteAsync();

        await Assert.ThrowsAsync<EndOfStreamException>(() => AmqpFrameIO.ReadFrameAsync(peer).AsTask());
    }

    [Fact]
    public async Task Premature_eof_mid_body_throws_EndOfStream()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        // Header declaring 20-byte frame, then only 4 body bytes, then close.
        var header = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 20);
        header[4] = AmqpFrameCodec.MinDoff;
        header[5] = (byte)AmqpFrameType.Amqp;
        await local.Output.WriteAsync(header);
        await local.Output.WriteAsync(new byte[] { 9, 9, 9, 9 });
        await local.Output.CompleteAsync();

        await Assert.ThrowsAsync<EndOfStreamException>(() => AmqpFrameIO.ReadFrameAsync(peer).AsTask());
    }

    [Fact]
    public async Task Two_back_to_back_frames_decode_independently()
    {
        var (local, peer) = PipePairTransport.CreatePair();
        await using var _ = local; await using var __ = peer;

        await AmqpFrameIO.WriteFrameAsync(local, AmqpFrameType.Amqp, channel: 1, new byte[] { 0xAB });
        await AmqpFrameIO.WriteFrameAsync(local, AmqpFrameType.Amqp, channel: 2, new byte[] { 0xCD, 0xEF });

        using (var f1 = await AmqpFrameIO.ReadFrameAsync(peer))
        {
            Assert.Equal((ushort)1, f1.Header.Channel);
            Assert.Equal(new byte[] { 0xAB }, f1.Body.ToArray());
        }
        using (var f2 = await AmqpFrameIO.ReadFrameAsync(peer))
        {
            Assert.Equal((ushort)2, f2.Header.Channel);
            Assert.Equal(new byte[] { 0xCD, 0xEF }, f2.Body.ToArray());
        }
    }

    [Fact]
    public async Task Write_above_initial_max_throws()
    {
        var (local, _peer) = PipePairTransport.CreatePair();
        await using var _ = local;

        var huge = new byte[AmqpFrameIO.InitialMaxFrameSize]; // header push us over
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AmqpFrameIO.WriteFrameAsync(local, AmqpFrameType.Amqp, 0, huge).AsTask());
    }

    [Fact]
    public void RentedFrame_dispose_is_idempotent()
    {
        var rented = ArrayPool<byte>.Shared.Rent(8);
        var f = new RentedFrame(new AmqpFrameHeader(8, 8, AmqpFrameType.Amqp, 0), rented, 0);
        f.Dispose();
        f.Dispose(); // second call must not throw / double-return
        Assert.Throws<ObjectDisposedException>(() => f.Body);
    }
}
