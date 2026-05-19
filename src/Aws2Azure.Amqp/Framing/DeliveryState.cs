using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 delivery-state descriptor codes (§3.4 "Messaging /
/// delivery state"): the four terminal outcomes a receiver may use
/// to settle a delivery (<c>accepted</c>, <c>rejected</c>,
/// <c>released</c>, <c>modified</c>) plus the in-flight transactional
/// states that this profile does not yet use. The values appear as
/// the <c>state</c> field on <see cref="AmqpDisposition"/> and
/// <see cref="AmqpTransfer"/>.
/// </summary>
internal static class DeliveryStateDescriptor
{
    public const ulong Received = 0x0000_0000_0000_0023UL;
    public const ulong Accepted = 0x0000_0000_0000_0024UL;
    public const ulong Rejected = 0x0000_0000_0000_0025UL;
    public const ulong Released = 0x0000_0000_0000_0026UL;
    public const ulong Modified = 0x0000_0000_0000_0027UL;
}

/// <summary>
/// Categorical kind of a delivery-state value; the result of
/// <see cref="DeliveryState.PeekKind"/>.
/// </summary>
internal enum DeliveryStateKind
{
    Unknown = 0,
    Received,
    Accepted,
    Rejected,
    Released,
    Modified,
}

/// <summary>
/// Codecs for the four terminal AMQP 1.0 delivery-state outcomes.
/// Each is a described type whose value is a list (possibly empty).
/// </summary>
internal static class DeliveryState
{
    /// <summary>Identifies which delivery-state descriptor begins at <paramref name="source"/>.</summary>
    public static DeliveryStateKind PeekKind(ReadOnlySpan<byte> source, out ulong descriptor)
    {
        var offset = AmqpCompoundReader.ReadDescribedHeader(source);
        descriptor = AmqpPrimitiveReader.ReadULong(source[offset..], out _);
        return descriptor switch
        {
            DeliveryStateDescriptor.Received => DeliveryStateKind.Received,
            DeliveryStateDescriptor.Accepted => DeliveryStateKind.Accepted,
            DeliveryStateDescriptor.Rejected => DeliveryStateKind.Rejected,
            DeliveryStateDescriptor.Released => DeliveryStateKind.Released,
            DeliveryStateDescriptor.Modified => DeliveryStateKind.Modified,
            _ => DeliveryStateKind.Unknown,
        };
    }
}

/// <summary>
/// <c>accepted</c> (§3.4.3) — receiver acknowledges the message as
/// processed. Always encoded as a described list0.
/// </summary>
internal readonly record struct Accepted
{
    public const ulong Descriptor = DeliveryStateDescriptor.Accepted;

    public static void Write(Span<byte> destination, out int written)
    {
        Span<byte> scratch = stackalloc byte[1];
        Span<int> offsets = stackalloc int[] { 0 };
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..0], offsets, 0);
    }

    public static void Read(ReadOnlyMemory<byte> source, out int consumed)
    {
        PerformativeCodec.ReadPerformativeFields(source.Span, Descriptor, out _, out consumed);
    }
}

/// <summary>
/// <c>rejected</c> (§3.4.4) — receiver refuses the message; carries
/// the diagnostic <see cref="AmqpError"/>. Service Bus uses this to
/// dead-letter on the broker side when configured.
/// </summary>
internal readonly record struct Rejected
{
    public const ulong Descriptor = DeliveryStateDescriptor.Rejected;

    /// <summary>Optional error describing why the message was rejected.</summary>
    public ReadOnlyMemory<byte> Error { get; init; }

    public static void Write(Span<byte> destination, in Rejected value, out int written)
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

    public static void Read(ReadOnlyMemory<byte> source, out Rejected value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        var error = view.Count >= 1
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;
        value = new Rejected { Error = error };
    }
}

/// <summary>
/// <c>released</c> (§3.4.5) — receiver returns the message to the
/// broker without indicating success or failure. Service Bus uses
/// this for receiver disconnects on peek-lock receive mode (the
/// message becomes available to other receivers immediately).
/// </summary>
internal readonly record struct Released
{
    public const ulong Descriptor = DeliveryStateDescriptor.Released;

    public static void Write(Span<byte> destination, out int written)
    {
        Span<byte> scratch = stackalloc byte[1];
        Span<int> offsets = stackalloc int[] { 0 };
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..0], offsets, 0);
    }

    public static void Read(ReadOnlyMemory<byte> source, out int consumed)
    {
        PerformativeCodec.ReadPerformativeFields(source.Span, Descriptor, out _, out consumed);
    }
}

/// <summary>
/// <c>modified</c> (§3.4.6) — receiver requeues the message with
/// optional flags. Service Bus honours <c>delivery-failed</c> (incr.
/// dlq counter) and <c>undeliverable-here</c> (route to DLQ
/// immediately).
/// </summary>
internal readonly record struct Modified
{
    public const ulong Descriptor = DeliveryStateDescriptor.Modified;

    public bool? DeliveryFailed { get; init; }
    public bool? UndeliverableHere { get; init; }
    /// <summary>Optional annotations to merge into the message before re-delivery; opaque AMQP-encoded <c>fields</c> map.</summary>
    public ReadOnlyMemory<byte> MessageAnnotations { get; init; }

    public static void Write(Span<byte> destination, in Modified value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[4];
        int o = 0;
        int len;

        offsets[0] = o;
        if (value.DeliveryFailed is { } df) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], df, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[1] = o;
        if (value.UndeliverableHere is { } uh) AmqpPrimitiveWriter.WriteBoolean(scratch[o..], uh, out len);
        else PerformativeCodec.WriteNullField(scratch[o..], out len);
        o += len;

        offsets[2] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.MessageAnnotations.Span, out len); o += len;

        offsets[3] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 3);
    }

    public static void Read(ReadOnlyMemory<byte> source, out Modified value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        bool? deliveryFailed = null;
        if (view.Count >= 1 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            deliveryFailed = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        bool? undeliverableHere = null;
        if (view.Count >= 2 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            undeliverableHere = AmqpPrimitiveReader.ReadBoolean(els[o..], out len); o += len;
        }
        var annotations = view.Count >= 3
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;

        value = new Modified
        {
            DeliveryFailed = deliveryFailed,
            UndeliverableHere = undeliverableHere,
            MessageAnnotations = annotations,
        };
    }
}
