using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 <c>error</c> composite type ("AMQP 1.0 Transport" §2.8.14),
/// descriptor 0x1D. Carries diagnostics for a connection / session /
/// link failure as a <c>condition</c> symbol (REQUIRED, see
/// <see cref="AmqpErrorCondition"/>), an optional human-readable
/// <c>description</c> and an optional <c>info</c> map (kept opaque
/// here — typed map decoders are out of scope for this slice).
/// </summary>
internal readonly record struct AmqpError
{
    public const ulong Descriptor = 0x0000_0000_0000_001DUL;

    /// <summary>REQUIRED. One of the symbols in <see cref="AmqpErrorCondition"/>.</summary>
    public string Condition { get; init; }
    public string? Description { get; init; }
    /// <summary>Opaque AMQP-encoded <c>fields</c> map; empty &#8801; absent.</summary>
    public ReadOnlyMemory<byte> Info { get; init; }

    /// <summary>
    /// Convenience over <see cref="AmqpErrorClassifier.Classify"/>.
    /// </summary>
    public AmqpErrorKind Kind => AmqpErrorClassifier.Classify(Condition);

    public static void Write(Span<byte> destination, in AmqpError value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[4];
        int o = 0;
        int len;

        offsets[0] = o;
        AmqpVariableWriter.WriteSymbol(scratch[o..], value.Condition ?? string.Empty, out len); o += len;

        offsets[1] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.Description, out len); o += len;

        offsets[2] = o;
        PerformativeCodec.WriteOpaqueOrNull(scratch[o..], value.Info.Span, out len); o += len;

        offsets[3] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 3);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpError value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(
            span, Descriptor, out var elementsOffset, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        if (view.Count < 1) throw new InvalidDataException("error.condition is required.");
        var condition = AmqpVariableReader.ReadSymbol(els[o..], out len); o += len;

        string? description = null;
        if (view.Count >= 2 && !PerformativeCodec.TryConsumeNull(els, ref o))
        {
            description = AmqpVariableReader.ReadString(els[o..], out len); o += len;
        }
        var info = view.Count >= 3
            ? PerformativeCodec.ReadOpaqueField(source, els, elementsOffset, ref o)
            : ReadOnlyMemory<byte>.Empty;

        value = new AmqpError { Condition = condition, Description = description, Info = info };
    }
}
