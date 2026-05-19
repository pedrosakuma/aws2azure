using Aws2Azure.Amqp.Codec;

namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// Shared helpers for encoding and decoding AMQP 1.0 transport
/// performatives. Every performative on the wire is a described type
/// whose descriptor is a <c>ulong</c> code (see
/// <see cref="PerformativeDescriptor"/>) and whose value is a list
/// holding the performative's fields in positional order. Per spec
/// §1.3.5, trailing fields whose value equals their default may be
/// elided from the list; this helper performs the trim on write and
/// surfaces missing trailing fields as defaults on read.
/// </summary>
internal static class PerformativeCodec
{
    /// <summary>Maximum number of fields any defined performative carries (attach, 14).</summary>
    public const int MaxFields = 16;

    /// <summary>
    /// Reads the described-type envelope, validates the descriptor matches
    /// <paramref name="expectedDescriptor"/>, and returns a view over the
    /// field list. <paramref name="elementsOffset"/> is the absolute
    /// offset within <paramref name="source"/> where the field elements
    /// begin — performative readers add it to per-field offsets when
    /// slicing opaque fields out of the backing
    /// <see cref="ReadOnlyMemory{T}"/>. <paramref name="bodyConsumed"/>
    /// is the total number of bytes consumed from
    /// <paramref name="source"/>.
    /// </summary>
    public static AmqpCompoundView ReadPerformativeFields(
        ReadOnlySpan<byte> source,
        ulong expectedDescriptor,
        out int elementsOffset,
        out int bodyConsumed)
    {
        var offset = AmqpCompoundReader.ReadDescribedHeader(source);
        var descriptor = AmqpPrimitiveReader.ReadULong(source[offset..], out var descLen);
        offset += descLen;
        if (descriptor != expectedDescriptor)
        {
            throw new InvalidDataException(
                $"Expected performative descriptor 0x{expectedDescriptor:X16}, got 0x{descriptor:X16}.");
        }
        // Compute the absolute element offset by inspecting the list
        // constructor: list0 has no elements; list8 puts elements at +3
        // (constructor + size byte + count byte); list32 at +9.
        var listConstructor = source[offset];
        var listElementsOffset = listConstructor switch
        {
            AmqpFormatCode.List0 => 0,
            AmqpFormatCode.List8 => 3,
            AmqpFormatCode.List32 => 9,
            _ => throw new InvalidDataException(
                $"Expected list constructor for performative fields, got 0x{listConstructor:X2}."),
        };
        elementsOffset = listConstructor == AmqpFormatCode.List0 ? 0 : offset + listElementsOffset;
        var view = AmqpCompoundReader.ReadList(source[offset..], out var listLen);
        bodyConsumed = offset + listLen;
        return view;
    }

    /// <summary>
    /// Peeks the descriptor of an AMQP performative without consuming
    /// the field list. Useful for dispatch.
    /// </summary>
    public static PerformativeKind PeekKind(ReadOnlySpan<byte> source, out ulong descriptor)
    {
        var offset = AmqpCompoundReader.ReadDescribedHeader(source);
        descriptor = AmqpPrimitiveReader.ReadULong(source[offset..], out _);
        return descriptor switch
        {
            PerformativeDescriptor.Open => PerformativeKind.Open,
            PerformativeDescriptor.Begin => PerformativeKind.Begin,
            PerformativeDescriptor.Attach => PerformativeKind.Attach,
            PerformativeDescriptor.Flow => PerformativeKind.Flow,
            PerformativeDescriptor.Transfer => PerformativeKind.Transfer,
            PerformativeDescriptor.Disposition => PerformativeKind.Disposition,
            PerformativeDescriptor.Detach => PerformativeKind.Detach,
            PerformativeDescriptor.End => PerformativeKind.End,
            PerformativeDescriptor.Close => PerformativeKind.Close,
            PerformativeDescriptor.SaslMechanisms => PerformativeKind.SaslMechanisms,
            PerformativeDescriptor.SaslInit => PerformativeKind.SaslInit,
            PerformativeDescriptor.SaslChallenge => PerformativeKind.SaslChallenge,
            PerformativeDescriptor.SaslResponse => PerformativeKind.SaslResponse,
            PerformativeDescriptor.SaslOutcome => PerformativeKind.SaslOutcome,
            _ => PerformativeKind.Unknown,
        };
    }

    /// <summary>
    /// Wraps an encoded field-list body in a described type with the given
    /// performative <paramref name="descriptor"/>, eliding trailing fields
    /// whose encoding equals the AMQP <c>null</c> marker (0x40).
    /// <paramref name="fields"/> holds the concatenated AMQP-encoded
    /// field values; <paramref name="fieldOffsets"/> holds
    /// <c>fieldCount + 1</c> offsets such that field <c>i</c> spans
    /// <c>fields[offsets[i]..offsets[i+1]]</c>.
    /// </summary>
    public static int WritePerformative(
        Span<byte> destination,
        ulong descriptor,
        ReadOnlySpan<byte> fields,
        ReadOnlySpan<int> fieldOffsets,
        int fieldCount)
    {
        if (fieldOffsets.Length < fieldCount + 1)
        {
            throw new ArgumentException(
                "fieldOffsets must contain fieldCount + 1 entries.",
                nameof(fieldOffsets));
        }

        // Trim trailing null fields: a field is "null on the wire" iff its
        // encoding is exactly one byte equal to 0x40.
        var lastNonNull = -1;
        for (var i = fieldCount - 1; i >= 0; i--)
        {
            var start = fieldOffsets[i];
            var len = fieldOffsets[i + 1] - start;
            if (!(len == 1 && fields[start] == AmqpFormatCode.Null))
            {
                lastNonNull = i;
                break;
            }
        }
        var trimmedCount = lastNonNull + 1;
        var trimmedLen = trimmedCount == 0 ? 0 : fieldOffsets[trimmedCount];
        var trimmed = fields[..trimmedLen];

        var written = 0;
        destination[written++] = AmqpFormatCode.Described;
        AmqpPrimitiveWriter.WriteULong(destination[written..], descriptor, out var descLen);
        written += descLen;
        AmqpCompoundWriter.WriteList(destination[written..], trimmed, trimmedCount, out var listLen);
        written += listLen;
        return written;
    }

    // --- field-encoding helpers ----------------------------------------

    /// <summary>Writes a single AMQP <c>null</c> marker (0x40); used for elided/absent fields.</summary>
    public static void WriteNullField(Span<byte> destination, out int written)
    {
        destination[0] = AmqpFormatCode.Null;
        written = 1;
    }

    /// <summary>
    /// Writes <paramref name="value"/> verbatim if non-empty (the caller
    /// guarantees it is already a valid AMQP-encoded value), or a null
    /// marker if empty. Used for opaque field types (source, target,
    /// error, delivery-state, properties maps, capability arrays) that
    /// this slice does not yet model with typed structs.
    /// </summary>
    public static void WriteOpaqueOrNull(
        Span<byte> destination,
        ReadOnlySpan<byte> value,
        out int written)
    {
        if (value.IsEmpty)
        {
            WriteNullField(destination, out written);
            return;
        }
        value.CopyTo(destination);
        written = value.Length;
    }

    /// <summary>
    /// Writes <paramref name="value"/> as an AMQP string, or null if
    /// <paramref name="value"/> is <c>null</c>.
    /// </summary>
    public static void WriteStringOrNull(
        Span<byte> destination,
        string? value,
        out int written)
    {
        if (value is null)
        {
            WriteNullField(destination, out written);
            return;
        }
        AmqpVariableWriter.WriteString(destination, value, out written);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as an AMQP symbol, or null if
    /// <paramref name="value"/> is <c>null</c>.
    /// </summary>
    public static void WriteSymbolOrNull(
        Span<byte> destination,
        string? value,
        out int written)
    {
        if (value is null)
        {
            WriteNullField(destination, out written);
            return;
        }
        AmqpVariableWriter.WriteSymbol(destination, value, out written);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as an AMQP binary, or null if
    /// <paramref name="value"/> is empty (callers that need to
    /// distinguish empty-binary from absent should use a different
    /// helper; for transport performatives — delivery-tag — both are
    /// modeled as "absent").
    /// </summary>
    public static void WriteBinaryOrNull(
        Span<byte> destination,
        ReadOnlySpan<byte> value,
        bool isPresent,
        out int written)
    {
        if (!isPresent)
        {
            WriteNullField(destination, out written);
            return;
        }
        AmqpVariableWriter.WriteBinary(destination, value, out written);
    }

    // --- field-decoding helpers ----------------------------------------

    /// <summary>
    /// Returns <c>true</c> and advances <paramref name="offset"/> by one
    /// byte if the next byte in <paramref name="elements"/> is the AMQP
    /// null marker. Used by performative readers to detect absent
    /// nullable fields without invoking type-specific decoders.
    /// </summary>
    public static bool TryConsumeNull(ReadOnlySpan<byte> elements, ref int offset)
    {
        if (offset < elements.Length && elements[offset] == AmqpFormatCode.Null)
        {
            offset++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Captures the next field as an opaque <see cref="ReadOnlyMemory{T}"/>
    /// slice of <paramref name="source"/>: empty when the field is null
    /// or has been elided, otherwise the verbatim AMQP-encoded bytes.
    /// </summary>
    public static ReadOnlyMemory<byte> ReadOpaqueField(
        ReadOnlyMemory<byte> source,
        ReadOnlySpan<byte> elements,
        int elementsOffset,
        ref int offset)
    {
        if (offset >= elements.Length || TryConsumeNull(elements, ref offset))
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        var len = AmqpValueScanner.Measure(elements[offset..]);
        var memory = source.Slice(elementsOffset + offset, len);
        offset += len;
        return memory;
    }
}
