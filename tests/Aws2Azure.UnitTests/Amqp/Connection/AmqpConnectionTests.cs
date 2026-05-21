using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Connection;

/// <summary>
/// Tests for <see cref="AmqpConnection"/>'s open/close handshake and idle
/// heartbeat. The "server" side is a hand-rolled peer using
/// <see cref="AmqpFrameIO"/> so we exercise the full wire format.
/// </summary>
public sealed class AmqpConnectionTests
{
    private static AmqpConnectionSettings DefaultSettings(TimeSpan? idle = null) => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = idle ?? TimeSpan.FromSeconds(60),
    };

    [Fact]
    public async Task Open_then_close_succeeds()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            // Read client open.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(AmqpFrameType.Amqp, f.Header.Type);
                Assert.Equal(PerformativeKind.Open, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpOpen.Read(f.Body, out var open, out _);
                Assert.Equal("test-client", open.ContainerId);
            }
            // Send server open.
            await SendPerfAsync(server, new AmqpOpen
            {
                ContainerId = "server",
                MaxFrameSize = 8192,
                IdleTimeoutMilliseconds = 30_000,
            }, AmqpOpen.Write);

            // Read client close.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Close, PerformativeCodec.PeekKind(f.Body.Span, out _));
            }
            // Reply close.
            await SendPerfAsync(server, new AmqpClose(), AmqpClose.Write);
        });

        await conn.OpenAsync();
        Assert.Equal("server", conn.RemoteOpen.ContainerId);
        Assert.Equal(8192u, conn.EffectiveOutgoingMaxFrameSize);
        Assert.Equal(TimeSpan.FromSeconds(30), conn.EffectiveOutgoingIdleTimeout);

        await conn.CloseAsync();
        await serverTask;
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Peer_close_before_open_throws_with_condition()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { /* drain open */ }

            // Send close with an unauthorized-access error.
            var err = new AmqpError
            {
                Condition = AmqpErrorCondition.UnauthorizedAccess,
                Description = "nope",
            };
            var rented = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                AmqpError.Write(rented, in err, out var errLen);
                var close = new AmqpClose { Error = rented.AsMemory(0, errLen) };
                await SendPerfAsync(server, close, AmqpClose.Write);
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        });

        var ex = await Assert.ThrowsAsync<AmqpConnectionException>(() => conn.OpenAsync());
        Assert.Equal(AmqpErrorCondition.UnauthorizedAccess, ex.PeerCondition);
        Assert.Equal(AmqpErrorKind.Auth, ex.Kind);
        await serverTask;
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Heartbeat_is_sent_at_half_of_peer_idle_timeout()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

            // Tell client our idle timeout is 200 ms → heartbeats expected ~100 ms.
            await SendPerfAsync(server, new AmqpOpen
            {
                ContainerId = "srv",
                IdleTimeoutMilliseconds = 200,
            }, AmqpOpen.Write);

            // Collect frames until we get at least one empty (heartbeat).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(3))
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                if (f.Body.IsEmpty && f.Header.Type == AmqpFrameType.Amqp)
                    return; // heartbeat received
                if (PerformativeCodec.PeekKind(f.Body.Span, out _) == PerformativeKind.Close)
                    Assert.Fail("Got close before heartbeat.");
            }
            Assert.Fail("Did not receive heartbeat within 3s.");
        });

        await conn.OpenAsync();
        await serverTask;
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Peer_initiated_close_triggers_local_reply()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, new AmqpOpen { ContainerId = "srv" }, AmqpOpen.Write);

            // Server initiates close.
            await SendPerfAsync(server, new AmqpClose(), AmqpClose.Write);

            // Expect a reply close back from client.
            using var f = await AmqpFrameIO.ReadFrameAsync(server);
            Assert.Equal(PerformativeKind.Close, PerformativeCodec.PeekKind(f.Body.Span, out _));
        });

        await conn.OpenAsync();
        await serverTask;
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Double_open_throws_invalid_operation()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultSettings());

        var serverTask = Task.Run(async () =>
        {
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, new AmqpOpen { ContainerId = "srv" }, AmqpOpen.Write);
        });

        await conn.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => conn.OpenAsync());
        await serverTask;
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Peer_silence_past_twice_local_idle_timeout_aborts_connection()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        // Local idle-time-out 200 ms ⇒ silence deadline ~400 ms.
        var conn = new AmqpConnection(clientTransport, DefaultSettings(TimeSpan.FromMilliseconds(200)));

        var serverTask = Task.Run(async () =>
        {
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            // Server advertises no idle-time-out so client won't send heartbeats either,
            // then stays silent.
            await SendPerfAsync(server, new AmqpOpen { ContainerId = "srv" }, AmqpOpen.Write);
        });

        await conn.OpenAsync();
        await serverTask;

        // Bounded poll on internal state until the silence detector marks
        // the connection terminal (~400 ms deadline + tick + scheduling).
        // We avoid going through CloseAsync to poll because its hardcoded
        // 30-second peer-close wait would dominate any pre-trigger attempt.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !conn.IsTerminallyFaulted)
            await Task.Delay(25);

        Assert.True(conn.IsTerminallyFaulted, "Silence detector did not mark the connection terminal within 5s.");

        var ex = await Assert.ThrowsAsync<AmqpConnectionException>(() => conn.CloseAsync());
        Assert.Equal(AmqpErrorKind.Transient, ex.Kind);
        Assert.Contains("idle", ex.Message, StringComparison.OrdinalIgnoreCase);
        await conn.DisposeAsync();
    }

    // -- helpers ----------------------------------------------------------

    private delegate void PerfWriter<T>(Span<byte> destination, in T value, out int written);

    private static async Task SendPerfAsync<T>(IAmqpTransport transport, T value, PerfWriter<T> writer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            writer(rented, in value, out var n);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Amqp, 0, rented.AsMemory(0, n));
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }
}
