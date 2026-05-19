using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 messaging <c>source</c> terminus (§3.5.3, descriptor 0x28).
/// For Slice 5b we model only the <c>address</c> field; the remaining
/// fields (durable, expiry-policy, timeout, dynamic, dynamic-node-properties,
/// distribution-mode, filter, default-outcome, outcomes, capabilities) are
/// elided to defaults on write and ignored on read. Future slices grow
/// the surface (notably filters for FIFO sessions).
/// </summary>
internal readonly record struct AmqpSource
{
    public const ulong Descriptor = 0x0000_0000_0000_0028UL;

    public string? Address { get; init; }

    public static void Write(Span<byte> destination, in AmqpSource value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[2];
        int o = 0;

        offsets[0] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.Address, out var len);
        o += len;

        offsets[1] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 1);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpSource value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out _, out consumed);
        var els = view.Elements;
        int o = 0;
        string? address = null;
        if (view.Count >= 1 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            address = AmqpVariableReader.ReadString(els[o..], out var len);
            o += len;
        }
        value = new AmqpSource { Address = address };
    }
}

/// <summary>
/// AMQP 1.0 messaging <c>target</c> terminus (§3.5.4, descriptor 0x29).
/// Slice 5b models only <c>address</c>; other fields (durable, expiry,
/// timeout, dynamic, dynamic-node-properties, capabilities) elide to
/// defaults.
/// </summary>
internal readonly record struct AmqpTarget
{
    public const ulong Descriptor = 0x0000_0000_0000_0029UL;

    public string? Address { get; init; }

    public static void Write(Span<byte> destination, in AmqpTarget value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[2];
        int o = 0;

        offsets[0] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.Address, out var len);
        o += len;

        offsets[1] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 1);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpTarget value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out _, out consumed);
        var els = view.Elements;
        int o = 0;
        string? address = null;
        if (view.Count >= 1 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            address = AmqpVariableReader.ReadString(els[o..], out var len);
            o += len;
        }
        value = new AmqpTarget { Address = address };
    }
}
