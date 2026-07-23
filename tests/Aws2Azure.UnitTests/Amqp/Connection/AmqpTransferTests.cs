using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Connection;

/// <summary>
/// Slice 5c tests: message section encode/decode, sender transfer
/// emission, receiver flow + transfer + auto-disposition.
/// </summary>
public sealed class AmqpTransferTests
{
    private static AmqpConnectionSettings DefaultConnSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public void AmqpMessage_round_trip_preserves_properties_and_appprops_and_body()
    {
        var msg = new AmqpMessage
        {
            Properties = new AmqpProperties
            {
                MessageId = "id-1",
                ReplyTo = "$cbs",
                CorrelationId = "corr-1",
            },
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["operation"] = "put-token",
                ["status-code"] = 202,
                ["server-timeout"] = 60_000u,
            },
            Body = new byte[] { 1, 2, 3, 4, 5 },
        };

        Span<byte> buffer = stackalloc byte[1024];
        msg.Write(buffer, out var written);
        var parsed = AmqpMessage.Parse(buffer.Slice(0, written).ToArray());

        Assert.Equal("id-1", parsed.Properties.MessageId);
        Assert.Equal("$cbs", parsed.Properties.ReplyTo);
        Assert.Equal("corr-1", parsed.Properties.CorrelationId);
        Assert.NotNull(parsed.ApplicationProperties);
        Assert.Equal("put-token", parsed.ApplicationProperties!["operation"]);
        Assert.Equal(202, parsed.ApplicationProperties["status-code"]);
        Assert.Equal(60_000u, parsed.ApplicationProperties["server-timeout"]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, parsed.Body.ToArray());
    }

    [Fact]
    public async Task Sender_emits_transfer_with_message_payload_when_settled()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var transferRead = new TaskCompletionSource<(AmqpTransfer perf, byte[] payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 7);

            // Consume client attach + reply.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 7, new AmqpAttach
            {
                Name = "snd", Handle = 99, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // Grant credit so the client may transfer.
            await SendPerfAsync(server, channel: 7, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = 99, DeliveryCount = 0, LinkCredit = 100,
            }, AmqpFlow.Write);

            // Read the transfer frame.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Transfer, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpTransfer.Read(f.Body, out var t, out var perfLen);
                transferRead.TrySetResult((t, f.Body.Slice(perfLen).ToArray()));
            }

            // Detach + end + close shutdown.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 7, new AmqpDetach { Handle = 99, Closed = true }, AmqpDetach.Write);
            await ConsumeEndAndReply(server, peerChannel: 7);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "snd",
            Role = AmqpRole.Sender,
            TargetAddress = "$cbs",
            SenderSettleMode = AmqpSenderSettleMode.Settled,
        });

        var outcome = await link.SendMessageAsync(new AmqpMessage
        {
            Properties = new AmqpProperties { MessageId = "m1" },
            ApplicationProperties = new Dictionary<string, object?> { ["op"] = "ping" },
            Body = new byte[] { 0xAA, 0xBB },
        }, settled: true);

        Assert.Equal(AmqpDispositionOutcome.Accepted, outcome.Outcome);

        var (observedTransfer, observedPayload) = await transferRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0u, observedTransfer.Handle);
        Assert.Equal(0u, observedTransfer.DeliveryId);
        Assert.True(observedTransfer.Settled);

        // Payload must parse back to the same message.
        var parsed = AmqpMessage.Parse(observedPayload);
        Assert.Equal("m1", parsed.Properties.MessageId);
        Assert.Equal("ping", parsed.ApplicationProperties!["op"]);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, parsed.Body.ToArray());

        await link.DetachAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Unsettled_send_completes_on_accepted_disposition()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 11);

            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 11, new AmqpAttach
            {
                Name = "us", Handle = 5, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            await SendPerfAsync(server, channel: 11, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = 5, DeliveryCount = 0, LinkCredit = 100,
            }, AmqpFlow.Write);

            uint deliveryId = 0;
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                AmqpTransfer.Read(f.Body, out var t, out _);
                deliveryId = t.DeliveryId ?? 0;
                Assert.False(t.Settled);
            }

            // Send accepted disposition for that delivery.
            var acc = ArrayPool<byte>.Shared.Rent(8);
            Accepted.Write(acc, out var al);
            await SendPerfAsync(server, channel: 11, new AmqpDisposition
            {
                Role = AmqpRole.Receiver,
                First = deliveryId,
                Settled = true,
                State = acc.AsMemory(0, al),
            }, AmqpDisposition.Write);
            ArrayPool<byte>.Shared.Return(acc);

            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 11, new AmqpDetach { Handle = 5, Closed = true }, AmqpDetach.Write);
            await ConsumeEndAndReply(server, peerChannel: 11);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "us",
            Role = AmqpRole.Sender,
            TargetAddress = "$cbs",
        });

        var outcome = await link.SendMessageAsync(new AmqpMessage
        {
            Body = new byte[] { 1 },
        }, settled: false);
        Assert.Equal(AmqpDispositionOutcome.Accepted, outcome.Outcome);

        await link.DetachAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Receiver_grants_credit_and_receives_message()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        uint observedCredit = 0;
        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 9);

            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 9, new AmqpAttach
            {
                Name = "rcv", Handle = 21, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);

            // Read the flow with link credit.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Flow, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpFlow.Read(f.Body, out var flow, out _);
                observedCredit = flow.LinkCredit ?? 0;
            }

            // Send a transfer to the client.
            var payloadRented = ArrayPool<byte>.Shared.Rent(256);
            var msg = new AmqpMessage
            {
                Properties = new AmqpProperties { MessageId = "x" },
                Body = new byte[] { 7, 7, 7 },
            };
            msg.Write(payloadRented, out var payloadLen);

            var transferRented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
            var transfer = CreateTransfer(handle: 21, deliveryId: 0);
            AmqpTransfer.Write(transferRented, in transfer, out var tlen);

            var frame = ArrayPool<byte>.Shared.Rent(tlen + payloadLen);
            transferRented.AsSpan(0, tlen).CopyTo(frame);
            payloadRented.AsSpan(0, payloadLen).CopyTo(frame.AsSpan(tlen));
            await AmqpFrameIO.WriteFrameAsync(server, AmqpFrameType.Amqp, 9, frame.AsMemory(0, tlen + payloadLen));
            ArrayPool<byte>.Shared.Return(frame);
            ArrayPool<byte>.Shared.Return(transferRented);
            ArrayPool<byte>.Shared.Return(payloadRented);

            // Consume the auto-accept disposition.
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Disposition, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpDisposition.Read(f.Body, out var d, out _);
                Assert.Equal(AmqpRole.Receiver, d.Role);
                Assert.Equal(0u, d.First);
                Assert.True(d.Settled);
            }

            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 9, new AmqpDetach { Handle = 21, Closed = true }, AmqpDetach.Write);
            await ConsumeEndAndReply(server, peerChannel: 9);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv",
            Role = AmqpRole.Receiver,
            SourceAddress = "$cbs",
        });

        await link.GrantCreditAsync(10);
        var delivery = await link.ReceiveMessageAsync();
        Assert.Equal(0u, delivery.DeliveryId);
        var parsed = delivery.ToMessage();
        Assert.Equal("x", parsed.Properties.MessageId);
        Assert.Equal(new byte[] { 7, 7, 7 }, parsed.Body.ToArray());

        await link.AcceptAsync(delivery);
        Assert.Equal(10u, observedCredit);

        await link.DetachAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    private static AmqpTransfer CreateTransfer(uint handle, uint deliveryId) => new()
    {
        Handle = handle,
        DeliveryId = deliveryId,
        DeliveryTag = new byte[] { (byte)deliveryId },
        MessageFormat = 0,
        Settled = false,
    };

    // ---- shared helpers (mirrored from AmqpLinkTests) -------------------

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
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
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

    private static async Task ConsumeEndAndReply(IAmqpTransport server, ushort peerChannel)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
        await SendPerfAsync(server, peerChannel, new AmqpEnd(), AmqpEnd.Write);
    }

    private static async Task ConsumeCloseAsync(IAmqpTransport server)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
        await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
    }
}
