using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 messaging <c>source</c> terminus (§3.5.3, descriptor 0x28).
/// Currently models the <c>address</c> field plus the <c>filter</c> map
/// (field index 8) — the latter is the carrier for Service Bus's
/// <c>com.microsoft:session-filter</c> needed by FIFO/session receivers
/// (slice 7). Remaining fields (durable, expiry-policy, timeout, dynamic,
/// dynamic-node-properties, distribution-mode, default-outcome, outcomes,
/// capabilities) are elided to defaults on write and ignored on read.
/// </summary>
internal readonly record struct AmqpSource
{
    public const ulong Descriptor = 0x0000_0000_0000_0028UL;

    public string? Address { get; init; }

    /// <summary>
    /// Opaque AMQP-encoded <c>filter</c> map (§3.5.3 field 8 — a
    /// <c>map&lt;symbol,*&gt;</c>). Empty means "no filter"; on the wire
    /// the field is elided (along with all preceding optional fields) by
    /// truncating the source list. Producers build this with
    /// <see cref="ServiceBus.ServiceBusSessionFilter.Encode(string?)"/>;
    /// the broker echoes the assigned filter back on the attach response
    /// so receivers can discover which session was bound when they asked
    /// for "any".
    /// </summary>
    public ReadOnlyMemory<byte> Filter { get; init; }

    public static void Write(Span<byte> destination, in AmqpSource value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        // Worst case: address (field 0) + 6 elided null markers (fields
        // 1..6 since the next field we model is filter at index 7 inside
        // an 8-field projection — i.e. distribution-mode at 6, filter at
        // 7). Allocate enough offsets to address every potentially-set
        // field; only the populated prefix is passed to WritePerformative.
        Span<int> offsets = stackalloc int[9];
        int o = 0;

        offsets[0] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.Address, out var len);
        o += len;

        if (value.Filter.IsEmpty)
        {
            offsets[1] = o;
            written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets[..2], 1);
            return;
        }

        // Fields 1..6 are absent → encode as null markers so the list
        // count stays valid and the filter lands at its specified index.
        for (int i = 1; i <= 6; i++)
        {
            offsets[i] = o;
            PerformativeCodec.WriteNullField(scratch[o..], out var nullLen);
            o += nullLen;
        }

        offsets[7] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Filter.Span, out var fLen);
        o += fLen;
        offsets[8] = o;

        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 8);
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
        // Skip fields 1..6 (durable, expiry-policy, timeout, dynamic,
        // dynamic-node-properties, distribution-mode) — measure-and-skip
        // any non-null encoding so we land cleanly on the filter slot.
        for (int i = 1; i <= 6 && i < view.Count; i++)
        {
            if (PerformativeCodec.TryConsumeNull(els, ref o)) continue;
            var skipLen = AmqpValueScanner.Measure(els[o..]);
            o += skipLen;
        }
        var filter = ReadOnlyMemory<byte>.Empty;
        if (view.Count >= 8 && o < els.Length && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            var fLen = AmqpValueScanner.Measure(els[o..]);
            // Copy the filter bytes into a fresh array — AmqpCompoundView
            // exposes elements as a span (no offset back into `source`),
            // and the filter map is tiny (a single SB session-filter map
            // is well under 64 bytes), so the allocation is negligible.
            var copy = new byte[fLen];
            els.Slice(o, fLen).CopyTo(copy);
            filter = copy;
            o += fLen;
        }
        value = new AmqpSource { Address = address, Filter = filter };
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
