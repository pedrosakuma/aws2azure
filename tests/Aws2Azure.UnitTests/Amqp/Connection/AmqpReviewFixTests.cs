using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Connection;

/// <summary>
/// Regression tests for the five Major findings of the Phase 2.5
/// phase-exit code review.
/// </summary>
public sealed class AmqpReviewFixTests
{
    private static AmqpConnectionSettings DefaultConnSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "test",
        IdleTimeout = TimeSpan.Zero,
    };

    // ---------- Fix #1: peer-initiated end/detach must reach Final ----------

    [Fact]
    public async Task PeerInitiated_end_transitions_session_to_final_so_close_returns()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            // Peer-initiates end before the client does.
            await SendPerfAsync(server, channel: 4, new AmqpEnd(), AmqpEnd.Write);
            // Drain mirrored end.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        // Give the peer's end time to land.
        await Task.Delay(100);

        // A subsequent CloseAsync must complete (not deadlock waiting on Final).
        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task PeerInitiated_detach_transitions_link_to_final_so_detach_returns()
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
                Name = "link", Handle = 7, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // Peer initiates detach.
            await SendPerfAsync(server, channel: 4, new AmqpDetach { Handle = 7, Closed = true }, AmqpDetach.Write);
            // Drain mirrored detach + end + close.
            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "link", Role = AmqpRole.Sender, TargetAddress = "q",
        });

        // Poll for the link to observe the peer detach and reach a closed state.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!link.IsClosed && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(link.IsClosed, "link did not transition to closed after peer detach");

        // A no-op DetachAsync must short-circuit (StateFinal/StateClosed) and return promptly.
        await link.DetachAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await conn.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---------- Fix #2: sender must wait for flow before transferring ----------

    [Fact]
    public async Task Send_blocks_until_peer_grants_credit_via_flow()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var grantCredit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "snd", Handle = 7, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // Wait for the test to signal credit grant.
            await grantCredit.Task;
            await SendPerfAsync(server, channel: 4, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = 7, DeliveryCount = 0, LinkCredit = 1,
            }, AmqpFlow.Write);
            // Drain transfer + tear-down.
            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "snd", Role = AmqpRole.Sender, TargetAddress = "q",
            SenderSettleMode = AmqpSenderSettleMode.Settled,
        });

        var send = Task.Run(() => link.SendMessageAsync(new AmqpMessage
        {
            Properties = new AmqpProperties { MessageId = "m1" },
            Body = new byte[] { 1 },
        }, settled: true));

        // Without credit the send must NOT complete.
        await Task.Delay(150);
        Assert.False(send.IsCompleted, "send completed before peer granted credit");

        // Grant credit — send should now complete.
        grantCredit.SetResult();
        var outcome = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AmqpDispositionOutcome.Accepted, outcome);

        await conn.CloseAsync();
    }

    // ---------- Fix #3: outgoing writes honour negotiated max-frame-size ----

    [Fact]
    public async Task Outgoing_writes_honor_negotiated_max_frame_size_after_open()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var settings = new AmqpConnectionSettings
        {
            ContainerId = "c", Hostname = "h", IdleTimeout = TimeSpan.Zero,
            MaxFrameSize = 64 * 1024,
        };
        var conn = new AmqpConnection(clientTransport, settings);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server, maxFrameSize: 64 * 1024);
            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        // Fix #3: after open, the connection must honor the peer-advertised
        // max-frame-size (RemoteOpen.MaxFrameSize), not the spec-mandated
        // 4 KiB initial limit.
        Assert.Equal(64u * 1024u, conn.RemoteOpen.MaxFrameSize);
        Assert.Equal(64u * 1024u, conn.EffectiveOutgoingMaxFrameSize);

        await conn.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---------- Fix #4: multi-frame transfer reassembly ----------

    [Fact]
    public async Task Multi_frame_transfer_is_reassembled_into_single_delivery()
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
            // Read initial flow.
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }

            // Send delivery in 3 frames.
            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: 0u, deliveryTag: new byte[] { 0x01 },
                payload: new byte[] { 0x01, 0x02 }, more: true);
            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: null, deliveryTag: ReadOnlyMemory<byte>.Empty,
                payload: new byte[] { 0x03, 0x04 }, more: true);
            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: null, deliveryTag: ReadOnlyMemory<byte>.Empty,
                payload: new byte[] { 0x05, 0x06 }, more: false);

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
        });
        await link.GrantCreditAsync(10);

        var delivery = await link.ReceiveMessageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(default, delivery);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, delivery.Payload);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task Aborted_transfer_is_discarded_and_does_not_surface()
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

            // First delivery aborted mid-stream.
            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: 0u, deliveryTag: new byte[] { 0x01 },
                payload: new byte[] { 0x99 }, more: true);
            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: null, deliveryTag: ReadOnlyMemory<byte>.Empty,
                payload: ReadOnlyMemory<byte>.Empty, more: false, aborted: true);

            // Second delivery: a clean one we can observe.
            await SendTransferPayloadAsync(server, channel: 4, handle: 8,
                deliveryId: 1u, deliveryTag: new byte[] { 0x02 },
                payload: new byte[] { 0xAA, 0xBB }, more: false);

            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
        });
        await link.GrantCreditAsync(10);

        var delivery = await link.ReceiveMessageAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(default, delivery);
        // Aborted delivery dropped; we should receive the clean one.
        Assert.Equal(new byte[] { 0xAA, 0xBB }, delivery.Payload);

        await conn.CloseAsync();
    }

    // ---------- Fix #5: channel allocation honours RemoteOpen.ChannelMax ----

    [Fact]
    public async Task Channel_max_negotiation_uses_minimum_of_local_and_remote()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, new AmqpConnectionSettings
        {
            ContainerId = "c", Hostname = "h", IdleTimeout = TimeSpan.Zero,
            ChannelMax = 0xFFFF,
        });

        var serverTask = Task.Run(async () =>
        {
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 0, new AmqpOpen
            {
                ContainerId = "s", MaxFrameSize = 8192, ChannelMax = 0, // peer allows only channel 0
            }, AmqpOpen.Write);

            // Accept the first begin on channel 0 then drain to close.
            await ConsumeBeginAndReply(server, peerChannel: 0);
            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        Assert.Equal((ushort)0, conn.RemoteOpen.ChannelMax);

        var s1 = await conn.BeginSessionAsync();
        Assert.Equal((ushort)0, s1.OutgoingChannel);

        // Second BeginSession must fail because peer's channel-max=0 means only one channel.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await conn.BeginSessionAsync());

        await conn.CloseAsync();
    }

    // ====================================================================
    // PR #50 review fixes (Major findings, gpt-5.5).
    // ====================================================================

    // ---------- Finding 1: huge credit grants must release permits ----------

    [Fact]
    public async Task Flow_with_credit_beyond_int_max_value_still_unblocks_sender()
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
                Name = "snd", Handle = 7, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // §2.7.4: link-credit is uint — broker is allowed to grant uint.MaxValue.
            await SendPerfAsync(server, channel: 4, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = 7, DeliveryCount = 0, LinkCredit = uint.MaxValue,
            }, AmqpFlow.Write);
            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "snd", Role = AmqpRole.Sender, TargetAddress = "q",
            SenderSettleMode = AmqpSenderSettleMode.Settled,
        });

        var outcome = await link.SendMessageAsync(new AmqpMessage
        {
            Properties = new AmqpProperties { MessageId = "m1" },
            Body = new byte[] { 1 },
        }, settled: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AmqpDispositionOutcome.Accepted, outcome);

        await conn.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---------- Finding 2: outbound transfers must fragment across frames ----

    [Fact]
    public async Task Outbound_message_larger_than_max_frame_size_is_fragmented()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, new AmqpConnectionSettings
        {
            ContainerId = "c", Hostname = "h", IdleTimeout = TimeSpan.Zero,
            MaxFrameSize = 4096,
        });

        var fragmentCount = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDeliveryId = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server, maxFrameSize: 4096);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server, 4096)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "snd", Handle = 7, Role = AmqpRole.Receiver, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            await SendPerfAsync(server, channel: 4, new AmqpFlow
            {
                NextIncomingId = 0, IncomingWindow = uint.MaxValue,
                NextOutgoingId = 0, OutgoingWindow = uint.MaxValue,
                Handle = 7, DeliveryCount = 0, LinkCredit = 10,
            }, AmqpFlow.Write);

            int seen = 0;
            int reassembledLen = 0;
            uint? firstId = null;
            while (true)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server, 4096);
                var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                if (kind == PerformativeKind.Transfer)
                {
                    AmqpTransfer.Read(f.Body, out var t, out var perfLen);
                    reassembledLen += f.Body.Length - perfLen;
                    if (seen == 0) firstId = t.DeliveryId;
                    seen++;
                    if (!(t.More ?? false))
                    {
                        fragmentCount.TrySetResult(seen);
                        firstDeliveryId.TrySetResult(firstId);
                    }
                }
                else if (kind == PerformativeKind.Close)
                {
                    await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
                    return;
                }
                else if (kind == PerformativeKind.End)
                {
                    await SendPerfAsync(server, channel: 4, new AmqpEnd(), AmqpEnd.Write);
                }
                else if (kind == PerformativeKind.Detach)
                {
                    AmqpDetach.Read(f.Body, out var d, out _);
                    await SendPerfAsync(server, channel: 4, new AmqpDetach { Handle = d.Handle, Closed = true }, AmqpDetach.Write);
                }
            }
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "snd", Role = AmqpRole.Sender, TargetAddress = "q",
            SenderSettleMode = AmqpSenderSettleMode.Settled,
            MaxMessageSize = 0,
        });

        // 12 KiB payload — must split across multiple 4 KiB frames.
        var body = new byte[12 * 1024];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(i & 0xFF);

        var outcome = await link.SendMessageAsync(new AmqpMessage
        {
            Properties = new AmqpProperties { MessageId = "big" },
            Body = body,
        }, settled: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AmqpDispositionOutcome.Accepted, outcome);

        var n = await fragmentCount.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(n >= 3, $"expected ≥3 transfer frames for 12 KiB body over 4 KiB max-frame-size, got {n}");
        var first = await firstDeliveryId.Task;
        Assert.NotNull(first);

        await conn.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---------- Finding 3: inbound reassembly must cap at max-message-size ---

    [Fact]
    public async Task Inbound_payload_exceeding_max_message_size_detaches_link()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var sawDetach = new TaskCompletionSource<AmqpDetach>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 4, new AmqpAttach
            {
                Name = "rcv", Handle = 11, Role = AmqpRole.Sender, InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            // Two more=true continuations whose combined payload exceeds 256 B.
            var part = new byte[200];
            await SendTransferPayloadAsync(server, 4, handle: 11,
                deliveryId: 0, deliveryTag: new byte[] { 1 }, payload: part, more: true);
            await SendTransferPayloadAsync(server, 4, handle: 11,
                deliveryId: null, deliveryTag: ReadOnlyMemory<byte>.Empty, payload: part, more: true);

            // Expect a Detach back with link:message-size-exceeded.
            while (true)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                var kind = PerformativeCodec.PeekKind(f.Body.Span, out _);
                if (kind == PerformativeKind.Detach)
                {
                    AmqpDetach.Read(f.Body, out var d, out _);
                    sawDetach.TrySetResult(d);
                    await SendPerfAsync(server, channel: 4, new AmqpDetach { Handle = d.Handle, Closed = true }, AmqpDetach.Write);
                    break;
                }
            }
            await DrainUntilCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "rcv", Role = AmqpRole.Receiver, SourceAddress = "q",
            InitialDeliveryCount = null,
            MaxMessageSize = 256,
        });
        await link.GrantCreditAsync(5);

        var d = await sawDetach.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(d.Closed ?? false);
        // The error payload should carry amqp:link:message-size-exceeded.
        Assert.False(d.Error.IsEmpty);
        AmqpError.Read(d.Error, out var err, out _);
        Assert.Equal(AmqpErrorCondition.LinkMessageSizeExceeded, err.Condition);

        await conn.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---- helpers ----------------------------------------------------------

    private delegate void PerfWriter<T>(Span<byte> destination, in T value, out int written);

    private static async Task SendPerfAsync<T>(IAmqpTransport transport, ushort channel, T value, PerfWriter<T> writer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
        try
        {
            writer(rented, in value, out var n);
            await AmqpFrameIO.WriteFrameAsync(transport, AmqpFrameType.Amqp, channel, rented.AsMemory(0, n), default, 64 * 1024);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async Task SendTransferPayloadAsync(
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

    private static async Task ConsumeOpenAsync(IAmqpTransport server, uint maxFrameSize = 8192)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server, (int)Math.Max(maxFrameSize, AmqpFrameIO.InitialMaxFrameSize))) { }
        await SendPerfAsync(server, channel: 0, new AmqpOpen
        {
            ContainerId = "s", MaxFrameSize = maxFrameSize, ChannelMax = 0xFFFF,
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

    private static async Task ConsumeCloseAsync(IAmqpTransport server)
    {
        using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
        await SendPerfAsync(server, channel: 0, new AmqpClose(), AmqpClose.Write);
    }

    private static async Task DrainUntilCloseAsync(IAmqpTransport server, ushort peerSessionChannel = 4)
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
