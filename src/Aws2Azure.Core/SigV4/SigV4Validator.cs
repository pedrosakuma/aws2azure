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
    string[]? SignedHeaders = null)
{
    public bool IsValid => Status == SigV4ValidationStatus.Ok;

    public static SigV4ValidationResult Ok(string accessKeyId, string[] signedHeaders)
        => new(SigV4ValidationStatus.Ok, AccessKeyId: accessKeyId, SignedHeaders: signedHeaders);

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

    public SigV4Validator(ICredentialResolver credentials, TimeSpan? maxClockSkew = null)
    {
        _credentials = credentials;
        _maxClockSkew = maxClockSkew ?? TimeSpan.FromMinutes(15);
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

        Span<byte> canonicalHash = stackalloc byte[32];
        CanonicalRequest.HashCanonicalRequest(
            request.HttpMethod, request.RawPath, request.RawQueryString,
            request.Headers, signedHeaders, request.PayloadHash, request.S3PathStyle,
            canonicalHash);

        Span<byte> expectedHex = stackalloc byte[64];
        SigningKey.ComputeExpectedSignatureHex(secret, scope, amzDate, canonicalHash, expectedHex);

        // A SigV4 signature is always 64 lowercase hex chars; ASCII.GetBytes is
        // 1:1 so a length check on the string matches the old byte-length guard.
        if (clientSignature.Length != expectedHex.Length)
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.InvalidSignature,
                "signature mismatch");
        }

        Span<byte> clientBytes = stackalloc byte[64];
        Encoding.ASCII.GetBytes(clientSignature, clientBytes);
        if (!CryptographicOperations.FixedTimeEquals(clientBytes, expectedHex))
        {
            return SigV4ValidationResult.Fail(SigV4ValidationStatus.InvalidSignature,
                "signature mismatch");
        }

        return SigV4ValidationResult.Ok(scope.AccessKeyId, signedHeaders);
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
