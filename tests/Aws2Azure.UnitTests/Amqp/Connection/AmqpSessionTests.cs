using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Connection;

/// <summary>
/// Tests for <see cref="AmqpSession"/> lifecycle (Slice 5a): begin/end
/// handshake, peer-initiated end, multiplexing, double-open rejection.
/// </summary>
public sealed class AmqpSessionTests
{
    private static AmqpConnectionSettings DefaultSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task Begin_then_end_succeeds_with_channel_echo()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);

            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Begin, PerformativeCodec.PeekKind(f.Body.Span, out _));
                Assert.Equal((ushort)0, f.Header.Channel);
                AmqpBegin.Read(f.Body, out var clientBegin, out _);
                Assert.Null(clientBegin.RemoteChannel);
            }
            await SendPerfAsync(server, channel: 7, new AmqpBegin
            {
                RemoteChannel = 0, // echo of our outgoing channel
                NextOutgoingId = 0,
                IncomingWindow = 100,
                OutgoingWindow = 100,
            }, AmqpBegin.Write);

            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.End, PerformativeCodec.PeekKind(f.Body.Span, out _));
                Assert.Equal((ushort)0, f.Header.Channel);
            }
            await SendPerfAsync(server, channel: 7, new AmqpEnd(), AmqpEnd.Write);

            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        Assert.Equal((ushort)0, session.OutgoingChannel);
        Assert.Equal((ushort)7, session.RemoteChannel);
        Assert.Equal(100u, session.RemoteBegin.IncomingWindow);

        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Peer_initiated_end_triggers_mirror_reply()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var sawReplyEnd = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 3, new AmqpBegin
            {
                RemoteChannel = 0,
                NextOutgoingId = 0,
                IncomingWindow = 10,
                OutgoingWindow = 10,
            }, AmqpBegin.Write);

            // Wait a beat to ensure the client side has settled into Opened.
            await Task.Delay(50);
            // Server initiates end.
            await SendPerfAsync(server, channel: 3, new AmqpEnd(), AmqpEnd.Write);

            // Expect a mirrored end back on channel 0.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.End, PerformativeCodec.PeekKind(f.Body.Span, out _));
                Assert.Equal((ushort)0, f.Header.Channel);
                sawReplyEnd.TrySetResult(true);
            }
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();

        Assert.True(await sawReplyEnd.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(session.IsClosed);

        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Multiple_sessions_get_distinct_outgoing_channels()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            for (int i = 0; i < 3; i++)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                Assert.Equal(PerformativeKind.Begin, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpBegin.Read(f.Body, out var begin, out _);
                Assert.Null(begin.RemoteChannel);
                ushort ourChannel = f.Header.Channel;
                await SendPerfAsync(server, channel: (ushort)(100 + i), new AmqpBegin
                {
                    RemoteChannel = ourChannel,
                    NextOutgoingId = 0,
                    IncomingWindow = 1,
                    OutgoingWindow = 1,
                }, AmqpBegin.Write);
            }
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var s0 = await conn.BeginSessionAsync();
        var s1 = await conn.BeginSessionAsync();
        var s2 = await conn.BeginSessionAsync();

        Assert.Equal((ushort)0, s0.OutgoingChannel);
        Assert.Equal((ushort)1, s1.OutgoingChannel);
        Assert.Equal((ushort)2, s2.OutgoingChannel);
        Assert.Equal((ushort)100, s0.RemoteChannel);
        Assert.Equal((ushort)101, s1.RemoteChannel);
        Assert.Equal((ushort)102, s2.RemoteChannel);

        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Double_begin_on_same_session_rejected()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 9, new AmqpBegin
            {
                RemoteChannel = 0,
                NextOutgoingId = 0,
                IncomingWindow = 1,
                OutgoingWindow = 1,
            }, AmqpBegin.Write);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync());

        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Begin_without_open_throws()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        await Assert.ThrowsAsync<InvalidOperationException>(() => conn.BeginSessionAsync());
        await conn.DisposeAsync();
    }

    // -- helpers ---------------------------------------------------------

    private delegate void PerfWriter<T>(Span<byte> destination, in T value, out int written);

    private static async Task SendPerfAsync<T>(IAmqpTransport transport, ushort channel, T value, PerfWriter<T> writer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            writer(rented, in value, out var n);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Amqp, channel, rented.AsMemory(0, n));
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task ConsumeOpenAsync(IAmqpTransport server)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
        await SendPerfAsync(server, channel: 0, new AmqpOpen
        {
            ContainerId = "server",
            MaxFrameSize = 8192,
            ChannelMax = 0xFFFF,
        }, AmqpOpen.Write);
    }

    private static async Task ConsumeCloseAsync(IAmqpTransport server)
    {
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            Assert.Equal(PerformativeKind.Close, PerformativeCodec.PeekKind(f.Body.Span, out _));
        }
        await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
    }
}
