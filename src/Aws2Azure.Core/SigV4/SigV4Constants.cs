namespace Aws2Azure.Core.SigV4;

/// <summary>Names and literals defined by the AWS SigV4 specification.</summary>
public static class SigV4Constants
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string TerminationString = "aws4_request";
    public const string SecretKeyPrefix = "AWS4";

    public const string AmzDateHeader            = "x-amz-date";
    public const string AmzContentSha256Header   = "x-amz-content-sha256";
    public const string AmzSecurityTokenHeader   = "x-amz-security-token";
    public const string HostHeader               = "host";
    public const string AuthorizationHeader      = "Authorization";

    // Query-string parameters for presigned URLs.
    public const string AmzAlgorithmQuery     = "X-Amz-Algorithm";
    public const string AmzCredentialQuery    = "X-Amz-Credential";
    public const string AmzDateQuery          = "X-Amz-Date";
    public const string AmzExpiresQuery       = "X-Amz-Expires";
    public const string AmzSignedHeadersQuery = "X-Amz-SignedHeaders";
    public const string AmzSignatureQuery     = "X-Amz-Signature";
    public const string AmzSecurityTokenQuery = "X-Amz-Security-Token";

    // Sentinel payload-hash values accepted by SigV4.
    public const string UnsignedPayload          = "UNSIGNED-PAYLOAD";
    public const string StreamingPayloadSha256   = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";
    public const string StreamingPayloadTrailer  = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER";
    public const string EmptyPayloadSha256       = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    // Long ISO 8601 basic format used by AmzDate ("yyyyMMddTHHmmssZ").
    public const string AmzDateFormat = "yyyyMMddTHHmmssZ";
    public const string AmzShortDateFormat = "yyyyMMdd";
}
