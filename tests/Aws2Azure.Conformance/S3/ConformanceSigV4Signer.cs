using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Minimal SigV4 signer for crafting valid- and invalid-auth requests in the
/// conformance error matrix. Mirrors the integration-test signer; test-only —
/// the proxy never signs. Signing with a wrong secret yields
/// SignatureDoesNotMatch; an unknown key yields InvalidAccessKeyId; an old
/// timestamp yields RequestTimeTooSkewed.
/// </summary>
internal static class ConformanceSigV4Signer
{
    public static void SignHeader(
        HttpRequestMessage request,
        byte[] body,
        string accessKey,
        string secret,
        string region = "us-east-1",
        string service = "s3",
        DateTimeOffset? now = null,
        IReadOnlyList<string>? extraSignedHeaders = null,
        bool? s3PathStyle = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("RequestUri is required.");
        }

        var stamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = stamp.ToString(SigV4Constants.AmzDateFormat, CultureInfo.InvariantCulture);
        var shortDate = stamp.ToString(SigV4Constants.AmzShortDateFormat, CultureInfo.InvariantCulture);
        var scope = $"{shortDate}/{region}/{service}/{SigV4Constants.TerminationString}";

        var payloadHash = body.Length == 0
            ? SigV4Constants.EmptyPayloadSha256
            : SigningKey.Sha256Hex(body);

        request.Headers.TryAddWithoutValidation(SigV4Constants.AmzDateHeader, amzDate);
        request.Headers.TryAddWithoutValidation(SigV4Constants.AmzContentSha256Header, payloadHash);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", request.RequestUri.Authority),
            new(SigV4Constants.AmzDateHeader, amzDate),
            new(SigV4Constants.AmzContentSha256Header, payloadHash),
        };

        var signedSet = new SortedSet<string>(StringComparer.Ordinal)
        {
            "host",
            SigV4Constants.AmzContentSha256Header,
            SigV4Constants.AmzDateHeader,
        };
        // Some modules require additional headers be signed (e.g. DynamoDB's
        // X-Amz-Target, which the proxy enforces via RequiredSignedHeaders so a
        // signed payload cannot be redirected to a different operation). Pull the
        // value off the request — it must already be attached.
        if (extraSignedHeaders is not null)
        {
            foreach (var name in extraSignedHeaders)
            {
                var lower = name.ToLowerInvariant();
                if (!signedSet.Add(lower)) continue;
                string? value = null;
                if (request.Headers.TryGetValues(lower, out var hv))
                {
                    value = string.Join(",", hv);
                }
                else if (request.Content?.Headers.TryGetValues(lower, out var chv) == true)
                {
                    value = string.Join(",", chv);
                }
                if (value is null)
                {
                    throw new InvalidOperationException(
                        $"ConformanceSigV4Signer: extra signed header '{lower}' is not present on the request.");
                }
                headers.Add(new KeyValuePair<string, string>(lower, value));
            }
        }
        var signedHeaders = signedSet.ToArray();

        // Mirror the proxy's per-service path canonicalization
        // (ServiceModuleRegistry: s3PathStyle = ServiceName == "s3"). S3
        // single-encodes the path; every other service double-encodes it, so a
        // multi-service signer must follow suit or it would produce a signature
        // the proxy rejects for any non-root path on a non-S3 service.
        var pathStyle = s3PathStyle ?? string.Equals(service, "s3", StringComparison.Ordinal);

        var canonical = CanonicalRequest.Build(
            request.Method.Method,
            Uri.UnescapeDataString(request.RequestUri.AbsolutePath),
            request.RequestUri.Query.TrimStart('?'),
            headers,
            signedHeaders,
            payloadHash,
            s3PathStyle: pathStyle);

        var stringToSign = CanonicalRequest.StringToSign(amzDate, scope, canonical);

        var key = SigningKey.Derive(secret, shortDate, region, service);
        var sigBytes = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign));
        var signature = SigningKey.ToLowerHex(sigBytes);

        var auth =
            $"{SigV4Constants.Algorithm} " +
            $"Credential={accessKey}/{scope}, " +
            $"SignedHeaders={string.Join(';', signedHeaders)}, " +
            $"Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", auth);
    }
}
