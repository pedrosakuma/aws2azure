using System.Buffers;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Amqp.Connection;

/// <summary>
/// Outgoing or incoming AMQP 1.0 bare message (§3.2). Slice 5c carries
/// the three sections CBS needs: <see cref="Properties"/>,
/// <see cref="ApplicationProperties"/>, <see cref="Body"/>. Headers,
/// annotations, footer, etc. are out of scope until a real workload
/// demands them.
/// </summary>
internal sealed class AmqpMessage
{
    public AmqpProperties Properties { get; set; }
    public Dictionary<string, object?>? ApplicationProperties { get; set; }
    public ReadOnlyMemory<byte> Body { get; set; }

    /// <summary>
    /// Serialises the bare message into <paramref name="destination"/>.
    /// Sections are emitted in spec-defined order: properties (if any
    /// field present), application-properties (if any entry present),
    /// data (always — empty body produces an empty data section).
    /// </summary>
    public void Write(Span<byte> destination, out int written)
    {
        int w = 0;
        var hasProps = Properties.MessageId is not null
                    || Properties.ReplyTo is not null
                    || Properties.CorrelationId is not null;
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
        AmqpDataSection.Write(destination[w..], Body.Span, out var dl);
        w += dl;
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
        var rented = ArrayPool<byte>.Shared.Rent(Performatives.ScratchSize);
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
