using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 <c>header</c> message section (§3.2.1, descriptor 0x70).
/// The header carries transport-level metadata about the message that
/// the broker computes (e.g. <see cref="DeliveryCount"/>) or the sender
/// requests (e.g. <see cref="Ttl"/>). This profile is read-only — the
/// proxy never authors a header — and only surfaces the fields the SQS
/// translation needs:
/// <list type="bullet">
///   <item><c>delivery-count</c> (field 4, uint) → SQS
///   <c>ApproximateReceiveCount</c>.</item>
/// </list>
/// Other fields are decoded best-effort but not validated against the
/// spec; an unknown variant on any single field surfaces as <c>null</c>
/// rather than throwing, matching the rest of the codec's tolerance
/// policy for peer-extended payloads.
/// </summary>
internal readonly record struct AmqpHeader
{
    public const ulong Descriptor = MessageSectionDescriptor.Header;

    public bool? Durable { get; init; }
    public byte? Priority { get; init; }
    public uint? Ttl { get; init; }
    public bool? FirstAcquirer { get; init; }
    public uint? DeliveryCount { get; init; }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpHeader value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;

        bool? durable = view.Count >= 1 ? ReadBoolOrSkip(els, ref o) : null;
        byte? priority = view.Count >= 2 ? ReadUByteOrSkip(els, ref o) : null;
        uint? ttl = view.Count >= 3 ? ReadUIntOrSkip(els, ref o) : null;
        bool? firstAcquirer = view.Count >= 4 ? ReadBoolOrSkip(els, ref o) : null;
        uint? deliveryCount = view.Count >= 5 ? ReadUIntOrSkip(els, ref o) : null;

        value = new AmqpHeader
        {
            Durable = durable,
            Priority = priority,
            Ttl = ttl,
            FirstAcquirer = firstAcquirer,
            DeliveryCount = deliveryCount,
        };
    }

    private static bool? ReadBoolOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        switch (fc)
        {
            case AmqpFormatCode.BooleanTrue:  o += 1; return true;
            case AmqpFormatCode.BooleanFalse: o += 1; return false;
            case AmqpFormatCode.Boolean:      o += 2; return els[o - 1] != 0;
            default:
                o += AmqpValueScanner.Measure(els[o..]);
                return null;
        }
    }

    private static byte? ReadUByteOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        if (fc == AmqpFormatCode.UByte)
        {
            var v = els[o + 1];
            o += 2;
            return v;
        }
        o += AmqpValueScanner.Measure(els[o..]);
        return null;
    }

    private static uint? ReadUIntOrSkip(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return null;
        var fc = els[o];
        switch (fc)
        {
            case AmqpFormatCode.UInt0:
                o += 1;
                return 0u;
            case AmqpFormatCode.UIntSmall:
                var small = els[o + 1];
                o += 2;
                return small;
            case AmqpFormatCode.UInt:
                var big = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(els.Slice(o + 1, 4));
                o += 5;
                return big;
            default:
                o += AmqpValueScanner.Measure(els[o..]);
                return null;
        }
    }
}
