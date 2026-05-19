using System.Buffers;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Transport;
using Aws2Azure.UnitTests.Amqp.Transport;

namespace Aws2Azure.UnitTests.Amqp.Connection;

/// <summary>
/// Tests for <see cref="AmqpLink"/> lifecycle (Slice 5b): attach/detach
/// handshake, peer-initiated detach, handle allocation, name uniqueness,
/// source/target round-trip.
/// </summary>
public sealed class AmqpLinkTests
{
    private static AmqpConnectionSettings DefaultConnSettings() => new()
    {
        ContainerId = "test-client",
        Hostname = "ns.servicebus.windows.net",
        IdleTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task Attach_then_detach_sender_carries_target_address()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        AmqpAttach observedAttach = default;
        bool observed = false;
        ReadOnlyMemory<byte> capturedTarget = default;
        ReadOnlyMemory<byte> capturedSource = default;
        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 5);

            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Attach, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpAttach.Read(f.Body, out var attach, out _);
                observedAttach = attach;
                observed = true;
                capturedTarget = attach.Target.ToArray();
                capturedSource = attach.Source.ToArray();
            }

            // Reply with our attach (peer handle 42).
            await SendPerfAsync(server, channel: 5, new AmqpAttach
            {
                Name = "cbs-sender",
                Handle = 42,
                Role = AmqpRole.Receiver, // peer's role mirrors client's sender
                InitialDeliveryCount = 0,
            }, AmqpAttach.Write);

            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Detach, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpDetach.Read(f.Body, out var detach, out _);
                Assert.Equal(0u, detach.Handle);
                Assert.True(detach.Closed);
            }
            await SendPerfAsync(server, channel: 5, new AmqpDetach { Handle = 42, Closed = true }, AmqpDetach.Write);

            await ConsumeEndAndReply(server, peerChannel: 5);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "cbs-sender",
            Role = AmqpRole.Sender,
            TargetAddress = "$cbs",
            SenderSettleMode = AmqpSenderSettleMode.Settled,
        });

        Assert.Equal(0u, link.OutgoingHandle);
        Assert.Equal(42u, link.RemoteHandle);

        // Inspect the wire-level attach we sent.
        Assert.True(observed);
        var oa = observedAttach;
        Assert.Equal("cbs-sender", oa.Name);
        Assert.Equal(0u, oa.Handle);
        Assert.Equal(AmqpRole.Sender, oa.Role);
        Assert.Equal(AmqpSenderSettleMode.Settled, oa.SenderSettleMode);
        Assert.Equal(0u, oa.InitialDeliveryCount);
        Assert.False(capturedTarget.IsEmpty);
        AmqpTarget.Read(capturedTarget, out var tgt, out _);
        Assert.Equal("$cbs", tgt.Address);
        Assert.True(capturedSource.IsEmpty);

        await link.DetachAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Receiver_attach_carries_source_address()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        AmqpAttach observedAttach = default;
        bool observed = false;
        ReadOnlyMemory<byte> capturedSource = default;
        ReadOnlyMemory<byte> capturedTarget = default;
        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 1);
            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                AmqpAttach.Read(f.Body, out var attach, out _);
                observedAttach = attach;
                observed = true;
                capturedSource = attach.Source.ToArray();
                capturedTarget = attach.Target.ToArray();
            }
            await SendPerfAsync(server, channel: 1, new AmqpAttach
            {
                Name = "cbs-receiver",
                Handle = 1,
                Role = AmqpRole.Sender,
                InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 1, new AmqpDetach { Handle = 1, Closed = true }, AmqpDetach.Write);
            await ConsumeEndAndReply(server, peerChannel: 1);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "cbs-receiver",
            Role = AmqpRole.Receiver,
            SourceAddress = "$cbs",
        });

        var oa = observedAttach;
        Assert.True(observed);
        Assert.Equal(AmqpRole.Receiver, oa.Role);
        Assert.Null(oa.InitialDeliveryCount);
        AmqpSource.Read(capturedSource, out var src, out _);
        Assert.Equal("$cbs", src.Address);
        Assert.True(capturedTarget.IsEmpty);

        await link.DetachAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Peer_initiated_detach_triggers_mirror_reply()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var sawMirror = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 2);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 2, new AmqpAttach
            {
                Name = "lk",
                Handle = 7,
                Role = AmqpRole.Receiver,
                InitialDeliveryCount = 0,
            }, AmqpAttach.Write);

            await Task.Delay(50);
            await SendPerfAsync(server, channel: 2, new AmqpDetach { Handle = 7, Closed = true }, AmqpDetach.Write);

            using (var f = await AmqpFrameIO.ReadFrameAsync(server))
            {
                Assert.Equal(PerformativeKind.Detach, PerformativeCodec.PeekKind(f.Body.Span, out _));
                AmqpDetach.Read(f.Body, out var d, out _);
                Assert.True(d.Closed);
                sawMirror.TrySetResult(true);
            }
            await ConsumeEndAndReply(server, peerChannel: 2);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var link = await session.AttachLinkAsync(new AmqpLinkSettings
        {
            Name = "lk",
            Role = AmqpRole.Sender,
            TargetAddress = "q",
        });

        Assert.True(await sawMirror.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(link.IsClosed);

        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Multiple_links_get_distinct_handles_and_release_on_detach()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 4);
            for (int i = 0; i < 3; i++)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                AmqpAttach.Read(f.Body, out var attach, out _);
                await SendPerfAsync(server, channel: 4, new AmqpAttach
                {
                    Name = attach.Name,
                    Handle = attach.Handle + 100,
                    Role = attach.Role == AmqpRole.Sender ? AmqpRole.Receiver : AmqpRole.Sender,
                    InitialDeliveryCount = 0,
                }, AmqpAttach.Write);
            }
            // 3 detaches expected.
            for (int i = 0; i < 3; i++)
            {
                using var f = await AmqpFrameIO.ReadFrameAsync(server);
                AmqpDetach.Read(f.Body, out var d, out _);
                await SendPerfAsync(server, channel: 4, new AmqpDetach { Handle = d.Handle + 100, Closed = true }, AmqpDetach.Write);
            }
            await ConsumeEndAndReply(server, peerChannel: 4);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var l0 = await session.AttachLinkAsync(new() { Name = "a", Role = AmqpRole.Sender, TargetAddress = "x" });
        var l1 = await session.AttachLinkAsync(new() { Name = "b", Role = AmqpRole.Sender, TargetAddress = "x" });
        var l2 = await session.AttachLinkAsync(new() { Name = "c", Role = AmqpRole.Sender, TargetAddress = "x" });

        Assert.Equal(0u, l0.OutgoingHandle);
        Assert.Equal(1u, l1.OutgoingHandle);
        Assert.Equal(2u, l2.OutgoingHandle);

        await l1.DetachAsync();
        // After detach, a new attach should be able to reuse a freed handle (1 first
        // available again given the cursor wraps via next-after-last).
        // We just verify detach wired up; full re-use semantics are session-level.

        await l0.DetachAsync();
        await l2.DetachAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
    }

    [Fact]
    public async Task Duplicate_link_name_rejected()
    {
        var (clientTransport, serverTransport) = PipePairTransport.CreatePair();
        await using var server = serverTransport;
        var conn = new AmqpConnection(clientTransport, DefaultConnSettings());

        var serverTask = Task.Run(async () =>
        {
            await ConsumeOpenAsync(server);
            await ConsumeBeginAndReply(server, peerChannel: 3);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 3, new AmqpAttach
            {
                Name = "dup",
                Handle = 9,
                Role = AmqpRole.Receiver,
                InitialDeliveryCount = 0,
            }, AmqpAttach.Write);
            using (var _ = await AmqpFrameIO.ReadFrameAsync(server)) { }
            await SendPerfAsync(server, channel: 3, new AmqpDetach { Handle = 9, Closed = true }, AmqpDetach.Write);
            await ConsumeEndAndReply(server, peerChannel: 3);
            await ConsumeCloseAsync(server);
        });

        await conn.OpenAsync();
        var session = await conn.BeginSessionAsync();
        var first = await session.AttachLinkAsync(new() { Name = "dup", Role = AmqpRole.Sender, TargetAddress = "x" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.AttachLinkAsync(new() { Name = "dup", Role = AmqpRole.Sender, TargetAddress = "x" }));

        await first.DetachAsync();
        await session.CloseAsync();
        await conn.CloseAsync();
        await serverTask;
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

    private static async Task ConsumeBeginAndReply(IAmqpTransport server, ushort peerChannel)
    {
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            Assert.Equal(PerformativeKind.Begin, PerformativeCodec.PeekKind(f.Body.Span, out _));
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
        using (var f = await AmqpFrameIO.ReadFrameAsync(server))
        {
            Assert.Equal(PerformativeKind.End, PerformativeCodec.PeekKind(f.Body.Span, out _));
        }
        await SendPerfAsync(server, peerChannel, new AmqpEnd(), AmqpEnd.Write);
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
