using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Modules.Kinesis.ShardIterators;

public sealed class ShardIteratorTokenCodec
{
    internal const string Prefix = "aws2az-it-";
    private const string Version = "v1";
    private const int MaxAgeSeconds = 300;
    private const int SignatureBytes = 32;
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _signingKey;
    private readonly TimeProvider _timeProvider;

    public ShardIteratorTokenCodec(ReadOnlySpan<byte> signingKey, TimeProvider? timeProvider = null)
    {
        if (signingKey.IsEmpty)
        {
            throw new ArgumentException("Signing key must not be empty.", nameof(signingKey));
        }

        _signingKey = signingKey.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Encode(ShardIteratorToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(token.Stream);
        ArgumentNullException.ThrowIfNull(token.Shard);

        if (!IsValidPosition(token.Type, token.Position))
        {
            throw new ArgumentException("Position is invalid for the shard iterator type.", nameof(token));
        }

        var payload = string.Concat(
            Version,
            "|",
            Escape(token.Stream),
            "|",
            Escape(token.Shard),
            "|",
            ((int)token.Type).ToString(CultureInfo.InvariantCulture),
            "|",
            token.Position is null ? string.Empty : Escape(token.Position),
            "|",
            token.IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture));

        var payloadBytes = Utf8Strict.GetBytes(payload);
        var signature = ComputeSignature(payloadBytes);
        return Prefix + Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    public bool TryDecode(string encoded, out ShardIteratorToken token, out ShardIteratorVerifyError error)
    {
        token = new ShardIteratorToken(string.Empty, string.Empty, ShardIteratorType.TrimHorizon, null, 0);
        error = ShardIteratorVerifyError.None;

        if (string.IsNullOrEmpty(encoded)
            || !encoded.StartsWith(Prefix, StringComparison.Ordinal)
            || !TrySplitWire(encoded.AsSpan(Prefix.Length), out var payloadPart, out var signaturePart)
            || !TryBase64UrlDecode(payloadPart, out var payloadBytes)
            || !TryBase64UrlDecode(signaturePart, out var providedSignature))
        {
            error = ShardIteratorVerifyError.MalformedFormat;
            return false;
        }

        var expectedSignature = ComputeSignature(payloadBytes);
        if (providedSignature.Length != SignatureBytes
            || !CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            error = ShardIteratorVerifyError.BadSignature;
            return false;
        }

        string payload;
        try
        {
            payload = Utf8Strict.GetString(payloadBytes);
        }
        catch (DecoderFallbackException)
        {
            error = ShardIteratorVerifyError.MalformedPayload;
            return false;
        }

        if (!TryParsePayload(payload, out token))
        {
            error = ShardIteratorVerifyError.MalformedPayload;
            return false;
        }

        var ageSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds() - token.IssuedAtUnixSeconds;
        if (ageSeconds > MaxAgeSeconds)
        {
            token = new ShardIteratorToken(string.Empty, string.Empty, ShardIteratorType.TrimHorizon, null, 0);
            error = ShardIteratorVerifyError.Expired;
            return false;
        }

        return true;
    }

    private byte[] ComputeSignature(ReadOnlySpan<byte> payloadBytes)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(payloadBytes.ToArray());
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

    private static bool TryParsePayload(string payload, out ShardIteratorToken token)
    {
        token = new ShardIteratorToken(string.Empty, string.Empty, ShardIteratorType.TrimHorizon, null, 0);

        if (!TrySplitPayloadFields(payload, out var fields)
            || !string.Equals(fields[0], Version, StringComparison.Ordinal)
            || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var typeValue)
            || !TryParseType(typeValue, out var type)
            || !long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var issuedAtUnixSeconds))
        {
            return false;
        }

        var position = fields[4].Length == 0 ? null : fields[4];
        if (!IsValidPosition(type, position))
        {
            return false;
        }

        token = new ShardIteratorToken(fields[1], fields[2], type, position, issuedAtUnixSeconds);
        return true;
    }

    private static bool TrySplitPayloadFields(string payload, out string[] fields)
    {
        fields = new string[6];
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

    private static bool TryParseType(int value, out ShardIteratorType type)
    {
        switch (value)
        {
            case (int)ShardIteratorType.TrimHorizon:
                type = ShardIteratorType.TrimHorizon;
                return true;
            case (int)ShardIteratorType.Latest:
                type = ShardIteratorType.Latest;
                return true;
            case (int)ShardIteratorType.AtSequenceNumber:
                type = ShardIteratorType.AtSequenceNumber;
                return true;
            case (int)ShardIteratorType.AfterSequenceNumber:
                type = ShardIteratorType.AfterSequenceNumber;
                return true;
            case (int)ShardIteratorType.AtTimestamp:
                type = ShardIteratorType.AtTimestamp;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static bool IsValidPosition(ShardIteratorType type, string? position)
    {
        return type switch
        {
            ShardIteratorType.TrimHorizon or ShardIteratorType.Latest => position is null,
            ShardIteratorType.AtSequenceNumber or ShardIteratorType.AfterSequenceNumber or ShardIteratorType.AtTimestamp
                => !string.IsNullOrEmpty(position),
            _ => false,
        };
    }

    private static string Escape(string value)
    {
        var needsEscape = false;
        foreach (var c in value)
        {
            if (c is '|' or '\\')
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (c is '|' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data)
    {
        var base64 = Convert.ToBase64String(data);
        var trimmed = base64.TrimEnd('=');
        return trimmed.Replace('+', '-').Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string encoded, out byte[] data)
    {
        data = Array.Empty<byte>();
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
