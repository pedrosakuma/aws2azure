using System.Buffers.Binary;
using System.Text;

namespace Aws2Azure.Amqp.Codec;

/// <summary>
/// Span-based decoders for AMQP 1.0 variable-width primitives
/// (binary, string-utf8, symbol-ascii). Each reader returns the decoded
/// payload and yields the number of bytes consumed via <c>consumed</c>.
/// Symbols are returned as <see cref="string"/> (ASCII) for ergonomics;
/// binary is exposed as a <see cref="ReadOnlySpan{T}"/> over the original
/// buffer to keep the codec allocation-free at this layer.
/// </summary>
internal static class AmqpVariableReader
{
    /// <summary>
    /// Strict UTF-8 decoder: invalid byte sequences throw
    /// <see cref="DecoderFallbackException"/> instead of being replaced
    /// with U+FFFD. We use this on every string decode so codec
    /// corruption surfaces at the protocol boundary rather than silently
    /// producing garbled performatives.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    // --- binary ----------------------------------------------------------

    public static ReadOnlySpan<byte> ReadBinary(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        int length, headerLen;
        switch (code)
        {
            case AmqpFormatCode.Binary8:
                length = source[1];
                headerLen = 2;
                break;
            case AmqpFormatCode.Binary32:
                length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[1..]));
                headerLen = 5;
                break;
            default:
                throw UnexpectedCode("binary", code);
        }
        consumed = headerLen + length;
        if (consumed > source.Length)
        {
            throw new InvalidDataException("Truncated AMQP binary payload.");
        }
        return source.Slice(headerLen, length);
    }

    // --- string ----------------------------------------------------------

    public static string ReadString(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        int length, headerLen;
        switch (code)
        {
            case AmqpFormatCode.String8Utf8:
                length = source[1];
                headerLen = 2;
                break;
            case AmqpFormatCode.String32Utf8:
                length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[1..]));
                headerLen = 5;
                break;
            default:
                throw UnexpectedCode("string", code);
        }
        consumed = headerLen + length;
        if (consumed > source.Length)
        {
            throw new InvalidDataException("Truncated AMQP string payload.");
        }
        // Strict decoder throws DecoderFallbackException on invalid
        // UTF-8 — we surface that to the caller; the codec layer does
        // not silently produce U+FFFD replacement chars for
        // performative fields.
        return StrictUtf8.GetString(source.Slice(headerLen, length));
    }

    // --- symbol ----------------------------------------------------------

    public static string ReadSymbol(ReadOnlySpan<byte> source, out int consumed)
    {
        var code = source[0];
        int length, headerLen;
        switch (code)
        {
            case AmqpFormatCode.Symbol8:
                length = source[1];
                headerLen = 2;
                break;
            case AmqpFormatCode.Symbol32:
                length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source[1..]));
                headerLen = 5;
                break;
            default:
                throw UnexpectedCode("symbol", code);
        }
        consumed = headerLen + length;
        if (consumed > source.Length)
        {
            throw new InvalidDataException("Truncated AMQP symbol payload.");
        }
        var payload = source.Slice(headerLen, length);
        // Validate ASCII: a non-ASCII byte indicates either codec corruption
        // or a peer that misinterprets symbol's wire format. Either way it
        // is an interop bug we should not paper over.
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] > 0x7F)
            {
                throw new InvalidDataException("AMQP symbol contains non-ASCII byte.");
            }
        }
        return Encoding.ASCII.GetString(payload);
    }

    private static InvalidDataException UnexpectedCode(string what, byte actual)
    {
        return new InvalidDataException($"Expected {what}, got AMQP format code 0x{actual:X2}.");
    }
}
