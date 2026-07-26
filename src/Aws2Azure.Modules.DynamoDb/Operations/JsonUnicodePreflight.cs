using System;
using System.Text;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static class JsonUnicodePreflight
{
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
    };

    public static bool TryValidate(
        ReadOnlySpan<byte> body,
        out string error)
    {
        try
        {
            var reader = new Utf8JsonReader(
                body,
                isFinalBlock: true,
                new JsonReaderState(ReaderOptions));
            while (reader.Read())
            {
                if (reader.TokenType is not (
                        JsonTokenType.PropertyName or JsonTokenType.String))
                {
                    continue;
                }

                if (!HasValidUtf8(reader.ValueSpan)
                    || (reader.ValueIsEscaped
                        && !HasValidSurrogateEscapes(reader.ValueSpan)))
                {
                    error =
                        "Malformed JSON: JSON strings must contain valid Unicode scalar values.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            error = "Malformed JSON: " + exception.Message;
            return false;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            error =
                "Malformed JSON: JSON strings must contain valid Unicode scalar values.";
            return false;
        }
    }

    private static bool HasValidUtf8(ReadOnlySpan<byte> encoded)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(encoded);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasValidSurrogateEscapes(
        ReadOnlySpan<byte> encoded)
    {
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != (byte)'\\')
            {
                continue;
            }

            index++;
            if (index >= encoded.Length || encoded[index] != (byte)'u')
            {
                continue;
            }
            if (!TryReadHexCodeUnit(
                    encoded,
                    index + 1,
                    out var codeUnit))
            {
                return false;
            }
            index += 4;

            if (codeUnit is >= 0xDC00 and <= 0xDFFF)
            {
                return false;
            }
            if (codeUnit is not (>= 0xD800 and <= 0xDBFF))
            {
                continue;
            }

            if (index + 6 >= encoded.Length
                || encoded[index + 1] != (byte)'\\'
                || encoded[index + 2] != (byte)'u'
                || !TryReadHexCodeUnit(
                    encoded,
                    index + 3,
                    out var lowSurrogate)
                || lowSurrogate is not (>= 0xDC00 and <= 0xDFFF))
            {
                return false;
            }
            index += 6;
        }

        return true;
    }

    private static bool TryReadHexCodeUnit(
        ReadOnlySpan<byte> encoded,
        int start,
        out int codeUnit)
    {
        codeUnit = 0;
        if (start < 0 || start + 4 > encoded.Length)
        {
            return false;
        }

        for (var index = start; index < start + 4; index++)
        {
            var value = encoded[index] switch
            {
                >= (byte)'0' and <= (byte)'9' =>
                    encoded[index] - (byte)'0',
                >= (byte)'A' and <= (byte)'F' =>
                    encoded[index] - (byte)'A' + 10,
                >= (byte)'a' and <= (byte)'f' =>
                    encoded[index] - (byte)'a' + 10,
                _ => -1,
            };
            if (value < 0)
            {
                return false;
            }
            codeUnit = (codeUnit << 4) | value;
        }

        return true;
    }
}
