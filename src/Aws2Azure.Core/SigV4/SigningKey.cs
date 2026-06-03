using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Core.SigV4;

/// <summary>Derives the SigV4 signing key via the documented HMAC chain.</summary>
public static class SigningKey
{
    // Values up to this many UTF-8 bytes are encoded on the stack; larger
    // values (e.g. an attacker-controlled oversized region/service in the
    // credential scope, or a pathological secret) fall back to ArrayPool so a
    // hostile request can never overflow the stack.
    private const int StackEncodeThreshold = 256;

    public static byte[] Derive(string secretKey, string date, string region, string service)
    {
        var kSecret = Encoding.UTF8.GetBytes(SigV4Constants.SecretKeyPrefix + secretKey);
        var kDate    = HmacSha256(kSecret, date);
        var kRegion  = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, SigV4Constants.TerminationString);
    }

    /// <summary>
    /// Allocation-light equivalent of <see cref="Derive(string,string,string,string)"/>:
    /// writes the 32-byte signing key into <paramref name="dest"/> using stack /
    /// pooled scratch and one-shot <see cref="HMACSHA256.HashData(ReadOnlySpan{byte},ReadOnlySpan{byte},Span{byte})"/>
    /// per step. Byte-identical to the array overload (proven by the golden
    /// tests). Secret-bearing scratch buffers are zeroed before release so no
    /// key material is left behind in a returned <see cref="ArrayPool{T}"/>
    /// array or on the stack.
    /// </summary>
    public static void Derive(string secretKey, string date, string region, string service, Span<byte> dest)
    {
        // kSecret = "AWS4" + secretKey, encoded as UTF-8. Writing the prefix and
        // the secret as separate spans is byte-identical to UTF8(prefix+secret):
        // the prefix's last char ('4') cannot form a surrogate pair with the
        // secret's first char across the (UTF-16 string) boundary.
        var prefix = SigV4Constants.SecretKeyPrefix; // "AWS4"
        var secretLen = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(secretKey);
        byte[]? secretRented = secretLen > StackEncodeThreshold
            ? ArrayPool<byte>.Shared.Rent(secretLen)
            : null;
        Span<byte> kSecretScratch = secretRented is null ? stackalloc byte[StackEncodeThreshold] : secretRented;

        Span<byte> a = stackalloc byte[32];
        Span<byte> b = stackalloc byte[32];
        try
        {
            var written = Encoding.UTF8.GetBytes(prefix, kSecretScratch);
            written += Encoding.UTF8.GetBytes(secretKey, kSecretScratch[written..]);
            var kSecret = kSecretScratch[..written];

            HmacUtf8(kSecret, date, a);                                  // kDate
            HmacUtf8(a, region, b);                                      // kRegion
            HmacUtf8(b, service, a);                                     // kService
            HmacUtf8(a, SigV4Constants.TerminationString, dest);        // signing key
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kSecretScratch);
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
            if (secretRented is not null)
            {
                ArrayPool<byte>.Shared.Return(secretRented);
            }
        }
    }

    /// <summary>
    /// Computes the lowercase-hex expected SigV4 signature into
    /// <paramref name="destHexLower"/> (must be 64 bytes) from the precomputed
    /// canonical-request digest — allocation-light end to end. Byte-identical to
    /// the string path
    /// (<c>ToLowerHex(HmacSha256(Derive(...), UTF8(StringToSign(amzDate, scope, canonical))))</c>),
    /// proven by the golden tests.
    /// </summary>
    public static void ComputeExpectedSignatureHex(
        string secretKey,
        CredentialScope scope,
        string amzDate,
        ReadOnlySpan<byte> canonicalHash,
        Span<byte> destHexLower)
    {
        Span<byte> signingKey = stackalloc byte[32];
        Derive(secretKey, scope.Date, scope.Region, scope.Service, signingKey);

        var sts = new PooledByteWriter(192);
        try
        {
            // string-to-sign: Algorithm \n amzDate \n {date}/{region}/{service}/aws4_request \n hex(canonicalHash)
            sts.WriteUtf8(SigV4Constants.Algorithm);
            sts.WriteByte((byte)'\n');
            sts.WriteUtf8(amzDate);
            sts.WriteByte((byte)'\n');
            sts.WriteUtf8(scope.Date);
            sts.WriteByte((byte)'/');
            sts.WriteUtf8(scope.Region);
            sts.WriteByte((byte)'/');
            sts.WriteUtf8(scope.Service);
            sts.WriteByte((byte)'/');
            sts.WriteUtf8(SigV4Constants.TerminationString);
            sts.WriteByte((byte)'\n');
            sts.WriteLowerHex(canonicalHash);

            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(signingKey, sts.WrittenSpan, mac);
            PooledByteWriter.WriteLowerHex(mac, destHexLower);
        }
        finally
        {
            sts.Dispose();
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    /// <summary>
    /// HMAC-SHA256 of UTF-8(<paramref name="data"/>) under <paramref name="key"/>
    /// into <paramref name="dest"/>, encoding the (non-secret) data via stack /
    /// pooled scratch. <paramref name="key"/>, the data scratch, and
    /// <paramref name="dest"/> never alias.
    /// </summary>
    private static void HmacUtf8(ReadOnlySpan<byte> key, string data, Span<byte> dest)
    {
        var byteCount = Encoding.UTF8.GetByteCount(data);
        byte[]? rented = byteCount > StackEncodeThreshold ? ArrayPool<byte>.Shared.Rent(byteCount) : null;
        Span<byte> buffer = rented is null ? stackalloc byte[StackEncodeThreshold] : rented;
        var written = Encoding.UTF8.GetBytes(data, buffer);
        HMACSHA256.HashData(key, buffer[..written], dest);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
