using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// AMQP 1.0 messaging section descriptors (§3.2). A bare message is the
/// concatenation of described sections in spec-defined order: header,
/// delivery-annotations, message-annotations, properties,
/// application-properties, body (one of data | amqp-sequence |
/// amqp-value), footer. Slice 5c only models the three sections CBS
/// needs (properties, application-properties, data); the rest are
/// passed through opaquely.
/// </summary>
internal static class MessageSectionDescriptor
{
    public const ulong Header = 0x0000_0000_0000_0070UL;
    public const ulong DeliveryAnnotations = 0x0000_0000_0000_0071UL;
    public const ulong MessageAnnotations = 0x0000_0000_0000_0072UL;
    public const ulong Properties = 0x0000_0000_0000_0073UL;
    public const ulong ApplicationProperties = 0x0000_0000_0000_0074UL;
    public const ulong Data = 0x0000_0000_0000_0075UL;
    public const ulong AmqpSequence = 0x0000_0000_0000_0076UL;
    public const ulong AmqpValue = 0x0000_0000_0000_0077UL;
    public const ulong Footer = 0x0000_0000_0000_0078UL;
}

/// <summary>
/// AMQP 1.0 <c>properties</c> message section (§3.2.4, descriptor 0x73).
/// Slice 5c models only the three fields CBS uses: <see cref="MessageId"/>,
/// <see cref="ReplyTo"/>, <see cref="CorrelationId"/>. Per spec each of
/// message-id / correlation-id may be one of (ulong | uuid | binary |
/// string); this profile reads/writes the string variant only. Other
/// variants are surfaced as <c>null</c> on read so a peer-generated
/// non-string id won't crash decoding.
/// </summary>
internal readonly record struct AmqpProperties
{
    public const ulong Descriptor = MessageSectionDescriptor.Properties;

    public string? MessageId { get; init; }
    public string? ReplyTo { get; init; }
    public string? CorrelationId { get; init; }

    public static void Write(Span<byte> destination, in AmqpProperties value, out int written)
    {
        Span<byte> scratch = stackalloc byte[Performatives.ScratchSize];
        Span<int> offsets = stackalloc int[14];
        int o = 0;
        int len;

        // 0 message-id
        offsets[0] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.MessageId, out len); o += len;
        // 1 user-id
        offsets[1] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 2 to
        offsets[2] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 3 subject
        offsets[3] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 4 reply-to
        offsets[4] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.ReplyTo, out len); o += len;
        // 5 correlation-id
        offsets[5] = o;
        PerformativeCodec.WriteStringOrNull(scratch[o..], value.CorrelationId, out len); o += len;
        // 6 content-type
        offsets[6] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 7 content-encoding
        offsets[7] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 8 absolute-expiry-time
        offsets[8] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 9 creation-time
        offsets[9] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 10 group-id
        offsets[10] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 11 group-sequence
        offsets[11] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;
        // 12 reply-to-group-id
        offsets[12] = o; PerformativeCodec.WriteNullField(scratch[o..], out len); o += len;

        offsets[13] = o;
        written = PerformativeCodec.WritePerformative(destination, Descriptor, scratch[..o], offsets, 13);
    }

    public static void Read(ReadOnlyMemory<byte> source, out AmqpProperties value, out int consumed)
    {
        var span = source.Span;
        var view = PerformativeCodec.ReadPerformativeFields(span, Descriptor, out _, out consumed);
        var els = view.Elements;
        int o = 0;
        int len;

        string? messageId = null;
        if (view.Count >= 1) messageId = ReadStringOrSkip(els, ref o, out len);
        if (view.Count >= 2) SkipField(els, ref o);                                  // user-id
        if (view.Count >= 3) SkipField(els, ref o);                                  // to
        if (view.Count >= 4) SkipField(els, ref o);                                  // subject

        string? replyTo = null;
        if (view.Count >= 5) replyTo = ReadStringOrSkip(els, ref o, out len);

        string? correlationId = null;
        if (view.Count >= 6) correlationId = ReadStringOrSkip(els, ref o, out len);

        // remaining fields are ignored

        value = new AmqpProperties
        {
            MessageId = messageId,
            ReplyTo = replyTo,
            CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// Reads the next field: if it is a string, returns the value; if it
    /// is null or anything else, skips it and returns null.
    /// </summary>
    private static string? ReadStringOrSkip(ReadOnlySpan<byte> els, ref int o, out int len)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) { len = 0; return null; }
        var fc = els[o];
        if (fc == AmqpFormatCode.String8Utf8 || fc == AmqpFormatCode.String32Utf8)
        {
            var s = AmqpVariableReader.ReadString(els[o..], out len);
            o += len;
            return s;
        }
        // unknown variant — measure and skip
        len = AmqpValueScanner.Measure(els[o..]);
        o += len;
        return null;
    }

    private static void SkipField(ReadOnlySpan<byte> els, ref int o)
    {
        if (PerformativeCodec.TryConsumeNull(els, ref o)) return;
        var len = AmqpValueScanner.Measure(els[o..]);
        o += len;
    }
}
