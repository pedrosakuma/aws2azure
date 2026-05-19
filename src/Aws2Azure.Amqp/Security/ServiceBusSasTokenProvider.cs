using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Amqp.Security;

/// <summary>
/// Generates Azure Service Bus SAS tokens (CBS-compatible) of the form
/// <c>SharedAccessSignature sr={uri}&amp;sig={sig}&amp;se={expiry}&amp;skn={keyName}</c>.
/// Signature is HMAC-SHA256 over <c>urlEncode(resourceUri) + "\n" + expiry</c>.
/// AOT-safe (no reflection, no <see cref="System.Web"/>); uses
/// <see cref="UrlEncoder"/> below for the small subset of URL encoding
/// that matters for these tokens.
/// </summary>
internal sealed class ServiceBusSasTokenProvider : IAmqpTokenProvider
{
    private readonly string _keyName;
    private readonly byte[] _key;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;

    public ServiceBusSasTokenProvider(
        string keyName,
        string keyValue,
        TimeSpan? ttl = null,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyValue);
        _keyName = keyName;
        _key = Encoding.UTF8.GetBytes(keyValue);
        _ttl = ttl ?? TimeSpan.FromMinutes(20);
        _clock = clock ?? TimeProvider.System;
    }

    public string TokenType => "servicebus.windows.net:sastoken";

    public AmqpToken GetToken(string audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        var expiry = _clock.GetUtcNow().Add(_ttl);
        var resource = NormalizeResource(audience);
        var encodedResource = UrlEncoder.Encode(resource);
        var expirySeconds = expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var stringToSign = encodedResource + "\n" + expirySeconds;
        Span<byte> sig = stackalloc byte[32];
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(stringToSign), sig);
        var sigB64 = Convert.ToBase64String(sig);

        var token = "SharedAccessSignature sr=" + encodedResource
            + "&sig=" + UrlEncoder.Encode(sigB64)
            + "&se=" + expirySeconds
            + "&skn=" + UrlEncoder.Encode(_keyName);
        return new AmqpToken(token, expiry);
    }

    private static string NormalizeResource(string audience)
    {
        // CBS uses the bare resource URI (typically scheme-less or
        // amqps:// scheme) lowercased and without a trailing slash.
        var trimmed = audience.TrimEnd('/');
        return trimmed.ToLowerInvariant();
    }
}

/// <summary>
/// Minimal AOT-friendly URL encoder covering the characters that
/// appear in Service Bus SAS tokens (path, base64 signature, key name).
/// Encodes everything except unreserved chars per RFC 3986
/// (ALPHA / DIGIT / "-" / "_" / "." / "~"). Output uses uppercase hex.
/// </summary>
internal static class UrlEncoder
{
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var src = Encoding.UTF8.GetBytes(value);
        // Worst case 3x expansion (each byte → "%HH").
        var rented = ArrayPool<byte>.Shared.Rent(src.Length * 3);
        try
        {
            int o = 0;
            foreach (var b in src)
            {
                if (IsUnreserved(b))
                {
                    rented[o++] = b;
                }
                else
                {
                    rented[o++] = (byte)'%';
                    rented[o++] = HexUpper(b >> 4);
                    rented[o++] = HexUpper(b & 0xF);
                }
            }
            return Encoding.ASCII.GetString(rented, 0, o);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsUnreserved(byte b)
        => (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'-' || b == (byte)'_' || b == (byte)'.' || b == (byte)'~';

    private static byte HexUpper(int nibble)
        => (byte)(nibble < 10 ? (byte)'0' + nibble : (byte)'A' + (nibble - 10));
}
