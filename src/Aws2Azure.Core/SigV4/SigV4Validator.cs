using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Core.SigV4;

public enum SigV4ValidationStatus
{
    Ok,
    Malformed,
    UnknownAccessKey,
    InvalidSignature,
    Expired,
    ClockSkewTooLarge,
}

public readonly record struct SigV4ValidationResult(
    SigV4ValidationStatus Status,
    string? Reason = null,
    string? AccessKeyId = null,
    string[]? SignedHeaders = null,
    string? Region = null)
{
    public bool IsValid => Status == SigV4ValidationStatus.Ok;

    public static SigV4ValidationResult Ok(string accessKeyId, string[] signedHeaders, string region)
        => new(SigV4ValidationStatus.Ok, AccessKeyId: accessKeyId, SignedHeaders: signedHeaders, Region: region);

    public static SigV4ValidationResult Fail(SigV4ValidationStatus status, string reason)
        => new(status, reason);
}

/// <summary>
/// Per-request input fed to <see cref="SigV4Validator"/>. The caller is
/// responsible for capturing the on-the-wire form of method/path/query and
/// for providing a payload hash (or the relevant sentinel).
/// </summary>
public sealed class SigV4Request
{
    public required string HttpMethod { get; init; }
    public required string RawPath { get; init; }
    public required string RawQueryString { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
    public required string PayloadHash { get; init; }
    public bool S3PathStyle { get; init; }
    public DateTimeOffset? Now { get; init; }
}

/// <summary>
/// Validates an incoming AWS SigV4 request — either via the
/// <c>Authorization</c> header or via presigned URL query parameters.
/// </summary>
public sealed class SigV4Validator
{
    private readonly ICredentialResolver _credentials;
    private readonly TimeSpan _maxClockSkew;

    /// <summary>
    /// Opt-in presigned-URL host-rewrite allowlist: AWS origin signing hosts a
    /// presigned URL may legitimately have been signed against before its host
    /// was rewritten to the proxy. Empty (default) = strict host binding. Stored
    /// lowercase; see <see cref="Configuration.S3Settings.PresignedTrustedSigningHosts"/>.
    /// </summary>
    private readonly string[] _presignedTrustedSigningHosts;

    public SigV4Validator(
        ICredentialResolver credentials,
        TimeSpan? maxClockSkew = null,
        IReadOnlyList<string>? presignedTrustedSigningHosts = null)
    {
        _credentials = credentials;
        _maxClockSkew = maxClockSkew ?? TimeSpan.FromMinutes(15);

        if (presignedTrustedSigningHosts is { Count: > 0 })
        {
            var normalized = new string[presignedTrustedSigningHosts.Count];
            for (var i = 0; i < normalized.Length; i++)
            {
                normalized[i] = presignedTrustedSigningHosts[i].ToLowerInvariant();
            }
            _presignedTrustedSigningHosts = normalized;
        }
        else
        {
            _presignedTrustedSigningHosts = Array.Empty<string>();
        }
    }

    public SigV4ValidationResult Validate(SigV4Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (TryGetQueryValue(request.RawQueryString, SigV4Constants.AmzSignatureQuery, out _))
        {
            return ValidatePresigned(request);
        }

        var authHeader = FindHeader(request.Headers, SigV4Constants.AuthorizationHeader);
        if (authHeader is null)
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Malformed,
                "no Authorization header and no X-Amz-Signature query parameter");
        }

        if (!AuthorizationHeader.TryParse(authHeader, out var parsed))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Malformed,
                "Authorization header could not be parsed");
        }

        var amzDate = FindHeader(request.Headers, SigV4Constants.AmzDateHeader)
                      ?? FindHeader(request.Headers, "date");
        if (string.IsNullOrEmpty(amzDate))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Malformed,
                "missing X-Amz-Date / Date header");
        }

        return ValidateInternal(
            request,
            parsed.Credential,
            parsed.SignedHeaders,
            parsed.Signature,
            amzDate,
            allowExpiry: false);
    }

    private SigV4ValidationResult ValidatePresigned(SigV4Request request)
    {
        if (!TryGetQueryValue(request.RawQueryString, SigV4Constants.AmzCredentialQuery, out var credentialRaw)
            || !TryGetQueryValue(request.RawQueryString, SigV4Constants.AmzSignedHeadersQuery, out var signedHeadersRaw)
            || !TryGetQueryValue(request.RawQueryString, SigV4Constants.AmzSignatureQuery, out var signature)
            || !TryGetQueryValue(request.RawQueryString, SigV4Constants.AmzDateQuery, out var amzDate))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Malformed,
                "presigned URL is missing one of X-Amz-Credential / X-Amz-SignedHeaders / X-Amz-Signature / X-Amz-Date");
        }

        if (!CredentialScope.TryParse(credentialRaw, out var scope))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Malformed,
                "X-Amz-Credential is not a valid scope");
        }

        if (!SigningKey.TryParseAmzDate(amzDate, out var signedAt))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Malformed,
                "X-Amz-Date is not a valid ISO 8601 basic timestamp");
        }

        // AWS requires X-Amz-Expires on every presigned URL; it must be a
        // positive integer no greater than 604800 (7 days).
        if (!TryGetQueryValue(request.RawQueryString, SigV4Constants.AmzExpiresQuery, out var expiresRaw)
            || !int.TryParse(expiresRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var expiresSeconds)
            || expiresSeconds < 1
            || expiresSeconds > 604_800)
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Malformed,
                "X-Amz-Expires is required and must be an integer between 1 and 604800");
        }

        var now = request.Now ?? DateTimeOffset.UtcNow;
        if (now > signedAt + TimeSpan.FromSeconds(expiresSeconds))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.Expired,
                "presigned URL has expired");
        }
        if (signedAt - now > _maxClockSkew)
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.ClockSkewTooLarge,
                "X-Amz-Date is too far in the future");
        }

        var signedHeaders = signedHeadersRaw.Split(';');
        return ValidateInternal(request, scope, signedHeaders, signature, amzDate, allowExpiry: true);
    }

    private SigV4ValidationResult ValidateInternal(
        SigV4Request request,
        CredentialScope scope,
        string[] signedHeaders,
        string clientSignature,
        string amzDate,
        bool allowExpiry)
    {
        if (!_credentials.TryGetAwsSecret(scope.AccessKeyId, out var secret))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.UnknownAccessKey,
                $"access key '{scope.AccessKeyId}' is not configured");
        }

        if (SigningKey.TryParseAmzDate(amzDate, out var requestTime))
        {
            var now = request.Now ?? DateTimeOffset.UtcNow;
            var skew = (now - requestTime).Duration();
            if (!allowExpiry && skew > _maxClockSkew)
            {
                return SigV4ValidationResult.Fail(SigV4ValidationStatus.ClockSkewTooLarge,
                    $"clock skew {(int)skew.TotalSeconds}s exceeds {(int)_maxClockSkew.TotalSeconds}s");
            }
        }

        if (SignatureMatches(request, secret, scope, signedHeaders, amzDate, clientSignature,
                request.Headers, request.RawPath))
        {
            return SigV4ValidationResult.Ok(scope.AccessKeyId, signedHeaders, scope.Region);
        }

        // Presigned host-rewrite fallback (opt-in): the URL may have been signed
        // against an AWS S3 endpoint host and then had its host rewritten to the
        // proxy. Re-check the signature against each configured trusted origin
        // host (path-style and virtual-hosted). Header-authenticated requests are
        // never eligible — allowExpiry is only true on the presigned path.
        if (allowExpiry && _presignedTrustedSigningHosts.Length > 0
            && TryPresignedHostRewrite(request, secret, scope, signedHeaders, amzDate, clientSignature))
        {
            return SigV4ValidationResult.Ok(scope.AccessKeyId, signedHeaders, scope.Region);
        }

        return SigV4ValidationResult.Fail(SigV4ValidationStatus.InvalidSignature, "signature mismatch");
    }

    /// <summary>
    /// Recomputes the expected SigV4 signature for <paramref name="request"/>
    /// using the supplied header set and path, and constant-time-compares it to
    /// the client-presented signature. The header set / path are parameters so
    /// the presigned host-rewrite fallback can substitute a different signed
    /// <c>host</c> and (for virtual-hosted origins) a bucket-stripped path.
    /// </summary>
    private static bool SignatureMatches(
        SigV4Request request,
        string secret,
        CredentialScope scope,
        string[] signedHeaders,
        string amzDate,
        string clientSignature,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string rawPath)
    {
        Span<byte> canonicalHash = stackalloc byte[32];
        CanonicalRequest.HashCanonicalRequest(
            request.HttpMethod, rawPath, request.RawQueryString,
            headers, signedHeaders, request.PayloadHash, request.S3PathStyle,
            canonicalHash);

        Span<byte> expectedHex = stackalloc byte[64];
        SigningKey.ComputeExpectedSignatureHex(secret, scope, amzDate, canonicalHash, expectedHex);

        // A SigV4 signature is always 64 lowercase hex chars; ASCII.GetBytes is
        // 1:1 so a length check on the string matches the old byte-length guard.
        if (clientSignature.Length != expectedHex.Length)
        {
            return false;
        }

        Span<byte> clientBytes = stackalloc byte[64];
        Encoding.ASCII.GetBytes(clientSignature, clientBytes);
        return CryptographicOperations.FixedTimeEquals(clientBytes, expectedHex);
    }

    /// <summary>
    /// Presigned host-rewrite fallback. For each configured trusted origin host
    /// <c>H</c>, retries the signature check against the two ways an AWS-signed
    /// presigned URL is commonly rewritten to a path-style proxy request:
    /// <list type="bullet">
    /// <item>path-style origin — <c>host = H</c>, path unchanged; and</item>
    /// <item>virtual-hosted origin — <c>host = {bucket}.H</c> with the leading
    /// <c>/{bucket}</c> stripped from the path.</item>
    /// </list>
    /// The signature still requires the correct secret and every other signed
    /// parameter; only the signed <c>host</c> (and, for vhost, the bucket path
    /// segment) is substituted.
    /// </summary>
    private bool TryPresignedHostRewrite(
        SigV4Request request,
        string secret,
        CredentialScope scope,
        string[] signedHeaders,
        string amzDate,
        string clientSignature)
    {
        // The vhost candidate reconstructs the origin from a leading path segment,
        // so it only applies when the proxy received the request path-style
        // (/{bucket}/{key...}). Parse it once.
        SplitLeadingSegment(request.RawPath, out var bucket, out var remainderPath);

        foreach (var host in _presignedTrustedSigningHosts)
        {
            // Candidate A — path-style origin: host = H, path unchanged.
            var pathStyleHeaders = WithHostHeader(request.Headers, host);
            if (SignatureMatches(request, secret, scope, signedHeaders, amzDate, clientSignature,
                    pathStyleHeaders, request.RawPath))
            {
                return true;
            }

            // Candidate B — virtual-hosted origin: host = {bucket}.H, bucket
            // stripped from the path.
            if (bucket.Length > 0)
            {
                var vhostHeaders = WithHostHeader(request.Headers, string.Concat(bucket, ".", host));
                if (SignatureMatches(request, secret, scope, signedHeaders, amzDate, clientSignature,
                        vhostHeaders, remainderPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a raw request path <c>/{first}/{rest}</c> into its first segment
    /// (candidate bucket name) and the remainder path <c>/{rest}</c>. Returns an
    /// empty bucket when the path has no distinct leading segment (root, or a
    /// single segment such as <c>/key</c>).
    /// </summary>
    internal static void SplitLeadingSegment(string rawPath, out string bucket, out string remainderPath)
    {
        bucket = string.Empty;
        remainderPath = rawPath;

        if (string.IsNullOrEmpty(rawPath) || rawPath[0] != '/')
        {
            return;
        }

        var secondSlash = rawPath.IndexOf('/', 1);
        if (secondSlash < 0)
        {
            // Only one segment (e.g. "/key") — no leading bucket to strip.
            return;
        }

        var first = rawPath.Substring(1, secondSlash - 1);
        if (first.Length == 0)
        {
            // Path started with "//" — no usable bucket segment.
            return;
        }

        bucket = first;
        remainderPath = rawPath.Substring(secondSlash);
    }

    /// <summary>
    /// Returns a copy of <paramref name="headers"/> with the value of the
    /// (case-insensitive) <c>host</c> header replaced by
    /// <paramref name="hostValue"/>. All other headers are preserved in order.
    /// </summary>
    private static List<KeyValuePair<string, string>> WithHostHeader(
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string hostValue)
    {
        var copy = new List<KeyValuePair<string, string>>(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            var kv = headers[i];
            if (string.Equals(kv.Key, SigV4Constants.HostHeader, StringComparison.OrdinalIgnoreCase))
            {
                copy.Add(new KeyValuePair<string, string>(kv.Key, hostValue));
            }
            else
            {
                copy.Add(kv);
            }
        }
        return copy;
    }


    private static string? FindHeader(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return headers[i].Value;
            }
        }
        return null;
    }

    private static bool TryGetQueryValue(string rawQueryString, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(rawQueryString))
        {
            return false;
        }

        var qs = rawQueryString[0] == '?' ? rawQueryString[1..] : rawQueryString;
        foreach (var part in qs.Split('&'))
        {
            var eq = part.IndexOf('=');
            string k = eq < 0 ? part : part[..eq];
            if (k.Equals(key, StringComparison.Ordinal))
            {
                value = eq < 0 ? string.Empty : UrlUnescape(part[(eq + 1)..]);
                return true;
            }
        }
        return false;
    }

    private static string UrlUnescape(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));
}
