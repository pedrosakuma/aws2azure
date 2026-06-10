using Aws2Azure.Core.SigV4;

namespace Aws2Azure.Core.Modules;

/// <summary>
/// Maps an abstract <see cref="SigV4ValidationStatus"/> failure to the
/// on-the-wire (HTTP status, error code) pair that real AWS returns for the
/// caller's protocol family.
///
/// <para>AWS REST-XML services (S3) answer SigV4 failures with HTTP 403 and the
/// S3 error-code vocabulary (<c>SignatureDoesNotMatch</c>, <c>InvalidAccessKeyId</c>,
/// …). AWS-JSON protocol services (DynamoDB, Kinesis, the modern SQS JSON path)
/// answer with HTTP <b>400</b> and a distinct exception vocabulary
/// (<c>InvalidSignatureException</c>, <c>UnrecognizedClientException</c>, …)
/// emitted by the shared AWS front door — so the code and the status are both
/// observable to an AWS SDK and must not be the S3 vocabulary. Emitting S3 codes
/// for a JSON caller is a wire-faithfulness break (issue #241).</para>
///
/// <para>The JSON vocabulary is identical across AWS-JSON services because the
/// auth front door is service-agnostic; it is keyed only on the wire format, not
/// on the service name.</para>
/// </summary>
public static class AuthErrorVocabulary
{
    /// <summary>
    /// Resolves the faithful (HTTP status, error code) for a SigV4 failure in
    /// the given wire format. <paramref name="status"/> must be a failure
    /// status; <see cref="SigV4ValidationStatus.Ok"/> is treated as the default.
    /// </summary>
    public static (int StatusCode, string Code) Resolve(AwsErrorFormat format, SigV4ValidationStatus status)
        => format == AwsErrorFormat.Json ? ResolveJson(status) : ResolveXml(status);

    private static (int, string) ResolveJson(SigV4ValidationStatus status) => status switch
    {
        // Bad signature and clock skew both surface as InvalidSignatureException
        // on the JSON front door (skew is reported as "Signature expired …").
        SigV4ValidationStatus.InvalidSignature  => (400, "InvalidSignatureException"),
        SigV4ValidationStatus.ClockSkewTooLarge => (400, "InvalidSignatureException"),
        SigV4ValidationStatus.Expired           => (400, "InvalidSignatureException"),
        // Unknown / unconfigured access key.
        SigV4ValidationStatus.UnknownAccessKey  => (400, "UnrecognizedClientException"),
        // A malformed / incomplete Authorization header (unparseable, missing
        // date, bad presigned params). The pure no-credentials case is also
        // folded into Malformed by the validator; real AWS would answer that
        // narrow sub-case with MissingAuthenticationTokenException/403, but the
        // dominant Malformed case here is a genuinely malformed signature, for
        // which IncompleteSignatureException/400 is the faithful JSON code.
        _                                       => (400, "IncompleteSignatureException"),
    };

    private static (int, string) ResolveXml(SigV4ValidationStatus status) => status switch
    {
        SigV4ValidationStatus.InvalidSignature  => (403, "SignatureDoesNotMatch"),
        SigV4ValidationStatus.UnknownAccessKey  => (403, "InvalidAccessKeyId"),
        SigV4ValidationStatus.Expired           => (403, "AccessDenied"),
        SigV4ValidationStatus.ClockSkewTooLarge => (403, "RequestTimeTooSkewed"),
        _                                       => (400, "InvalidRequest"),
    };
}
