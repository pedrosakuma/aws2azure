using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Modules.Kinesis.ShardIterators;

internal enum HmacTokenDecodeError
{
    None = 0,
    MalformedFormat,
    BadSignature,
    MalformedPayload,
}

internal sealed class HmacTokenCodec
{
    private const string Version = "v1";
    private const int SignatureBytes = 32;
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _prefix;
    private readonly byte[] _signingKey;

    public HmacTokenCodec(string prefix, ReadOnlySpan<byte> signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (signingKey.IsEmpty)
        {
            throw new ArgumentException("Signing key must not be empty.", nameof(signingKey));
        }

        _prefix = prefix;
        _signingKey = signingKey.ToArray();
    }

    public string Encode(ReadOnlySpan<string> fields)
    {
        var payload = BuildPayload(fields);
        var payloadBytes = Utf8Strict.GetBytes(payload);
        return _prefix + Base64UrlEncode(payloadBytes) + "." + ComputeSignatureBase64(payloadBytes);
    }

    public bool TryDecode(
        string encoded,
        int fieldCount,
        out string[] fields,
        out HmacTokenDecodeError error)
    {
        fields = [];
        error = HmacTokenDecodeError.None;

        if (fieldCount < 0
            || string.IsNullOrEmpty(encoded)
            || !encoded.StartsWith(_prefix, StringComparison.Ordinal)
            || !TrySplitWire(encoded.AsSpan(_prefix.Length), out var payloadPart, out var signaturePart)
            || !TryBase64UrlDecode(payloadPart, out var payloadBytes)
            || !TryBase64UrlDecode(signaturePart, out var providedSignature))
        {
            error = HmacTokenDecodeError.MalformedFormat;
            return false;
        }

        Span<byte> expectedSignature = stackalloc byte[SignatureBytes];
        HMACSHA256.HashData(_signingKey, payloadBytes, expectedSignature);
        if (providedSignature.Length != SignatureBytes
            || !CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            error = HmacTokenDecodeError.BadSignature;
            return false;
        }

        string payload;
        try
        {
            payload = Utf8Strict.GetString(payloadBytes);
        }
        catch (DecoderFallbackException)
        {
            error = HmacTokenDecodeError.MalformedPayload;
            return false;
        }

        if (!TrySplitPayloadFields(payload, fieldCount + 1, out var rawFields)
            || !string.Equals(rawFields[0], Version, StringComparison.Ordinal))
        {
            error = HmacTokenDecodeError.MalformedPayload;
            return false;
        }

        fields = new string[fieldCount];
        Array.Copy(rawFields, 1, fields, 0, fieldCount);
        return true;
    }

    private string BuildPayload(ReadOnlySpan<string> fields)
    {
        var builder = new StringBuilder(Version.Length + (fields.Length * 8));
        builder.Append(Version);
        for (var i = 0; i < fields.Length; i++)
        {
            builder.Append('|');
            AppendEscaped(builder, fields[i]);
        }

        return builder.ToString();
    }

    private string ComputeSignatureBase64(ReadOnlySpan<byte> payloadBytes)
    {
        Span<byte> signature = stackalloc byte[SignatureBytes];
        HMACSHA256.HashData(_signingKey, payloadBytes, signature);
        return Base64UrlEncode(signature);
    }

    private static bool TrySplitWire(
        ReadOnlySpan<char> encoded,
        out string payloadPart,
        out string signaturePart)
    {
        payloadPart = string.Empty;
        signaturePart = string.Empty;

        var separatorIndex = encoded.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == encoded.Length - 1)
        {
            return false;
        }

        if (encoded[(separatorIndex + 1)..].IndexOf('.') >= 0)
        {
            return false;
        }

        payloadPart = encoded[..separatorIndex].ToString();
        signaturePart = encoded[(separatorIndex + 1)..].ToString();
        return true;
    }

    private static bool TrySplitPayloadFields(string payload, int expectedFieldCount, out string[] fields)
    {
        fields = new string[expectedFieldCount];
        var current = new StringBuilder(payload.Length);
        var fieldIndex = 0;
        var escaping = false;

        foreach (var c in payload)
        {
            if (escaping)
            {
                current.Append(c);
                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (c == '|')
            {
                if (fieldIndex >= fields.Length - 1)
                {
                    return false;
                }

                fields[fieldIndex++] = current.ToString();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (escaping || fieldIndex != fields.Length - 1)
        {
            return false;
        }

        fields[fieldIndex] = current.ToString();
        return true;
    }

    private static void AppendEscaped(StringBuilder builder, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var c in value)
        {
            if (c is '|' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data)
    {
        var base64 = Convert.ToBase64String(data);
        var trimmed = base64.TrimEnd('=');
        return trimmed.Replace('+', '-').Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string encoded, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var builder = new StringBuilder(encoded.Length + 3);
        foreach (var c in encoded)
        {
            builder.Append(c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c,
            });
        }

        switch (builder.Length % 4)
        {
            case 2:
                builder.Append("==");
                break;
            case 3:
                builder.Append('=');
                break;
            case 1:
                return false;
        }

        try
        {
            data = Convert.FromBase64String(builder.ToString());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
