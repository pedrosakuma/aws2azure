using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.UnitTests.Amqp;

/// <summary>
/// AMQP 1.0 transport-performative codec tests (Phase 2.5 Slice 3b).
/// Each performative gets a default-values round trip, a full-fields
/// round trip, and (where applicable) a trailing-null elision check.
/// Cross-cutting tests exercise descriptor mismatch rejection and
/// <see cref="PerformativeCodec.PeekKind"/>.
/// </summary>
public sealed class AmqpPerformativeCodecTests
{
    // ---------- helpers ------------------------------------------------

    private static byte[] WriteToArray<T>(T value, Action<T, byte[]> writeAction, int capacity = 4096)
    {
        var buf = new byte[capacity];
        writeAction(value, buf);
        return buf;
    }

    // ---------- Open ---------------------------------------------------

    [Fact]
    public void Open_round_trips_minimal_with_only_container_id()
    {
        var open = new AmqpOpen { ContainerId = "client-1" };
        var buf = new byte[256];
        AmqpOpen.Write(buf, open, out var written);

        // Described constructor + smallulong descriptor (2 bytes) + list8.
        Assert.Equal(AmqpFormatCode.Described, buf[0]);
        Assert.Equal(AmqpFormatCode.ULongSmall, buf[1]);
        Assert.Equal(PerformativeDescriptor.Open, (ulong)buf[2]);
        Assert.Equal(AmqpFormatCode.List8, buf[3]);

        AmqpOpen.Read(buf.AsMemory(0, written), out var decoded, out var consumed);
        Assert.Equal(written, consumed);
        Assert.Equal("client-1", decoded.ContainerId);
        Assert.Null(decoded.Hostname);
        Assert.Null(decoded.MaxFrameSize);
    }

    [Fact]
    public void Open_round_trips_with_all_primitive_fields()
    {
        var open = new AmqpOpen
        {
            ContainerId = "container-xyz",
            Hostname = "namespace.servicebus.windows.net",
            MaxFrameSize = 65_536,
            ChannelMax = 255,
            IdleTimeoutMilliseconds = 30_000,
        };
        var buf = new byte[256];
        AmqpOpen.Write(buf, open, out var written);

        AmqpOpen.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.Equal(open.ContainerId, decoded.ContainerId);
        Assert.Equal(open.Hostname, decoded.Hostname);
        Assert.Equal(open.MaxFrameSize, decoded.MaxFrameSize);
        Assert.Equal(open.ChannelMax, decoded.ChannelMax);
        Assert.Equal(open.IdleTimeoutMilliseconds, decoded.IdleTimeoutMilliseconds);
    }

    [Fact]
    public void Open_trims_trailing_null_fields()
    {
        var open = new AmqpOpen { ContainerId = "c", Hostname = "h" };
        var buf = new byte[64];
        AmqpOpen.Write(buf, open, out var written);

        // After described+desc (3 bytes) we expect list8 with count=2
        // (container-id + hostname); MaxFrameSize..Properties (8 fields)
        // all trim away.
        Assert.Equal(AmqpFormatCode.List8, buf[3]);
        var count = buf[5];
        Assert.Equal(2, count);
    }

    [Fact]
    public void Open_rejects_wrong_descriptor()
    {
        var begin = new AmqpBegin { NextOutgoingId = 0, IncomingWindow = 1, OutgoingWindow = 1 };
        var buf = new byte[64];
        AmqpBegin.Write(buf, begin, out var written);

        Assert.Throws<InvalidDataException>(() =>
        {
            AmqpOpen.Read(buf.AsMemory(0, written), out _, out _);
        });
    }

    // ---------- Begin --------------------------------------------------

    [Fact]
    public void Begin_round_trips_with_required_fields()
    {
        var begin = new AmqpBegin
        {
            NextOutgoingId = 42,
            IncomingWindow = 100,
            OutgoingWindow = 200,
        };
        var buf = new byte[64];
        AmqpBegin.Write(buf, begin, out var written);
        AmqpBegin.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Null(decoded.RemoteChannel);
        Assert.Equal(42u, decoded.NextOutgoingId);
        Assert.Equal(100u, decoded.IncomingWindow);
        Assert.Equal(200u, decoded.OutgoingWindow);
    }

    [Fact]
    public void Begin_round_trips_with_remote_channel_and_handle_max()
    {
        var begin = new AmqpBegin
        {
            RemoteChannel = 7,
            NextOutgoingId = 1,
            IncomingWindow = 5_000,
            OutgoingWindow = 5_000,
            HandleMax = 1023,
        };
        var buf = new byte[64];
        AmqpBegin.Write(buf, begin, out var written);
        AmqpBegin.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal((ushort)7, decoded.RemoteChannel);
        Assert.Equal(1023u, decoded.HandleMax);
    }

    // ---------- Attach -------------------------------------------------

    [Fact]
    public void Attach_round_trips_receiver_with_settle_modes()
    {
        var attach = new AmqpAttach
        {
            Name = "client-recv-link",
            Handle = 0,
            Role = AmqpRole.Receiver,
            SenderSettleMode = AmqpSenderSettleMode.Mixed,
            ReceiverSettleMode = AmqpReceiverSettleMode.First,
            MaxMessageSize = 256 * 1024,
        };
        var buf = new byte[256];
        AmqpAttach.Write(buf, attach, out var written);
        AmqpAttach.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal("client-recv-link", decoded.Name);
        Assert.Equal(0u, decoded.Handle);
        Assert.Equal(AmqpRole.Receiver, decoded.Role);
        Assert.Equal(AmqpSenderSettleMode.Mixed, decoded.SenderSettleMode);
        Assert.Equal(AmqpReceiverSettleMode.First, decoded.ReceiverSettleMode);
        Assert.Equal(256ul * 1024, decoded.MaxMessageSize);
    }

    [Fact]
    public void Attach_round_trips_sender_with_initial_delivery_count()
    {
        var attach = new AmqpAttach
        {
            Name = "send-1",
            Handle = 1,
            Role = AmqpRole.Sender,
            InitialDeliveryCount = 100,
        };
        var buf = new byte[128];
        AmqpAttach.Write(buf, attach, out var written);
        AmqpAttach.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal(AmqpRole.Sender, decoded.Role);
        Assert.Equal(100u, decoded.InitialDeliveryCount);
    }

    [Fact]
    public void Attach_round_trips_opaque_source_and_target_blobs()
    {
        // Construct a tiny "source" described-type blob ourselves to verify
        // that opaque fields are written verbatim and read back as exact
        // memory slices of the original buffer.
        Span<byte> sourceBlobScratch = stackalloc byte[16];
        sourceBlobScratch[0] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(sourceBlobScratch[1..], 0x28UL, out var dl);
        AmqpCompoundWriter.WriteList0(sourceBlobScratch[(1 + dl)..], out var ll);
        var sourceBlobLen = 1 + dl + ll;
        var sourceBlob = sourceBlobScratch[..sourceBlobLen].ToArray();

        var attach = new AmqpAttach
        {
            Name = "link",
            Handle = 0,
            Role = AmqpRole.Receiver,
            Source = sourceBlob,
        };
        var buf = new byte[128];
        AmqpAttach.Write(buf, attach, out var written);
        AmqpAttach.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.True(decoded.Source.Span.SequenceEqual(sourceBlob));
    }

    // ---------- Flow ---------------------------------------------------

    [Fact]
    public void Flow_round_trips_required_session_fields()
    {
        var flow = new AmqpFlow
        {
            IncomingWindow = 2048,
            NextOutgoingId = 5,
            OutgoingWindow = 2048,
        };
        var buf = new byte[64];
        AmqpFlow.Write(buf, flow, out var written);
        AmqpFlow.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Null(decoded.NextIncomingId);
        Assert.Equal(2048u, decoded.IncomingWindow);
        Assert.Equal(5u, decoded.NextOutgoingId);
        Assert.Equal(2048u, decoded.OutgoingWindow);
    }

    [Fact]
    public void Flow_round_trips_link_credit_and_drain()
    {
        var flow = new AmqpFlow
        {
            NextIncomingId = 10,
            IncomingWindow = 1024,
            NextOutgoingId = 0,
            OutgoingWindow = 1024,
            Handle = 0,
            DeliveryCount = 50,
            LinkCredit = 25,
            Drain = true,
            Echo = false,
        };
        var buf = new byte[128];
        AmqpFlow.Write(buf, flow, out var written);
        AmqpFlow.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal(10u, decoded.NextIncomingId);
        Assert.Equal(50u, decoded.DeliveryCount);
        Assert.Equal(25u, decoded.LinkCredit);
        Assert.True(decoded.Drain);
        Assert.False(decoded.Echo);
    }

    // ---------- Transfer -----------------------------------------------

    [Fact]
    public void Transfer_round_trips_with_delivery_tag_and_settled()
    {
        var tag = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var transfer = new AmqpTransfer
        {
            Handle = 0,
            DeliveryId = 999,
            DeliveryTag = tag,
            MessageFormat = 0,
            Settled = false,
            More = false,
        };
        var buf = new byte[128];
        AmqpTransfer.Write(buf, transfer, out var written);
        AmqpTransfer.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal(0u, decoded.Handle);
        Assert.Equal(999u, decoded.DeliveryId);
        Assert.True(decoded.DeliveryTag.Span.SequenceEqual(tag));
        Assert.Equal(0u, decoded.MessageFormat);
        Assert.False(decoded.Settled);
        Assert.False(decoded.More);
    }

    [Fact]
    public void Transfer_round_trips_handle_only()
    {
        var transfer = new AmqpTransfer { Handle = 7 };
        var buf = new byte[32];
        AmqpTransfer.Write(buf, transfer, out var written);
        AmqpTransfer.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal(7u, decoded.Handle);
        Assert.True(decoded.DeliveryTag.IsEmpty);
        Assert.Null(decoded.DeliveryId);
    }

    // ---------- Disposition --------------------------------------------

    [Fact]
    public void Disposition_round_trips_with_state_blob()
    {
        // Accepted state: described-list with descriptor 0x24 + list0.
        Span<byte> accepted = stackalloc byte[8];
        accepted[0] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(accepted[1..], 0x24UL, out var dl);
        AmqpCompoundWriter.WriteList0(accepted[(1 + dl)..], out var ll);
        var stateBlob = accepted[..(1 + dl + ll)].ToArray();

        var disposition = new AmqpDisposition
        {
            Role = AmqpRole.Receiver,
            First = 100,
            Last = 105,
            Settled = true,
            State = stateBlob,
        };
        var buf = new byte[64];
        AmqpDisposition.Write(buf, disposition, out var written);
        AmqpDisposition.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal(AmqpRole.Receiver, decoded.Role);
        Assert.Equal(100u, decoded.First);
        Assert.Equal(105u, decoded.Last);
        Assert.True(decoded.Settled);
        Assert.True(decoded.State.Span.SequenceEqual(stateBlob));
    }

    // ---------- Detach -------------------------------------------------

    [Fact]
    public void Detach_round_trips_with_closed_flag()
    {
        var detach = new AmqpDetach { Handle = 3, Closed = true };
        var buf = new byte[32];
        AmqpDetach.Write(buf, detach, out var written);
        AmqpDetach.Read(buf.AsMemory(0, written), out var decoded, out _);

        Assert.Equal(3u, decoded.Handle);
        Assert.True(decoded.Closed);
        Assert.True(decoded.Error.IsEmpty);
    }

    // ---------- End / Close (single-field) -----------------------------

    [Fact]
    public void End_with_no_error_encodes_as_list0()
    {
        var end = new AmqpEnd();
        var buf = new byte[16];
        AmqpEnd.Write(buf, end, out var written);

        // 0x00 + smallulong(0x17) + list0(0x45) = 4 bytes total.
        Assert.Equal(4, written);
        Assert.Equal(AmqpFormatCode.List0, buf[3]);

        AmqpEnd.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.True(decoded.Error.IsEmpty);
    }

    [Fact]
    public void Close_with_no_error_encodes_as_list0()
    {
        var close = new AmqpClose();
        var buf = new byte[16];
        AmqpClose.Write(buf, close, out var written);

        Assert.Equal(4, written);
        Assert.Equal(AmqpFormatCode.List0, buf[3]);

        AmqpClose.Read(buf.AsMemory(0, written), out var decoded, out _);
        Assert.True(decoded.Error.IsEmpty);
    }

    // ---------- Descriptor peek ----------------------------------------

    [Fact]
    public void PeekKind_dispatches_each_descriptor()
    {
        var cases = new (ulong descriptor, PerformativeKind kind, Action<byte[]> write)[]
        {
            (PerformativeDescriptor.Open, PerformativeKind.Open, b => AmqpOpen.Write(b, new AmqpOpen { ContainerId = "x" }, out _)),
            (PerformativeDescriptor.Begin, PerformativeKind.Begin, b => AmqpBegin.Write(b, new AmqpBegin { NextOutgoingId = 0, IncomingWindow = 1, OutgoingWindow = 1 }, out _)),
            (PerformativeDescriptor.Flow, PerformativeKind.Flow, b => AmqpFlow.Write(b, new AmqpFlow { IncomingWindow = 1, NextOutgoingId = 0, OutgoingWindow = 1 }, out _)),
            (PerformativeDescriptor.End, PerformativeKind.End, b => AmqpEnd.Write(b, default, out _)),
            (PerformativeDescriptor.Close, PerformativeKind.Close, b => AmqpClose.Write(b, default, out _)),
        };
        foreach (var (descriptor, kind, write) in cases)
        {
            var buf = new byte[64];
            write(buf);
            var k = PerformativeCodec.PeekKind(buf, out var d);
            Assert.Equal(descriptor, d);
            Assert.Equal(kind, k);
        }
    }

    [Fact]
    public void PeekKind_returns_unknown_for_unrecognised_descriptor()
    {
        Span<byte> buf = stackalloc byte[16];
        buf[0] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(buf[1..], 0x99UL, out var len);
        AmqpCompoundWriter.WriteList0(buf[(1 + len)..], out _);

        var kind = PerformativeCodec.PeekKind(buf, out var d);
        Assert.Equal(0x99UL, d);
        Assert.Equal(PerformativeKind.Unknown, kind);
    }

    // ---------- Value scanner ------------------------------------------

    [Fact]
    public void ValueScanner_measures_fixed_width_codes()
    {
        ReadOnlySpan<byte> nul = stackalloc byte[] { AmqpFormatCode.Null };
        Assert.Equal(1, AmqpValueScanner.Measure(nul));

        ReadOnlySpan<byte> ushortVal = stackalloc byte[] { AmqpFormatCode.UShort, 0x00, 0x01 };
        Assert.Equal(3, AmqpValueScanner.Measure(ushortVal));

        ReadOnlySpan<byte> uintVal = stackalloc byte[] { AmqpFormatCode.UInt, 0, 0, 0, 1 };
        Assert.Equal(5, AmqpValueScanner.Measure(uintVal));

        ReadOnlySpan<byte> ulongVal = stackalloc byte[] { AmqpFormatCode.ULong, 0, 0, 0, 0, 0, 0, 0, 1 };
        Assert.Equal(9, AmqpValueScanner.Measure(ulongVal));
    }

    [Fact]
    public void ValueScanner_measures_variable_and_compound_codes()
    {
        Span<byte> bin = stackalloc byte[8];
        AmqpVariableWriter.WriteBinary(bin, stackalloc byte[] { 0xAA, 0xBB, 0xCC }, out var binLen);
        Assert.Equal(binLen, AmqpValueScanner.Measure(bin[..binLen]));

        Span<byte> list = stackalloc byte[16];
        AmqpCompoundWriter.WriteList0(list, out var listLen);
        Assert.Equal(listLen, AmqpValueScanner.Measure(list[..listLen]));
    }

    [Fact]
    public void ValueScanner_measures_described_recursively()
    {
        Span<byte> described = stackalloc byte[16];
        described[0] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(described[1..], 0x10UL, out var dl);
        AmqpCompoundWriter.WriteList0(described[(1 + dl)..], out var ll);
        var total = 1 + dl + ll;

        Assert.Equal(total, AmqpValueScanner.Measure(described[..total]));
    }
}
