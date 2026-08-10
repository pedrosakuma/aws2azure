using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.SigV4;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Builds an AWS SigV4 presigned URL for S3, mirroring what
/// <c>boto3.client('s3').generate_presigned_url(...)</c> and
/// <c>AWSSDK.S3 GetPreSignedURL</c> emit. Test-only — the proxy itself never
/// signs requests. This is a conformance-tier twin of
/// <c>Aws2Azure.IntegrationTests.Fixtures.TestPresignedUrlBuilder</c>; kept
/// local to this project to avoid a cross-test-project reference.
/// </summary>
internal static class ConformancePresignedUrlBuilder
{
    /// <summary>
    /// Build a presigned URL for the given path-style S3 endpoint. The
    /// returned URI carries the <c>X-Amz-Algorithm / Credential / Date /
    /// Expires / SignedHeaders / Signature</c> query parameters. Only the
    /// <c>host</c> header is signed, matching boto3's default
    /// <c>generate_presigned_url</c> behavior.
    /// </summary>
    public static Uri BuildPresignedUri(
        HttpMethod method,
        Uri baseAddress,
        string pathAndQuery,
        TimeSpan expiresIn,
        string accessKey,
        string secret,
        string region = "us-east-1",
        string service = "s3",
        DateTimeOffset? now = null,
        string? sessionToken = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrEmpty(pathAndQuery);

        var stamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var amzDate = stamp.ToString(SigV4Constants.AmzDateFormat, CultureInfo.InvariantCulture);
        var shortDate = stamp.ToString(SigV4Constants.AmzShortDateFormat, CultureInfo.InvariantCulture);
        var scope = $"{shortDate}/{region}/{service}/{SigV4Constants.TerminationString}";
        var credential = $"{accessKey}/{scope}";

        var absolute = new Uri(baseAddress, pathAndQuery);
        var path = absolute.AbsolutePath;
        var existingQuery = absolute.Query.TrimStart('?');

        var presignParams = new List<(string Key, string Value)>
        {
            (SigV4Constants.AmzAlgorithmQuery, SigV4Constants.Algorithm),
            (SigV4Constants.AmzCredentialQuery, credential),
            (SigV4Constants.AmzDateQuery, amzDate),
            (SigV4Constants.AmzExpiresQuery, ((int)expiresIn.TotalSeconds).ToString(CultureInfo.InvariantCulture)),
            (SigV4Constants.AmzSignedHeadersQuery, "host"),
        };
        if (!string.IsNullOrEmpty(sessionToken))
        {
            presignParams.Add((SigV4Constants.AmzSecurityTokenQuery, sessionToken));
        }

        var queryBuilder = new StringBuilder(existingQuery);
        foreach (var (k, v) in presignParams)
        {
            if (queryBuilder.Length > 0) queryBuilder.Append('&');
            queryBuilder.Append(Uri.EscapeDataString(k));
            queryBuilder.Append('=');
            queryBuilder.Append(Uri.EscapeDataString(v));
        }
        var queryWithoutSignature = queryBuilder.ToString();

        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", baseAddress.Authority),
        };

        var canonical = CanonicalRequest.Build(
            method.Method,
            Uri.UnescapeDataString(path),
            queryWithoutSignature,
            headers,
            ["host"],
            SigV4Constants.UnsignedPayload,
            s3PathStyle: true);

        var stringToSign = CanonicalRequest.StringToSign(amzDate, scope, canonical);
        var key = SigningKey.Derive(secret, shortDate, region, service);
        var signature = SigningKey.ToLowerHex(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));

        var fullQuery = queryWithoutSignature +
            "&" + SigV4Constants.AmzSignatureQuery + "=" + signature;

        var builder = new UriBuilder(absolute) { Query = fullQuery };
        return builder.Uri;
    }
}
