using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Core.SigV4;

/// <summary>Derives the SigV4 signing key via the documented HMAC chain.</summary>
public static class SigningKey
{
    public static byte[] Derive(string secretKey, string date, string region, string service)
    {
        var kSecret = Encoding.UTF8.GetBytes(SigV4Constants.SecretKeyPrefix + secretKey);
        var kDate    = HmacSha256(kSecret, date);
        var kRegion  = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, SigV4Constants.TerminationString);
    }

    public static byte[] HmacSha256(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    public static byte[] HmacSha256(byte[] key, byte[] data)
        => HMACSHA256.HashData(key, data);

    public static string ToLowerHex(ReadOnlySpan<byte> bytes)
    {
        // Convert.ToHexStringLower is AOT-safe in .NET 9+.
        return Convert.ToHexStringLower(bytes);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return ToLowerHex(hash);
    }

    public static bool TryParseAmzDate(string value, out DateTimeOffset utc)
        => DateTimeOffset.TryParseExact(
            value,
            SigV4Constants.AmzDateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out utc);
}
