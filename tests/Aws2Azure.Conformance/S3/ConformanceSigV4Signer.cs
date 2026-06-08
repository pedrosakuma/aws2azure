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
        DateTimeOffset? now = null)
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

        var signedHeaders = new[]
        {
            "host",
            SigV4Constants.AmzContentSha256Header,
            SigV4Constants.AmzDateHeader,
        };
        Array.Sort(signedHeaders, StringComparer.Ordinal);

        var canonical = CanonicalRequest.Build(
            request.Method.Method,
            Uri.UnescapeDataString(request.RequestUri.AbsolutePath),
            request.RequestUri.Query.TrimStart('?'),
            headers,
            signedHeaders,
            payloadHash,
            s3PathStyle: true);

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
