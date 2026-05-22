using System.Buffers;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Outgoing or incoming AMQP 1.0 bare message (§3.2). Slice 5c carries
/// the three sections CBS needs: <see cref="Properties"/>,
/// <see cref="ApplicationProperties"/>, <see cref="Body"/>. Slice 8b.1
/// extends the inbound path to also parse <see cref="Header"/> (§3.2.1,
/// for <c>delivery-count</c>) and <see cref="MessageAnnotations"/>
/// (§3.2.3, for the Service Bus <c>x-opt-*</c> metadata). These two
/// extra sections are read-only — the proxy never authors them.
/// Other inbound sections (delivery-annotations, footer) are still
/// skipped opaquely.
/// </summary>
internal sealed class AmqpMessage
{
    public AmqpProperties Properties { get; set; }
    public Dictionary<string, object?>? ApplicationProperties { get; set; }
    public ReadOnlyMemory<byte> Body { get; set; }

    /// <summary>
    /// Parsed <c>header</c> section (§3.2.1) if present on the wire.
    /// Read-only — the proxy never authors a header section.
    /// </summary>
    public AmqpHeader? Header { get; set; }

    /// <summary>
    /// Parsed <c>message-annotations</c> section (§3.2.3) if present on
    /// the wire. Read-only — the proxy never authors annotations.
    /// </summary>
    public AmqpMessageAnnotations? MessageAnnotations { get; set; }

    /// <summary>
    /// Optional amqp-value (§3.2, descriptor 0x77) string body. When set
    /// it takes precedence over <see cref="Body"/> and the message is
    /// serialised with an amqp-value section instead of a data section.
    /// Used by Service Bus CBS, which mandates SAS tokens be carried as
    /// amqp-value strings rather than binary data sections.
    /// </summary>
    public string? BodyValueString { get; set; }

    /// <summary>
    /// Optional pre-encoded amqp-value body content (any AMQP value type
    /// — typically a map or list). When non-null and
    /// <see cref="BodyValueString"/> is null, the message is serialised
    /// with an amqp-value section carrying these bytes verbatim.
    /// On parse, non-string amqp-value bodies are surfaced here (e.g.
    /// Service Bus <c>$management</c> response bodies which are maps).
    /// Mutually exclusive with <see cref="BodyValueString"/>: setting
    /// both is a programmer error.
    /// </summary>
    public ReadOnlyMemory<byte>? BodyValueBytes { get; set; }

    /// <summary>
    /// Serialises the bare message into <paramref name="destination"/>.
    /// Sections are emitted in spec-defined order: properties (if any
    /// field present), application-properties (if any entry present),
    /// body. The body is either an amqp-value string (when
    /// <see cref="BodyValueString"/> is non-null) or a data section
    /// carrying <see cref="Body"/> (always — empty body produces an
    /// empty data section).
    /// </summary>
    public void Write(Span<byte> destination, out int written)
    {
        int w = 0;
        if (MessageAnnotations is { } annotations && !annotations.IsEmptyForWrite)
        {
            annotations.Write(destination[w..], out var ml);
            w += ml;
        }
        var hasProps = Properties.MessageId is not null
                    || Properties.ReplyTo is not null
                    || Properties.CorrelationId is not null
                    || Properties.GroupId is not null
                    || Properties.GroupSequence is not null;
        if (hasProps)
        {
            var props = Properties;
            AmqpProperties.Write(destination[w..], in props, out var pl);
            w += pl;
        }
        if (ApplicationProperties is { Count: > 0 } ap)
        {
            AmqpApplicationProperties.Write(destination[w..], ap, out var al);
            w += al;
        }
        if (BodyValueString is { } value)
        {
            destination[w++] = Aws2Azure.Amqp.Codec.AmqpFormatCode.Described;
            Aws2Azure.Amqp.Codec.AmqpPrimitiveWriter.WriteULong(
                destination[w..], MessageSectionDescriptor.AmqpValue, out var dl);
            w += dl;
            Aws2Azure.Amqp.Codec.AmqpVariableWriter.WriteString(destination[w..], value, out var sl);
            w += sl;
        }
        else if (BodyValueBytes is { } bytes)
        {
            destination[w++] = Aws2Azure.Amqp.Codec.AmqpFormatCode.Described;
            Aws2Azure.Amqp.Codec.AmqpPrimitiveWriter.WriteULong(
                destination[w..], MessageSectionDescriptor.AmqpValue, out var dl);
            w += dl;
            bytes.Span.CopyTo(destination[w..]);
            w += bytes.Length;
        }
        else
        {
            AmqpDataSection.Write(destination[w..], Body.Span, out var dl);
            w += dl;
        }
        written = w;
    }

    /// <summary>
    /// Parses the bare message body produced by <see cref="Write"/> (or
    /// any spec-compliant peer). Unknown sections are skipped.
    /// </summary>
    public static AmqpMessage Parse(ReadOnlyMemory<byte> source)
    {
        var message = new AmqpMessage();
        var remaining = source;
        while (!remaining.IsEmpty)
        {
            // Peek the descriptor of the next described section.
            var span = remaining.Span;
            if (span[0] != Aws2Azure.Amqp.Codec.AmqpFormatCode.Described)
                throw new InvalidDataException(
                    $"Expected described-type constructor for message section, got 0x{span[0]:X2}.");
            int o = 1;
            var descriptor = Aws2Azure.Amqp.Codec.AmqpPrimitiveReader.ReadULong(span[o..], out var descLen);

            int consumed;
            switch (descriptor)
            {
                case MessageSectionDescriptor.Header:
                    AmqpHeader.Read(remaining, out var header, out consumed);
                    message.Header = header;
                    break;
                case MessageSectionDescriptor.MessageAnnotations:
                    message.MessageAnnotations = AmqpMessageAnnotations.Read(remaining, out consumed);
                    break;
                case MessageSectionDescriptor.Properties:
                    AmqpProperties.Read(remaining, out var props, out consumed);
                    message.Properties = props;
                    break;
                case MessageSectionDescriptor.ApplicationProperties:
                    message.ApplicationProperties = AmqpApplicationProperties.Read(remaining, out consumed);
                    break;
                case MessageSectionDescriptor.Data:
                    message.Body = AmqpDataSection.Read(remaining, out consumed);
                    break;
                case MessageSectionDescriptor.AmqpValue:
                    // Skip the described constructor header (0x00 + ULong descriptor),
                    // then read the value verbatim. We model two variants:
                    //   - string AmqpValue → BodyValueString (CBS responses, status-text)
                    //   - any other type    → BodyValueBytes (Service Bus $management
                    //     responses are maps; callers decode themselves).
                    var valueOffset = 1 + descLen;
                    var valueSpan = span[valueOffset..];
                    if (valueSpan.Length > 0 &&
                        (valueSpan[0] == Aws2Azure.Amqp.Codec.AmqpFormatCode.String8Utf8
                         || valueSpan[0] == Aws2Azure.Amqp.Codec.AmqpFormatCode.String32Utf8))
                    {
                        message.BodyValueString = Aws2Azure.Amqp.Codec.AmqpVariableReader.ReadString(
                            valueSpan, out var sLen);
                        consumed = valueOffset + sLen;
                    }
                    else
                    {
                        var valueLen = Aws2Azure.Amqp.Codec.AmqpValueScanner.Measure(valueSpan);
                        message.BodyValueBytes = remaining.Slice(valueOffset, valueLen);
                        consumed = valueOffset + valueLen;
                    }
                    break;
                default:
                    // Skip unknown described section: descriptor + value (described type measure).
                    consumed = Aws2Azure.Amqp.Codec.AmqpValueScanner.Measure(span);
                    break;
            }
            remaining = remaining[consumed..];
        }
        return message;
    }

    /// <summary>
    /// Encodes the message into a pooled buffer and returns ownership.
    /// Caller must dispose to release the buffer.
    /// </summary>
    public PooledPayload EncodePooled()
    {
        // Body payload may exceed the perf scratch size for large messages.
        // Size for: scratch overhead + body bytes + a safety margin for
        // properties/application-properties/header encodings.
        var bodyLen = Body.Length + (BodyValueBytes?.Length ?? 0);
        var rentedSize = Performatives.ScratchSize + bodyLen + 256;
        var rented = ArrayPool<byte>.Shared.Rent(rentedSize);
        try
        {
            Write(rented, out var written);
            return new PooledPayload(rented, written);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }
}

/// <summary>Pool-owned byte buffer with an explicit length cursor.</summary>
internal readonly struct PooledPayload : IDisposable
{
    private readonly byte[] _buffer;

    internal PooledPayload(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }
    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);
    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}
