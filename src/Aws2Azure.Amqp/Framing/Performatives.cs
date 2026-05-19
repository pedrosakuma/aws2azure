using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 transport performatives (descriptors 0x10–0x18) as
/// hand-rolled <c>readonly record struct</c> codecs. Each performative:
/// <list type="bullet">
///   <item><description>declares <c>Descriptor</c> as a <c>ulong</c> constant matching <see cref="PerformativeDescriptor"/>;</description></item>
///   <item><description>exposes <c>Write(Span&lt;byte&gt; destination, in T value, out int written)</c>;</description></item>
///   <item><description>exposes <c>Read(ReadOnlyMemory&lt;byte&gt; source, out T value, out int consumed)</c>.</description></item>
/// </list>
/// Opaque-typed fields (source, target, error, delivery-state,
/// properties maps, capability symbol arrays) are modeled as
/// <see cref="ReadOnlyMemory{T}"/> slices of the original buffer
/// (empty &#8801; absent) — later slices add typed structs for
/// source/target/error and replace these placeholders.
/// </summary>
internal static class Performatives
{
    /// <summary>
    /// Scratch buffer size used when serialising a performative. Sized
    /// to comfortably hold any transport performative produced by the
    /// SQS-over-Service-Bus profile (typical open ≈ 200 bytes; attach
    /// with source filter ≈ 1 KB; flow / disposition ≈ 80 bytes).
    /// </summary>
    public const int ScratchSize = 4096;
}

// =====================================================================
// open  (descriptor 0x10) — connection negotiation
// =====================================================================

internal readonly record struct AmqpOpen
{
    public const ulong Descriptor = PerformativeDescriptor.Open;

    /// <summary>Container identifier (REQUIRED).</summary>
    public string ContainerId { get; init; }
    public string? Hostname { get; init; }
    /// <summary>Maximum frame size the peer may send; default 4 294 967 295.</summary>
    public uint? MaxFrameSize { get; init; }
    public ushort? ChannelMax { get; init; }
    /// <summary>Idle timeout in milliseconds (the peer must send within this window).</summary>
    public uint? IdleTimeoutMilliseconds { get; init; }
    public ReadOnlyMemory<byte> OutgoingLocales { get; init; }
    public ReadOnlyMemory<byte> IncomingLocales { get; init; }
    public ReadOnlyMemory<byte> OfferedCapabilities { get; init; }
    public ReadOnlyMemory<byte> DesiredCapabilities { get; init; }
    public ReadOnlyMemory<byte> Properties { get; init; }

    public static void Write(Span<byte> destination, in AmqpOpen value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[11];
        int o = 0;

        offsets[0] = o;
        AmqpVariableWriter.WriteString(scratch[o..], value.ContainerId ?? string.Empty, out var len);
        o += len;

        offsets[1] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.Hostname, out len); o += len;

        offsets[2] = o;
        if (value.MaxFrameSize is { } mfs) AmqpPrimitiveWriter.WriteUInt(scratch[o..], mfs, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[3] = o;
        if (value.ChannelMax is { } cm) AmqpPrimitiveWriter.WriteUShort(scratch[o..], cm, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[4] = o;
        if (value.IdleTimeoutMilliseconds is { } it) AmqpPrimitiveWriter.WriteUInt(scratch[o..], it, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[5] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.OutgoingLocales.Span, out len); o += len;

        offsets[6] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.IncomingLocales.Span, out len); o += len;

        offsets[7] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.OfferedCapabilities.Span, out len); o += len;

        offsets[8] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.DesiredCapabilities.Span, out len); o += len;

        offsets[9] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Properties.Span, out len); o += len;

        offsets[10] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 10);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpOpen value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        // container-id (REQUIRED)
        string containerId;
        if (view.Count >= 1 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            containerId = AmqpVariableReader.ReadString(els[o..], out len); o += len;
        }
        else
        {
            throw new InvalidDataException("open.container-id is required.");
        }

        string? hostname = null;
        if (view.Count >= 2 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            hostname = AmqpVariableReader.ReadString(els[o..], out len); o += len;
        }

        uint? maxFrameSize = null;
        if (view.Count >= 3 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            maxFrameSize = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }

        ushort? channelMax = null;
        if (view.Count >= 4 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            channelMax = AmqpPrimitiveReader.ReadUShort(els[o..], out len); o += len;
        }

        uint? idleTimeout = null;
        if (view.Count >= 5 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            idleTimeout = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }

        var outgoing = view.Count >= 6
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        var incoming = view.Count >= 7
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        var offered = view.Count >= 8
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        var desired = view.Count >= 9
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        var properties = view.Count >= 10
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;

        value = new AmqpOpen
        {
            ContainerId = containerId,
            Hostname = hostname,
            MaxFrameSize = maxFrameSize,
            ChannelMax = channelMax,
            IdleTimeoutMilliseconds = idleTimeout,
            OutgoingLocales = outgoing,
            IncomingLocales = incoming,
            OfferedCapabilities = offered,
            DesiredCapabilities = desired,
            Properties = properties,
        };
    }
}

// =====================================================================
// begin (descriptor 0x11) — session negotiation
// =====================================================================

internal readonly record struct AmqpBegin
{
    public const ulong Descriptor = PerformativeDescriptor.Begin;

    public ushort? RemoteChannel { get; init; }
    public uint NextOutgoingId { get; init; }
    public uint IncomingWindow { get; init; }
    public uint OutgoingWindow { get; init; }
    public uint? HandleMax { get; init; }
    public ReadOnlyMemory<byte> OfferedCapabilities { get; init; }
    public ReadOnlyMemory<byte> DesiredCapabilities { get; init; }
    public ReadOnlyMemory<byte> Properties { get; init; }

    public static void Write(Span<byte> destination, in AmqpBegin value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[9];
        int o = 0;
        int len;

        offsets[0] = o;
        if (value.RemoteChannel is { } rc) AmqpPrimitiveWriter.WriteUShort(scratch[o..], rc, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[1] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.NextOutgoingId, out len); o += len;

        offsets[2] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.IncomingWindow, out len); o += len;

        offsets[3] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.OutgoingWindow, out len); o += len;

        offsets[4] = o;
        if (value.HandleMax is { } hm) AmqpPrimitiveWriter.WriteUInt(scratch[o..], hm, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[5] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.OfferedCapabilities.Span, out len); o += len;
        offsets[6] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.DesiredCapabilities.Span, out len); o += len;
        offsets[7] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Properties.Span, out len); o += len;

        offsets[8] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 8);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpBegin value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        ushort? remoteChannel = null;
        if (view.Count >= 1 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            remoteChannel = AmqpPrimitiveReader.ReadUShort(els[o..], out len); o += len;
        }

        uint nextOutgoingId = 0, incomingWindow = 0, outgoingWindow = 0;
        if (view.Count >= 2)
        {
            nextOutgoingId = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        else throw new InvalidDataException("begin.next-outgoing-id is required.");
        if (view.Count >= 3)
        {
            incomingWindow = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        else throw new InvalidDataException("begin.incoming-window is required.");
        if (view.Count >= 4)
        {
            outgoingWindow = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        else throw new InvalidDataException("begin.outgoing-window is required.");

        uint? handleMax = null;
        if (view.Count >= 5 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            handleMax = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }

        var offered = view.Count >= 6
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        var desired = view.Count >= 7
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        var properties = view.Count >= 8
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;

        value = new AmqpBegin
        {
            RemoteChannel = remoteChannel,
            NextOutgoingId = nextOutgoingId,
            IncomingWindow = incomingWindow,
            OutgoingWindow = outgoingWindow,
            HandleMax = handleMax,
            OfferedCapabilities = offered,
            DesiredCapabilities = desired,
            Properties = properties,
        };
    }
}

// =====================================================================
// attach (descriptor 0x12) — link attach (sender/receiver bind)
// =====================================================================

/// <summary>AMQP 1.0 link role: sender (false) or receiver (true).</summary>
internal enum AmqpRole : byte { Sender = 0, Receiver = 1 }

/// <summary>AMQP 1.0 sender settle modes (§2.6.6).</summary>
internal enum AmqpSenderSettleMode : byte { Unsettled = 0, Settled = 1, Mixed = 2 }

/// <summary>AMQP 1.0 receiver settle modes (§2.6.7).</summary>
internal enum AmqpReceiverSettleMode : byte { First = 0, Second = 1 }

internal readonly record struct AmqpAttach
{
    public const ulong Descriptor = PerformativeDescriptor.Attach;

    public string Name { get; init; }
    public uint Handle { get; init; }
    public AmqpRole Role { get; init; }
    public AmqpSenderSettleMode? SenderSettleMode { get; init; }
    public AmqpReceiverSettleMode? ReceiverSettleMode { get; init; }
    public ReadOnlyMemory<byte> Source { get; init; }
    public ReadOnlyMemory<byte> Target { get; init; }
    public ReadOnlyMemory<byte> Unsettled { get; init; }
    public bool? IncompleteUnsettled { get; init; }
    /// <summary>REQUIRED when <see cref="Role"/> is <see cref="AmqpRole.Sender"/>; the initial transfer sequence number.</summary>
    public uint? InitialDeliveryCount { get; init; }
    public ulong? MaxMessageSize { get; init; }
    public ReadOnlyMemory<byte> OfferedCapabilities { get; init; }
    public ReadOnlyMemory<byte> DesiredCapabilities { get; init; }
    public ReadOnlyMemory<byte> Properties { get; init; }

    public static void Write(Span<byte> destination, in AmqpAttach value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[15];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpVariableWriter.WriteString(scratch[o..], value.Name ?? string.Empty, out len); o += len;

        offsets[1] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.Handle, out len); o += len;

        offsets[2] = o;
        AmqpPrimitiveWriter.WriteBoolean(scratch[o..], value.Role == AmqpRole.Receiver, out len); o += len;

        offsets[3] = o;
        if (value.SenderSettleMode is { } ssm) AmqpPrimitiveWriter.WriteUByte(scratch[o..], (byte)ssm, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[4] = o;
        if (value.ReceiverSettleMode is { } rsm) AmqpPrimitiveWriter.WriteUByte(scratch[o..], (byte)rsm, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[5] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Source.Span, out len); o += len;
        offsets[6] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Target.Span, out len); o += len;
        offsets[7] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Unsettled.Span, out len); o += len;

        offsets[8] = o;
        if (value.IncompleteUnsettled is { } iu) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], iu, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[9] = o;
        if (value.InitialDeliveryCount is { } idc) AmqpPrimitiveWriter.WriteUInt(scratch[o..], idc, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[10] = o;
        if (value.MaxMessageSize is { } mms) AmqpPrimitiveWriter.WriteULong(scratch[o..], mms, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[11] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.OfferedCapabilities.Span, out len); o += len;
        offsets[12] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.DesiredCapabilities.Span, out len); o += len;
        offsets[13] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Properties.Span, out len); o += len;

        offsets[14] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 14);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpAttach value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        if (view.Count < 3)
        {
            throw new InvalidDataException("attach requires name/handle/role.");
        }
        var name = AmqpVariableReader.ReadString(els[o..], out len); o += len;
        var handle = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        var role = AmqpPrimitiveReader.ReadBoolean(els[o..], out len) ? AmqpRole.Receiver : AmqpRole.Sender;
        o += len;

        AmqpSenderSettleMode? ssm = null;
        if (view.Count >= 4 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            ssm = (AmqpSenderSettleMode)AmqpPrimitiveReader.ReadUByte(els[o..], out len); o += len;
        }
        AmqpReceiverSettleMode? rsm = null;
        if (view.Count >= 5 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            rsm = (AmqpReceiverSettleMode)AmqpPrimitiveReader.ReadUByte(els[o..], out len); o += len;
        }
        var src = view.Count >= 6 ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o) : ReadOnlyMemory<byte>.Empty;
        var tgt = view.Count >= 7 ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o) : ReadOnlyMemory<byte>.Empty;
        var unsettled = view.Count >= 8 ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o) : ReadOnlyMemory<byte>.Empty;

        bool? incompleteUnsettled = null;
        if (view.Count >= 9 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            incompleteUnsettled = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        uint? initialDeliveryCount = null;
        if (view.Count >= 10 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            initialDeliveryCount = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        ulong? maxMessageSize = null;
        if (view.Count >= 11 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            maxMessageSize = AmqpPrimitiveReader.ReadULong(els[o..], out len); o += len;
        }
        var offered = view.Count >= 12 ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o) : ReadOnlyMemory<byte>.Empty;
        var desired = view.Count >= 13 ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o) : ReadOnlyMemory<byte>.Empty;
        var properties = view.Count >= 14 ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o) : ReadOnlyMemory<byte>.Empty;

        value = new AmqpAttach
        {
            Name = name,
            Handle = handle,
            Role = role,
            SenderSettleMode = ssm,
            ReceiverSettleMode = rsm,
            Source = src,
            Target = tgt,
            Unsettled = unsettled,
            IncompleteUnsettled = incompleteUnsettled,
            InitialDeliveryCount = initialDeliveryCount,
            MaxMessageSize = maxMessageSize,
            OfferedCapabilities = offered,
            DesiredCapabilities = desired,
            Properties = properties,
        };
    }
}

// =====================================================================
// flow (descriptor 0x13) — session/link flow control
// =====================================================================

internal readonly record struct AmqpFlow
{
    public const ulong Descriptor = PerformativeDescriptor.Flow;

    public uint? NextIncomingId { get; init; }
    public uint IncomingWindow { get; init; }
    public uint NextOutgoingId { get; init; }
    public uint OutgoingWindow { get; init; }
    public uint? Handle { get; init; }
    public uint? DeliveryCount { get; init; }
    public uint? LinkCredit { get; init; }
    public uint? Available { get; init; }
    public bool? Drain { get; init; }
    public bool? Echo { get; init; }
    public ReadOnlyMemory<byte> Properties { get; init; }

    public static void Write(Span<byte> destination, in AmqpFlow value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[12];
        int o = 0;
        int len;

        offsets[0] = o;
        if (value.NextIncomingId is { } nii) AmqpPrimitiveWriter.WriteUInt(scratch[o..], nii, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[1] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.IncomingWindow, out len); o += len;
        offsets[2] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.NextOutgoingId, out len); o += len;
        offsets[3] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.OutgoingWindow, out len); o += len;

        offsets[4] = o;
        if (value.Handle is { } h) AmqpPrimitiveWriter.WriteUInt(scratch[o..], h, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[5] = o;
        if (value.DeliveryCount is { } dc) AmqpPrimitiveWriter.WriteUInt(scratch[o..], dc, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[6] = o;
        if (value.LinkCredit is { } lc) AmqpPrimitiveWriter.WriteUInt(scratch[o..], lc, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[7] = o;
        if (value.Available is { } av) AmqpPrimitiveWriter.WriteUInt(scratch[o..], av, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[8] = o;
        if (value.Drain is { } d) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], d, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[9] = o;
        if (value.Echo is { } e) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], e, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[10] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Properties.Span, out len); o += len;

        offsets[11] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 11);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpFlow value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        uint? nextIncomingId = null;
        if (view.Count >= 1 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            nextIncomingId = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }

        uint incomingWindow = 0, nextOutgoingId = 0, outgoingWindow = 0;
        if (view.Count < 4) throw new InvalidDataException("flow requires incoming-window/next-outgoing-id/outgoing-window.");
        incomingWindow = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        nextOutgoingId = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        outgoingWindow = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;

        uint? handle = null;
        if (view.Count >= 5 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            handle = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        uint? deliveryCount = null;
        if (view.Count >= 6 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            deliveryCount = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        uint? linkCredit = null;
        if (view.Count >= 7 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            linkCredit = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        uint? available = null;
        if (view.Count >= 8 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            available = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        bool? drain = null;
        if (view.Count >= 9 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            drain = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        bool? echo = null;
        if (view.Count >= 10 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            echo = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        var properties = view.Count >= 11
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;

        value = new AmqpFlow
        {
            NextIncomingId = nextIncomingId,
            IncomingWindow = incomingWindow,
            NextOutgoingId = nextOutgoingId,
            OutgoingWindow = outgoingWindow,
            Handle = handle,
            DeliveryCount = deliveryCount,
            LinkCredit = linkCredit,
            Available = available,
            Drain = drain,
            Echo = echo,
            Properties = properties,
        };
    }
}

// =====================================================================
// transfer (descriptor 0x14) — message delivery
// =====================================================================

internal readonly record struct AmqpTransfer
{
    public const ulong Descriptor = PerformativeDescriptor.Transfer;

    public uint Handle { get; init; }
    public uint? DeliveryId { get; init; }
    /// <summary>Up to 32 bytes per spec; empty &#8801; absent on the wire.</summary>
    public ReadOnlyMemory<byte> DeliveryTag { get; init; }
    public uint? MessageFormat { get; init; }
    public bool? Settled { get; init; }
    public bool? More { get; init; }
    public AmqpReceiverSettleMode? ReceiverSettleMode { get; init; }
    public ReadOnlyMemory<byte> State { get; init; }
    public bool? Resume { get; init; }
    public bool? Aborted { get; init; }
    public bool? Batchable { get; init; }

    public static void Write(Span<byte> destination, in AmqpTransfer value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[12];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.Handle, out len); o += len;

        offsets[1] = o;
        if (value.DeliveryId is { } did) AmqpPrimitiveWriter.WriteUInt(scratch[o..], did, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[2] = o;
        PerformativeCodec.WriteBinaryOrNull(scratch[o..], value.DeliveryTag.Span, !value.DeliveryTag.IsEmpty, out len);
        o += len;

        offsets[3] = o;
        if (value.MessageFormat is { } mf) AmqpPrimitiveWriter.WriteUInt(scratch[o..], mf, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[4] = o;
        if (value.Settled is { } s) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], s, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[5] = o;
        if (value.More is { } m) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], m, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[6] = o;
        if (value.ReceiverSettleMode is { } rsm) AmqpPrimitiveWriter.WriteUByte(scratch[o..], (byte)rsm, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[7] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.State.Span, out len); o += len;

        offsets[8] = o;
        if (value.Resume is { } r) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], r, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[9] = o;
        if (value.Aborted is { } a) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], a, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[10] = o;
        if (value.Batchable is { } b) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], b, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[11] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 11);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpTransfer value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        if (view.Count < 1) throw new InvalidDataException("transfer requires handle.");
        var handle = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;

        uint? deliveryId = null;
        if (view.Count >= 2 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            deliveryId = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }

        ReadOnlyMemory<byte> deliveryTag = ReadOnlyMemory<byte>.Empty;
        if (view.Count >= 3 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            // Capture as ReadOnlyMemory of the source.
            var binLen = AmqpValueScanner.Measure(els[o..]);
            // Strip the binary constructor + length prefix.
            var binPayloadStart = els[o] == AmqpFormatCode.Binary8 ? 2 : 5;
            deliveryTag = source.Slice(elementsOffset + o + binPayloadStart, binLen - binPayloadStart);
            o += binLen;
        }

        uint? messageFormat = null;
        if (view.Count >= 4 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            messageFormat = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        bool? settled = null;
        if (view.Count >= 5 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            settled = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        bool? more = null;
        if (view.Count >= 6 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            more = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        AmqpReceiverSettleMode? rsm = null;
        if (view.Count >= 7 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            rsm = (AmqpReceiverSettleMode)AmqpPrimitiveReader.ReadUByte(els[o..], out len); o += len;
        }
        var state = view.Count >= 8
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        bool? resume = null;
        if (view.Count >= 9 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            resume = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        bool? aborted = null;
        if (view.Count >= 10 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            aborted = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        bool? batchable = null;
        if (view.Count >= 11 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            batchable = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }

        value = new AmqpTransfer
        {
            Handle = handle,
            DeliveryId = deliveryId,
            DeliveryTag = deliveryTag,
            MessageFormat = messageFormat,
            Settled = settled,
            More = more,
            ReceiverSettleMode = rsm,
            State = state,
            Resume = resume,
            Aborted = aborted,
            Batchable = batchable,
        };
    }
}

// =====================================================================
// disposition (descriptor 0x15) — settle/state update
// =====================================================================

internal readonly record struct AmqpDisposition
{
    public const ulong Descriptor = PerformativeDescriptor.Disposition;

    public AmqpRole Role { get; init; }
    public uint First { get; init; }
    public uint? Last { get; init; }
    public bool? Settled { get; init; }
    public ReadOnlyMemory<byte> State { get; init; }
    public bool? Batchable { get; init; }

    public static void Write(Span<byte> destination, in AmqpDisposition value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[7];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpPrimitiveWriter.WriteBoolean(scratch[o..], value.Role == AmqpRole.Receiver, out len); o += len;
        offsets[1] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.First, out len); o += len;

        offsets[2] = o;
        if (value.Last is { } l) AmqpPrimitiveWriter.WriteUInt(scratch[o..], l, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[3] = o;
        if (value.Settled is { } s) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], s, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[4] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.State.Span, out len); o += len;

        offsets[5] = o;
        if (value.Batchable is { } b) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], b, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[6] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 6);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpDisposition value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        if (view.Count < 2) throw new InvalidDataException("disposition requires role and first.");
        var role = AmqpPrimitiveReader.ReadBoolean(els[o..], out len) ? AmqpRole.Receiver : AmqpRole.Sender;
        o += len;
        var first = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;

        uint? last = null;
        if (view.Count >= 3 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            last = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;
        }
        bool? settled = null;
        if (view.Count >= 4 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            settled = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        var state = view.Count >= 5
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        bool? batchable = null;
        if (view.Count >= 6 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            batchable = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }

        value = new AmqpDisposition
        {
            Role = role,
            First = first,
            Last = last,
            Settled = settled,
            State = state,
            Batchable = batchable,
        };
    }
}

// =====================================================================
// detach (descriptor 0x16) — link detach
// =====================================================================

internal readonly record struct AmqpDetach
{
    public const ulong Descriptor = PerformativeDescriptor.Detach;

    public uint Handle { get; init; }
    public bool? Closed { get; init; }
    public ReadOnlyMemory<byte> Error { get; init; }

    public static void Write(Span<byte> destination, in AmqpDetach value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[4];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpPrimitiveWriter.WriteUInt(scratch[o..], value.Handle, out len); o += len;

        offsets[1] = o;
        if (value.Closed is { } c) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], c, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[2] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Error.Span, out len); o += len;

        offsets[3] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 3);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpDetach value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        if (view.Count < 1) throw new InvalidDataException("detach requires handle.");
        var handle = AmqpPrimitiveReader.ReadUInt(els[o..], out len); o += len;

        bool? closed = null;
        if (view.Count >= 2 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            closed = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        var error = view.Count >= 3
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;

        value = new AmqpDetach { Handle = handle, Closed = closed, Error = error };
    }
}

// =====================================================================
// end   (descriptor 0x17) — session end
// close (descriptor 0x18) — connection close
// =====================================================================

internal readonly record struct AmqpEnd
{
    public const ulong Descriptor = PerformativeDescriptor.End;
    public ReadOnlyMemory<byte> Error { get; init; }

    public static void Write(Span<byte> destination, in AmqpEnd value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[2];
        int o = 0;
        int len;

        offsets[0] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Error.Span, out len); o += len;

        offsets[1] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 1);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpEnd value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        var error = view.Count >= 1
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        value = new AmqpEnd { Error = error };
    }
}

internal readonly record struct AmqpClose
{
    public const ulong Descriptor = PerformativeDescriptor.Close;
    public ReadOnlyMemory<byte> Error { get; init; }

    public static void Write(Span<byte> destination, in AmqpClose value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[2];
        int o = 0;
        int len;

        offsets[0] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Error.Span, out len); o += len;

        offsets[1] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 1);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpClose value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        var error = view.Count >= 1
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        value = new AmqpClose { Error = error };
    }
}
