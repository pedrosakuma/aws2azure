using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aws2Azure.Modules.S3.Internal;

/// <summary>
/// Stateless codec for S3 multipart <c>UploadId</c> tokens. The proxy keeps
/// no per-upload server state — every uploadId is a 32-byte token that
/// embeds its own nonce + creation timestamp and is HMAC-bound to the
/// (Azure account, container, object key) it was issued for.
/// </summary>
/// <remarks>
/// <para>
/// Wire format (32 bytes, base64url-encoded → 43 chars, no padding):
/// <list type="bullet">
///   <item><c>nonce</c> (8 bytes) — random per upload, also used to scope
///   block IDs so two concurrent uploads on the same blob never collide.</item>
///   <item><c>createdAtMs</c> (8 bytes, big-endian unix-ms) — used to
///   enforce the 7-day TTL that matches Azure's uncommitted-block GC window.</item>
///   <item><c>tag</c> (16 bytes) — first 128 bits of
///   <c>HMAC-SHA256(accountKey, account || 0 || container || 0 || key || 0 || nonce || createdAtMs)</c>.</item>
/// </list>
/// </para>
/// <para>
/// Binding the tag to (account, container, key) prevents a token issued
/// for one blob from being replayed against another. Constant-time tag
/// comparison keeps validation safe from timing oracles.
/// </para>
/// </remarks>
internal static class UploadIdCodec
{
    public const int NonceBytes = 8;
    public const int TagBytes = 16;
    public const int RawLength = NonceBytes + 8 + TagBytes; // 32

    /// <summary>Tokens are rejected after this age — matches Azure's
    /// uncommitted-block garbage-collection window.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public readonly record struct UploadToken(
        ReadOnlyMemory<byte> Nonce,
        DateTimeOffset CreatedAt,
        string Encoded)
    {
        public string NonceHex => Convert.ToHexString(Nonce.Span).ToLowerInvariant();
    }

    public static UploadToken Issue(
        string accountName,
        string container,
        string key,
        ReadOnlySpan<byte> accountKey,
        DateTimeOffset? now = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var createdAt = now ?? DateTimeOffset.UtcNow;
        var raw = Build(accountName, container, key, accountKey, nonce, createdAt);
        return new UploadToken(nonce, createdAt, Base64Url.Encode(raw));
    }

    /// <summary>
    /// Validates <paramref name="encoded"/> against the supplied scope.
    /// Returns the decoded token on success, <c>null</c> on any validation
    /// failure (bad format, tag mismatch, expired). Callers translate the
    /// null result into an S3 <c>NoSuchUpload</c> error.
    /// </summary>
    public static UploadToken? TryDecode(
        string encoded,
        string accountName,
        string container,
        string key,
        ReadOnlySpan<byte> accountKey,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }
        if (!Base64Url.TryDecode(encoded, out var raw) || raw.Length != RawLength)
        {
            return null;
        }

        var nonce = raw.AsSpan(0, NonceBytes);
        var tsBytes = raw.AsSpan(NonceBytes, 8);
        var providedTag = raw.AsSpan(NonceBytes + 8, TagBytes);

        var createdAtMs = BinaryPrimitives.ReadInt64BigEndian(tsBytes);
        DateTimeOffset createdAt;
        try
        {
            createdAt = DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        var current = now ?? DateTimeOffset.UtcNow;
        if (current - createdAt > MaxAge || createdAt - current > TimeSpan.FromMinutes(5))
        {
            return null;
        }

        Span<byte> expectedTag = stackalloc byte[TagBytes];
        ComputeTag(accountName, container, key, accountKey, nonce, createdAt, expectedTag);
        if (!CryptographicOperations.FixedTimeEquals(providedTag, expectedTag))
        {
            return null;
        }

        return new UploadToken(nonce.ToArray(), createdAt, encoded);
    }

    /// <summary>
    /// Builds the Azure block ID for <paramref name="partNumber"/> under
    /// the given <paramref name="nonceHex"/>. All block IDs for a blob
    /// must be the same length (Azure constraint); the fixed-width
    /// <c>b{nonce16hex}p{part5d}</c> pre-base64 layout satisfies that.
    /// </summary>
    public static string BlockId(string nonceHex, int partNumber)
    {
        if (nonceHex.Length != NonceBytes * 2)
        {
            throw new ArgumentException("Nonce hex must be " + (NonceBytes * 2) + " chars.", nameof(nonceHex));
        }
        if (partNumber is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }
        // 23 ASCII chars → 32-char base64 (constant length per blob).
        var raw = string.Create(23, (nonceHex, partNumber), static (span, state) =>
        {
            span[0] = 'b';
            state.nonceHex.AsSpan().CopyTo(span[1..]);
            span[1 + NonceBytes * 2] = 'p';
            state.partNumber.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)
                .AsSpan().CopyTo(span[(1 + NonceBytes * 2 + 1)..]);
        });
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw));
    }

    private static byte[] Build(
        string accountName,
        string container,
        string key,
        ReadOnlySpan<byte> accountKey,
        ReadOnlySpan<byte> nonce,
        DateTimeOffset createdAt)
    {
        var raw = new byte[RawLength];
        nonce.CopyTo(raw.AsSpan(0, NonceBytes));
        BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(NonceBytes, 8), createdAt.ToUnixTimeMilliseconds());
        ComputeTag(accountName, container, key, accountKey, nonce, createdAt, raw.AsSpan(NonceBytes + 8, TagBytes));
        return raw;
    }

    private static void ComputeTag(
        string accountName,
        string container,
        string key,
        ReadOnlySpan<byte> accountKey,
        ReadOnlySpan<byte> nonce,
        DateTimeOffset createdAt,
        Span<byte> dest)
    {
        using var hmac = new HMACSHA256(accountKey.ToArray());
        // accountName || 0 || container || 0 || key || 0 || nonce || createdAtMsBE
        var nameBytes = Encoding.UTF8.GetBytes(accountName);
        var containerBytes = Encoding.UTF8.GetBytes(container);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var sep = new byte[] { 0 };
        hmac.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
        hmac.TransformBlock(sep, 0, 1, null, 0);
        hmac.TransformBlock(containerBytes, 0, containerBytes.Length, null, 0);
        hmac.TransformBlock(sep, 0, 1, null, 0);
        hmac.TransformBlock(keyBytes, 0, keyBytes.Length, null, 0);
        hmac.TransformBlock(sep, 0, 1, null, 0);
        var nonceArr = nonce.ToArray();
        hmac.TransformBlock(nonceArr, 0, nonceArr.Length, null, 0);
        var ts = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(ts, createdAt.ToUnixTimeMilliseconds());
        hmac.TransformFinalBlock(ts, 0, ts.Length);
        hmac.Hash.AsSpan(0, TagBytes).CopyTo(dest);
    }

    /// <summary>
    /// Minimal base64url codec (RFC 4648 §5, no padding). UploadIds are
    /// transmitted as query/XML values where '+' and '/' would be
    /// percent-encoded and round-tripped through SDKs unevenly.
    /// </summary>
    internal static class Base64Url
    {
        public static string Encode(ReadOnlySpan<byte> data)
        {
            var b64 = Convert.ToBase64String(data);
            // Strip padding, swap URL-unsafe chars.
            var trimmed = b64.TrimEnd('=');
            return trimmed.Replace('+', '-').Replace('/', '_');
        }

        public static bool TryDecode(string encoded, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (string.IsNullOrEmpty(encoded))
            {
                return false;
            }
            var sb = new StringBuilder(encoded.Length + 3);
            foreach (var c in encoded)
            {
                sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
            }
            switch (sb.Length % 4)
            {
                case 2: sb.Append("=="); break;
                case 3: sb.Append('='); break;
                case 1: return false;
            }
            try
            {
                data = Convert.FromBase64String(sb.ToString());
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
