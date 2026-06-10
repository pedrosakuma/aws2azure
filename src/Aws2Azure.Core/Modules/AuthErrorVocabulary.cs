using Aws2Azure.Core.SigV4;

namespace Aws2Azure.Core.Modules;

/// <summary>
/// The on-the-wire auth-error vocabulary a service speaks. AWS does not have a
/// single "XML" auth vocabulary: the <em>unknown access key</em> code differs by
/// service family even among REST/Query-XML services. The shared AWS Query/EC2
/// auth front door (SNS, SQS-Query, STS, EC2, IAM, …) answers an unrecognised
/// access key with <c>InvalidClientTokenId</c>, whereas S3 — which has its own
/// bespoke error vocabulary — answers <c>InvalidAccessKeyId</c>. AWS-JSON
/// services answer <c>UnrecognizedClientException</c>. Keying the vocabulary on
/// the wire <em>format</em> alone (issue #241) was therefore insufficient and
/// emitted the S3 code for SNS / SQS-Query (issue #247).
/// </summary>
public enum AwsAuthErrorDialect
{
    /// <summary>
    /// S3 REST-XML. Unknown key → <c>InvalidAccessKeyId</c> / 403. S3 is the
    /// documented exception; it must opt into this dialect explicitly.
    /// </summary>
    S3Xml,

    /// <summary>
    /// AWS Query / EC2 XML (SNS, SQS legacy Query, …). Unknown key →
    /// <c>InvalidClientTokenId</c> / 403, emitted by the shared AWS auth front
    /// door. This is the default for XML services because it is the common case.
    /// </summary>
    QueryXml,

    /// <summary>
    /// AWS-JSON 1.x (DynamoDB, Kinesis, modern SQS-Json). Unknown key →
    /// <c>UnrecognizedClientException</c> / 400. Service-agnostic.
    /// </summary>
    Json,
}

/// <summary>
/// Maps an abstract <see cref="SigV4ValidationStatus"/> failure to the
/// on-the-wire (HTTP status, error code) pair that real AWS returns for the
/// caller's protocol family.
///
/// <para>AWS-JSON protocol services (DynamoDB, Kinesis, the modern SQS JSON path)
/// answer SigV4 failures with HTTP <b>400</b> and a distinct exception vocabulary
/// (<c>InvalidSignatureException</c>, <c>UnrecognizedClientException</c>, …)
/// emitted by the shared AWS front door — so the code and the status are both
/// observable to an AWS SDK and must not be the S3 vocabulary. Emitting S3 codes
/// for a JSON caller is a wire-faithfulness break (issue #241).</para>
///
/// <para>The XML vocabulary is <b>not</b> uniform: the unknown-key code is
/// service-specific (S3 → <c>InvalidAccessKeyId</c>; the Query front door →
/// <c>InvalidClientTokenId</c>), so it is keyed on <see cref="AwsAuthErrorDialect"/>
/// rather than the wire format alone (issue #247). The remaining XML codes
/// (<c>SignatureDoesNotMatch</c>, <c>RequestTimeTooSkewed</c>, …) are shared
/// across the XML dialects.</para>
/// </summary>
public static class AuthErrorVocabulary
{
    /// <summary>
    /// Resolves the faithful (HTTP status, error code) for a SigV4 failure in
    /// the given auth-error dialect. <paramref name="status"/> must be a failure
    /// status; <see cref="SigV4ValidationStatus.Ok"/> is treated as the default.
    /// </summary>
    public static (int StatusCode, string Code) Resolve(AwsAuthErrorDialect dialect, SigV4ValidationStatus status)
        => dialect switch
        {
            AwsAuthErrorDialect.Json  => ResolveJson(status),
            AwsAuthErrorDialect.S3Xml => ResolveXml(status, unknownKeyCode: "InvalidAccessKeyId"),
            _                         => ResolveXml(status, unknownKeyCode: "InvalidClientTokenId"),
        };

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

    private static (int, string) ResolveXml(SigV4ValidationStatus status, string unknownKeyCode) => status switch
    {
        SigV4ValidationStatus.InvalidSignature  => (403, "SignatureDoesNotMatch"),
        // The only XML code that differs by service family (issue #247): S3 →
        // InvalidAccessKeyId, AWS Query front door (SNS, SQS-Query) →
        // InvalidClientTokenId. Both are HTTP 403.
        SigV4ValidationStatus.UnknownAccessKey  => (403, unknownKeyCode),
        SigV4ValidationStatus.Expired           => (403, "AccessDenied"),
        SigV4ValidationStatus.ClockSkewTooLarge => (403, "RequestTimeTooSkewed"),
        _                                       => (400, "InvalidRequest"),
    };
}
