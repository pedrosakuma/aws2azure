using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;
using static Aws2Azure.UnitTests.Amqp.Connection.AmqpTestBroker;

namespace Aws2Azure.UnitTests.Amqp.Connection;

/// <summary>
/// Phase 2.5 Slice 6: receiver link long-poll + disposition outcomes
/// (accept / reject / release / modify). Drives a real client AmqpLink
/// against an in-process broker over a pipe pair.
/// </summary>
public sealed class AmqpReceiverTests
{
    private static AmqpConnectionSettings DefaultConnSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "test",
        IdleTimeout = TimeSpan.Zero,
    };

    // ---------- ReceiveBatchAsync ------------------------------------------

    [Fact]
    public async Task ReceiveBatchAsync_returns_early_when_max_messages_reached()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            // Read client attach (receiver), reply with peer attach (broker is sender).
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "rcv", Handle = 8, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // Drain the credit-granting flow then send three deliveries.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

            for (uint i = 0; i < 3; i++)
            {
                await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                    deliveryId: i, deliveryTag: new byte[] { (byte)i },
                    payload: new byte[] { (byte)(0xA0 + i) }, more: false);
            }

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
        });

        var batch = await link.ReceiveBatchAsync(maxMessages: 3, maxWait: TimeSpan.FromSeconds(5))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, batch.Count);
        Assert.Equal(new byte[] { 0xA0 }, batch[0].Payload);
        Assert.Equal(new byte[] { 0xA1 }, batch[1].Payload);
        Assert.Equal(new byte[] { 0xA2 }, batch[2].Payload);

        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReceiveBatchAsync_honours_deadline_and_returns_partial()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "rcv", Handle = 8, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

            // Only one delivery — caller asked for ten.
            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: 0u, deliveryTag: new byte[] { 0x01 },
                payload: new byte[] { 0xCC }, more: false);

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var batch = await link.ReceiveBatchAsync(maxMessages: 10, maxWait: TimeSpan.FromMilliseconds(400))
            .WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.Single(batch);
        Assert.Equal(new byte[] { 0xCC }, batch[0].Payload);
        // Must have waited at least ~most of the deadline before returning.
        Assert.True(sw.ElapsedMilliseconds >= 200,
            $"ReceiveBatchAsync returned too quickly ({sw.ElapsedMilliseconds} ms); should have honoured deadline");

        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReceiveBatchAsync_returns_empty_when_no_messages_within_deadline()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "rcv", Handle = 8, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // Read the flow but never deliver anything.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
        });

        var batch = await link.ReceiveBatchAsync(maxMessages: 5, maxWait: TimeSpan.FromMilliseconds(200))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(batch);

        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---------- Disposition outcomes ---------------------------------------

    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    [InlineData("release")]
    [InlineData("modify")]
    public async Task Disposition_outcomes_emit_correct_state(string outcome)
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var dispositionTcs = new TaskCompletionSource<(uint first, DeliveryStateKind kind, bool? deliveryFailed, bool? undeliverableHere)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "rcv", Handle = 8, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: 0u, deliveryTag: new byte[] { 0x01 },
                payload: new byte[] { 0x77 }, more: false);

            // Read the disposition the client emits and inspect its state.
            try
            {
                while (true)
                {
                    using var f = await AmqpFrameIO.ReadFrameAsync(server, 64 * 1024);
                    var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                    if (kind == PerformativeKind.Disposition)
                    {
                        AmqpDisposition.Read(f.Body, out var d, out _);
                        var stateKind = d.State.IsEmpty
                            ? DeliveryStateKind.Unknown
                            : DeliveryState.PeekKind(d.State.Span, out _);

                        bool? df = null, uh = null;
                        if (stateKind == DeliveryStateKind.Modified)
                        {
                            Modified.Read(d.State, out var m, out _);
                            df = m.DeliveryFailed;
                            uh = m.UndeliverableHere;
                        }
                        dispositionTcs.TrySetResult((d.First, stateKind, df, uh));
                        break;
                    }
                }
            }
            catch (Exception ex) { dispositionTcs.TrySetException(ex); }

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
        });
        await link.GrantCreditAsync(1);

        var delivery = await link.ReceiveMessageAsync().WaitAsync(TimeSpan.FromSeconds(5));

        switch (outcome)
        {
            case "accept": await link.AcceptAsync(delivery); break;
            case "reject":
                await link.RejectAsync(delivery, new AmqpError
                {
                    Condition = AmqpErrorCondition.InternalError,
                    Description = "test",
                });
                break;
            case "release": await link.ReleaseAsync(delivery); break;
            case "modify":
                await link.ModifyAsync(delivery, deliveryFailed: true, undeliverableHere: true);
                break;
        }

        var observed = await dispositionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0u, observed.first);
        var expectedKind = outcome switch
        {
            "accept" => DeliveryStateKind.Accepted,
            "reject" => DeliveryStateKind.Rejected,
            "release" => DeliveryStateKind.Released,
            "modify" => DeliveryStateKind.Modified,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(expectedKind, observed.kind);
        if (outcome == "modify")
        {
            Assert.True(observed.deliveryFailed);
            Assert.True(observed.undeliverableHere);
        }

        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---------- PR-review follow-ups ---------------------------------------

    [Fact]
    public async Task AttachAsync_throws_when_peer_detaches_immediately_after_attach()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            // Peer attaches and immediately detaches — the link must NOT
            // be reported as successfully attached.
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "rcv", Handle = 8, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            await SendPerfAsync(server, channel: 4, new AmqpDetach
            {
                Handle = 8, Closed = true,
                Error = EncodeError(AmqpErrorCondition.UnauthorizedAccess, "no auth"),
            }, AmqpDetach.Write);

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();

        // Either AttachLinkAsync surfaces the detach as an exception, or
        // the returned link transitions to terminal soon after. Both
        // outcomes are acceptable — the bug we're guarding against is
        // returning a "successfully attached" link that the peer already
        // tore down and that stays usable.
        AmqpLink? link = null;
        Exception? attachEx = null;
        try
        {
            link = await session.AttachLinkAsync(new AmqpLinkSettings
            {
                Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { attachEx = ex; }

        if (attachEx is null)
        {
            Assert.NotNull(link);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!link!.IsClosed && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(link.IsClosed,
                "AttachAsync returned a link that never transitioned to closed despite peer detach.");
        }

        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReceiveBatchAsync_throws_when_link_closed_before_deadline()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "rcv", Handle = 8, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // Drain the credit-granting flow then detach the link.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpDetach
            {
                Handle = 8, Closed = true,
            }, AmqpDetach.Write);

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
        });

        // Long-poll with a generous deadline. The peer detach should
        // complete the incoming channel before the deadline elapses, and
        // ReceiveBatchAsync must surface the broken link rather than
        // returning an empty list as if the deadline fired.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await link.ReceiveBatchAsync(maxMessages: 5, maxWait: TimeSpan.FromSeconds(10))
                .WaitAsync(TimeSpan.FromSeconds(15)));

        await conn.CloseAsync();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ReadOnlyMemory<byte> EncodeError(string condition, string description)
    {
        var rented = new byte[256];
        var err = new AmqpError { Condition = condition, Description = description };
        AmqpError.Write(rented, in err, out var len);
        return new ReadOnlyMemory<byte>(rented, 0, len);
    }
}
