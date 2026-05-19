using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Management;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Management;

/// <summary>
/// Slice 5d tests for <see cref="AmqpRequestResponseLink"/>: paired
/// sender/receiver open, correlation by message-id ↔ correlation-id,
/// concurrent request routing, cancellation, and dispose cleanup.
/// </summary>
public sealed class AmqpRequestResponseLinkTests
{
    private static AmqpConnectionSettings DefaultConnSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task Request_response_round_trip_matches_correlation_id()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () => await RunEchoBrokerAsync(server, expectedRequests: 1));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        await using var rr = new AmqpRequestResponseLink(session, new AmqpRequestResponseLinkSettings
        {
            Address = "$cbs",
            ReplyToAddress = "cbs-reply-to",
            InitialReceiverCredit = 10,
        });
        await rr.OpenAsync();

        var response = await rr.SendRequestAsync(new AmqpMessage
        {
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["operation"] = "put-token",
                ["status-code"] = 0,
            },
            Body = new byte[] { 0xAB },
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("ok", response.ApplicationProperties!["status"]);
        Assert.NotNull(response.Properties.CorrelationId);
        Assert.StartsWith("req-", response.Properties.CorrelationId);

        await rr.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Concurrent_requests_route_back_to_their_callers()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () => await RunEchoBrokerAsync(server, expectedRequests: 5));

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        await using var rr = new AmqpRequestResponseLink(session, new AmqpRequestResponseLinkSettings
        {
            Address = "$cbs",
            ReplyToAddress = "cbs-reply-to",
            InitialReceiverCredit = 10,
        });
        await rr.OpenAsync();

        var tasks = Enumerable.Range(0, 5).Select(i => rr.SendRequestAsync(new AmqpMessage
        {
            ApplicationProperties = new Dictionary<string, object?> { ["seq"] = i },
            Body = new byte[] { (byte)i },
        })).ToArray();

        var responses = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        // Each response's body echoes the request body, so the i-th task
        // must have received the i-th request — but the broker may
        // service them in any order. Check the bag of body bytes.
        var bodies = responses.Select(r => r.Body.Span[0]).OrderBy(b => b).ToArray();
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, bodies);

        await rr.DisposeAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Dispose_cancels_pending_requests()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        // Broker accepts handshakes/attach but never responds to requests.
        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            // Sender attach.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                AmqpAttach.Read(f.Body, out var a, out _);
                await SendPerfAsync(server, channel: 4, new AmqpAttach
                {
                    Name = a.Name, Handle = 10, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
                }, AmqpAttach.Write);
            }
            // Receiver attach.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                AmqpAttach.Read(f.Body, out var a, out _);
                await SendPerfAsync(server, channel: 4, new AmqpAttach
                {
                    Name = a.Name, Handle = 11, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
                }, AmqpAttach.Write);
            }
            // Read flow.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            // Read transfer — do not respond.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

            // Drain the rest until peer end+close.
            try
            {
                while (true)
                {
                    using var f = await AmqpFrameIO.ReadFrameAsync(server);
                    var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                    if (kind == PerformativeKind.Detach)
                    {
                        AmqpDetach.Read(f.Body, out var d, out _);
                        var ourHandle = d.Handle == 0u ? 10u : 11u;
                        await SendPerfAsync(server, channel: 4, new AmqpDetach { Handle = ourHandle, Closed = true }, AmqpDetach.Write);
                    }
                    else if (kind == PerformativeKind.End)
                    {
                        await SendPerfAsync(server, channel: 4, new AmqpEnd(), AmqpEnd.Write);
                    }
                    else if (kind == PerformativeKind.Close)
                    {
                        await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
                        break;
                    }
                }
            }
            catch (IOException) { /* peer closed */ }
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var rr = new AmqpRequestResponseLink(session, new AmqpRequestResponseLinkSettings
        {
            Address = "$cbs",
            ReplyToAddress = "cbs-reply-to",
            InitialReceiverCredit = 5,
        });
        await rr.OpenAsync();

        var pending = rr.SendRequestAsync(new AmqpMessage { Body = new byte[] { 1 } });
        // Give the send time to land before we dispose.
        await Task.Delay(50);

        await rr.DisposeAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
        Assert.True(ex is ObjectDisposedException or InvalidOperationException);

        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    // ---- broker simulator ------------------------------------------------

    private static async Task RunEchoBrokerAsync(IAmqpTransport server, int expectedRequests)
    {
        await ConsumeOpenAsync(server);
        await ConsumeBeginAndReply(server, peerChannel: 3);

        // Sender attach (request channel) — peer handle 100.
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            AmqpAttach.Read(f.Body, out var a, out _);
            await SendPerfAsync(server, channel: 3, new AmqpAttach
            {
                Name = a.Name, Handle = 100, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
        }
        // Receiver attach (response channel) — peer handle 101.
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            AmqpAttach.Read(f.Body, out var a, out _);
            await SendPerfAsync(server, channel: 3, new AmqpAttach
            {
                Name = a.Name, Handle = 101, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
        }

        // Receive initial flow.
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

        uint nextDelivery = 0;
        int handled = 0;
        while (handled < expectedRequests)
        {
            using var f = await AmqpFrameIO.ReadFrameAsync(server);
            var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
            if (kind == PerformativeKind.Flow) continue;        // credit top-up
            if (kind == PerformativeKind.Disposition) continue; // receiver auto-accept
            if (kind != PerformativeKind.Transfer)
                throw new InvalidOperationException($"Unexpected frame kind {kind} from client.");

            AmqpTransfer.Read(f.Body, out var transfer, out var perfLen);
            var payload = f.Body.Slice(perfLen).ToArray();
            var requestMsg = AmqpMessage.Parse(payload);

            var response = new AmqpMessage
            {
                Properties = new AmqpProperties { CorrelationId = requestMsg.Properties.MessageId },
                ApplicationProperties = new Dictionary<string, object?> { ["status"] = "ok" },
                Body = requestMsg.Body.ToArray(),
            };
            var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            response.Write(rented, out var respLen);

            // Build transfer (handle = our sender peer-handle = 101) + payload.
            var respTransfer = new AmqpTransfer
            {
                Handle = 101u,
                DeliveryId = nextDelivery++,
                DeliveryTag = new byte[] { (byte)handled },
                MessageFormat = 0,
                Settled = false,
            };
            var perfRented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            AmqpTransfer.Write(perfRented, in respTransfer, out var tlen);

            var frame = ArrayPool<byte>.Shared.Rent(tlen + respLen);
            perfRented.AsSpan(0, tlen).CopyTo(frame);
            rented.AsSpan(0, respLen).CopyTo(frame.AsSpan(tlen));
            await AmqpFrameIO.WriteFrameAsync(server, AmqpFrameType.Amqp, 3, frame.AsMemory(0, tlen + respLen));

            ArrayPool<byte>.Shared.Return(frame);
            ArrayPool<byte>.Shared.Return(perfRented);
            ArrayPool<byte>.Shared.Return(rented);
            handled++;
        }

        // Drain remaining client frames (flows, disposition, detach×2, end, close).
        try
        {
            while (true)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                if (kind == PerformativeKind.Detach)
                {
                    AmqpDetach.Read(f.Body, out var d, out _);
                    // Reply with OUR handle for the same link: sender link
                    // (client handle 0) ↔ our handle 100; receiver link
                    // (client handle 1) ↔ our handle 101.
                    var ourHandle = d.Handle == 0u ? 100u : 101u;
                    await SendPerfAsync(server, channel: 3, new AmqpDetach { Handle = ourHandle, Closed = true }, AmqpDetach.Write);
                }
                else if (kind == PerformativeKind.End)
                {
                    await SendPerfAsync(server, channel: 3, new AmqpEnd(), AmqpEnd.Write);
                }
                else if (kind == PerformativeKind.Close)
                {
                    await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
                    break;
                }
            }
        }
        catch (IOException) { /* peer closed first */ }
    }

    // ---- shared frame helpers -------------------------------------------

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

    private static async Task ConsumeBeginAndReply(IAmqpTransport server, ushort peerChannel)
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
}
