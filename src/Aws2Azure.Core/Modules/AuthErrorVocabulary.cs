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
    /// AWS-JSON 1.x for DynamoDB and Kinesis. <b>Not</b> confirmed uniform
    /// across the whole AWS-JSON family — see the class doc comment. A
    /// real-AWS capture (workflow run 31397375332, 2026-08-10) proved
    /// <c>InvalidSignatureException</c> is HTTP <b>400</b> here, contradicting
    /// the "shared 403 JSON front door" assumption a prior fix (#750) drew
    /// from SQS-JSON evidence plus the CommonErrors API reference pages. Every
    /// other code in this dialect (<c>UnrecognizedClientException</c>,
    /// <c>IncompleteSignatureException</c>) has never been exercised against
    /// real AWS and is kept at the conservative pre-#750 baseline (400) until
    /// independently confirmed — do not assume 403 for those either.
    /// </summary>
    Json,

    /// <summary>
    /// AWS-JSON 1.0 for SQS's modern JSON front door only. Confirmed distinct
    /// from <see cref="Json"/> (DynamoDB/Kinesis): a real-AWS capture (workflow
    /// run 31347507212, 2026-08-10) proved <c>AmazonSQS.ListQueues</c> signed
    /// with a wrong secret answers HTTP <b>403</b> with
    /// <c>InvalidSignatureException</c>. This dialect must not be conflated
    /// with <see cref="Json"/> — the AWS-JSON wire format is shared, but the
    /// auth-error status is per-service, not per-protocol.
    /// </summary>
    SqsJson,
}

/// <summary>
/// Maps an abstract <see cref="SigV4ValidationStatus"/> failure to the
/// on-the-wire (HTTP status, error code) pair that real AWS returns for the
/// caller's protocol family.
///
/// <para><b>The AWS-JSON auth-error status is per-service, not per-protocol.</b>
/// A prior fix (#750) assumed the AWS-JSON front door answers SigV4 failures
/// uniformly at HTTP 403 across DynamoDB, Kinesis, and SQS-JSON, based on a
/// real-AWS SQS-JSON capture (workflow run 31347507212, 2026-08-10, confirming
/// <c>AmazonSQS.ListQueues</c> signed with a wrong secret returns
/// <b>403</b> with <c>{"__type":"com.amazon.coral.service#InvalidSignatureException"}</c>)
/// plus the DynamoDB/Kinesis "Common Error Types" API reference pages
/// (https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/CommonErrors.html,
/// https://docs.aws.amazon.com/kinesis/latest/APIReference/CommonErrors.html),
/// which were <b>interpreted</b> (not directly captured) as implying the same
/// 403 for the whole shared front door. A fresh real-AWS capture (workflow run
/// 31397375332, 2026-08-10) disproved that generalization:
/// <c>dynamodb.InvalidSignatureException</c> and
/// <c>kinesis.InvalidSignatureException</c> both return HTTP <b>400</b>, not
/// 403, on real AWS. So the JSON-protocol dialect is NOT uniform across
/// services — SQS-JSON really is 403 for this failure mode, DynamoDB/Kinesis
/// really are 400, and API-reference pages are not a substitute for a live
/// capture when doc language ("this error can occur") doesn't pin the exact
/// status per raising code path.</para>
///
/// <para>This is expressed as two separate <see cref="AwsAuthErrorDialect"/>
/// values instead of one shared <c>Json</c> dialect:
/// <see cref="AwsAuthErrorDialect.Json"/> for DynamoDB/Kinesis (confirmed 400
/// for <c>InvalidSignatureException</c>; every other code in this dialect is
/// unverified and kept at the conservative pre-#750 baseline of 400) and
/// <see cref="AwsAuthErrorDialect.SqsJson"/> for SQS's modern JSON front door
/// (confirmed 403 for <c>InvalidSignatureException</c>). Callers must not
/// assume these two dialects agree on status code for any failure mode
/// without an independent capture.</para>
///
/// <para>The XML vocabulary is <b>not</b> uniform either: the unknown-key code
/// is service-specific (S3 → <c>InvalidAccessKeyId</c>; the Query front door →
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
            AwsAuthErrorDialect.Json    => ResolveJson(status),
            AwsAuthErrorDialect.SqsJson => ResolveSqsJson(status),
            AwsAuthErrorDialect.S3Xml   => ResolveXml(status, unknownKeyCode: "InvalidAccessKeyId"),
            _                           => ResolveXml(status, unknownKeyCode: "InvalidClientTokenId"),
        };

    // DynamoDB / Kinesis AWS-JSON dialect. Only InvalidSignatureException has
    // a live real-AWS capture (run 31397375332, 2026-08-10: HTTP 400). Every
    // other code here is unverified and kept at the pre-#750 conservative
    // baseline (400) rather than assumed 403 — see the class doc comment.
    private static (int, string) ResolveJson(SigV4ValidationStatus status) => status switch
    {
        // Bad signature and clock skew both surface as InvalidSignatureException
        // on the JSON front door (skew is reported as "Signature expired …").
        // Confirmed 400 by a real-AWS DynamoDB/Kinesis capture (run 31397375332).
        SigV4ValidationStatus.InvalidSignature  => (400, "InvalidSignatureException"),
        SigV4ValidationStatus.ClockSkewTooLarge => (400, "InvalidSignatureException"),
        SigV4ValidationStatus.Expired           => (400, "InvalidSignatureException"),
        // Unknown / unconfigured access key. Unverified against real AWS;
        // kept at the pre-#750 baseline rather than the unconfirmed 403 that
        // #750 assumed from API-reference doc language alone.
        SigV4ValidationStatus.UnknownAccessKey  => (400, "UnrecognizedClientException"),
        // A malformed / incomplete Authorization header (unparseable, missing
        // date, bad presigned params). The pure no-credentials case is also
        // folded into Malformed by the validator; real AWS would answer that
        // narrow sub-case with MissingAuthenticationTokenException/403, but the
        // dominant Malformed case here is a genuinely malformed signature, for
        // which IncompleteSignatureException/400 is the faithful JSON code
        // (unverified against real AWS; pre-#750 baseline).
        _                                       => (400, "IncompleteSignatureException"),
    };

    // SQS's modern AWS-JSON 1.0 front door. Confirmed distinct from
    // ResolveJson (DynamoDB/Kinesis): a real-AWS capture (run 31347507212,
    // 2026-08-10) proved AmazonSQS.ListQueues signed with a wrong secret
    // answers HTTP 403 with InvalidSignatureException. The remaining codes
    // here mirror that capture's status for consistency within SQS's own
    // dialect, matching the previously-shipped (#750) SQS conformance
    // expectations, which are left untouched by this fix.
    private static (int, string) ResolveSqsJson(SigV4ValidationStatus status) => status switch
    {
        SigV4ValidationStatus.InvalidSignature  => (403, "InvalidSignatureException"),
        SigV4ValidationStatus.ClockSkewTooLarge => (403, "InvalidSignatureException"),
        SigV4ValidationStatus.Expired           => (403, "InvalidSignatureException"),
        SigV4ValidationStatus.UnknownAccessKey  => (403, "UnrecognizedClientException"),
        _                                       => (403, "IncompleteSignatureException"),
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
