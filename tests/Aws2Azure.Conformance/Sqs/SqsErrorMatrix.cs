using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.Sqs;

/// <summary>The SQS wire protocol a conformance case exercises.</summary>
public enum SqsCaseProtocol
{
    /// <summary>Legacy AWS Query protocol: form-url-encoded body, no
    /// <c>X-Amz-Target</c>, XML <c>&lt;ErrorResponse&gt;</c> error envelope at
    /// HTTP 403 for SigV4 failures.</summary>
    Query,

    /// <summary>Modern AWS JSON 1.0 protocol: JSON body, <c>X-Amz-Target:
    /// AmazonSQS.&lt;Op&gt;</c>, <c>{"__type":"…#Code"}</c> error envelope at HTTP
    /// 400 for SigV4 failures.</summary>
    Json,
}

/// <summary>
/// One proxy-side SQS error scenario. SQS is the proxy's only dual-protocol
/// module, and its SigV4 auth-error vocabulary is protocol-negotiated per request
/// (issue #241): legacy Query callers get the XML <c>&lt;ErrorResponse&gt;</c>
/// envelope with the S3-style codes at HTTP 403, while modern AWS-JSON callers get
/// the <c>{"__type":"…#Code"}</c> envelope with <c>InvalidSignatureException</c>/
/// <c>UnrecognizedClientException</c> at HTTP 400. Each case crafts a signed
/// request the proxy must reject before any Service Bus call — in the SigV4 stage
/// or the wire-protocol parser — paired with the AWS-contract outcome (HTTP status
/// + short error code) for that rejection. The outcomes are derived from the AWS
/// SQS API contract, not the proxy's own output, so the assertions are an
/// independent oracle.
/// </summary>
public sealed record SqsErrorCase(
    string Name,
    SqsCaseProtocol Protocol,
    int ExpectedStatus,
    string ExpectedCode,
    string Body,
    string? Target = null,
    string? AccessKeyOverride = null,
    string? SecretOverride = null)
{
    public HttpRequestMessage BuildRequest(string accessKey, string secret)
    {
        var signKey = AccessKeyOverride ?? accessKey;
        var signSecret = SecretOverride ?? secret;
        var bytes = Encoding.UTF8.GetBytes(Body);
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("http://sqs.us-east-1.amazonaws.com/"))
        {
            Content = new ByteArrayContent(bytes),
        };

        string[]? extraSigned = null;
        if (Protocol == SqsCaseProtocol.Json)
        {
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/x-amz-json-1.0");
            request.Headers.TryAddWithoutValidation("X-Amz-Target", Target);
            // Modern SQS JSON SDKs sign X-Amz-Target; mirror that (the module
            // does not enforce RequiredSignedHeaders, but signing it is the
            // faithful client shape and keeps the signature stable).
            extraSigned = new[] { "x-amz-target" };
        }
        else
        {
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        }

        // Parser-stage cases sign with the fixture's valid key/secret so the
        // request clears SigV4 and the rejection comes from the wire-protocol
        // parser. Auth-stage cases deliberately sign with a wrong secret
        // (→ InvalidSignature) or an unconfigured key (→ UnknownAccessKey) so the
        // registry's SigV4 stage rejects first — and the SQS module's
        // per-request EmitSigV4FailureAsync override renders the failure in the
        // caller's protocol vocabulary.
        ConformanceSigV4Signer.SignHeader(
            request, bytes, signKey, signSecret, service: "sqs",
            extraSignedHeaders: extraSigned);
        return request;
    }
}

/// <summary>
/// The SQS proxy-side error matrix. Every case rejects before any Service Bus
/// call — in the SigV4 stage or the AWS-JSON/Query wire-protocol parser — so the
/// whole matrix runs offline on every PR. SQS is the third JSON-protocol service
/// to exercise the JSON-envelope canonicalizer and the first to exercise the AWS
/// Query <c>&lt;ErrorResponse&gt;</c> XML envelope through the canonicalizer's
/// unwrap path. The Query+JSON auth pairs together pin both branches of the
/// issue #241 SQS <c>EmitSigV4FailureAsync</c> override — the only module with a
/// per-request protocol-negotiated auth vocabulary.
/// </summary>
public static class SqsErrorMatrix
{
    public static IReadOnlyList<SqsErrorCase> Cases { get; } = new[]
    {
        // --- Query (legacy XML) protocol ---

        // A validly-signed Query request naming an action SQS does not expose.
        // The AWS Query frontend answers HTTP 400 with the <ErrorResponse> XML
        // envelope and <Code>InvalidAction</Code>.
        new SqsErrorCase(
            "query-invalid-action",
            SqsCaseProtocol.Query,
            400,
            "InvalidAction",
            Body: "Action=NotARealAction&Version=2012-11-05"),

        // A Query request signed with the WRONG secret. SQS's REST-XML auth
        // vocabulary answers SignatureDoesNotMatch / 403 (unlike the JSON path).
        new SqsErrorCase(
            "query-invalid-signature",
            SqsCaseProtocol.Query,
            403,
            "SignatureDoesNotMatch",
            Body: "Action=ListQueues&Version=2012-11-05",
            SecretOverride: "this-is-not-the-configured-secret-000000000"),

        // A Query request signed with an unknown access key → InvalidClientTokenId
        // / 403, the faithful Query front-door vocabulary (issue #247). S3 is the
        // only XML service that answers InvalidAccessKeyId here.
        new SqsErrorCase(
            "query-unrecognized-client",
            SqsCaseProtocol.Query,
            403,
            "InvalidClientTokenId",
            Body: "Action=ListQueues&Version=2012-11-05",
            AccessKeyOverride: "AKIACONFORMANCEUNKNOWN",
            SecretOverride: "any-secret-since-the-key-is-unknown-00000000"),

        // --- AWS-JSON protocol (same auth failures, different wire shape) ---

        // The JSON twin of query-invalid-signature: the AWS-JSON front door
        // answers InvalidSignatureException / 400 with the {"__type":…} envelope.
        // Together with the Query case above this pins both branches of the SQS
        // EmitSigV4FailureAsync override.
        new SqsErrorCase(
            "json-invalid-signature",
            SqsCaseProtocol.Json,
            400,
            "InvalidSignatureException",
            Body: "{}",
            Target: "AmazonSQS.ListQueues",
            SecretOverride: "this-is-not-the-configured-secret-000000000"),

        // The JSON twin of query-unrecognized-client: UnrecognizedClientException
        // / 400.
        new SqsErrorCase(
            "json-unrecognized-client",
            SqsCaseProtocol.Json,
            400,
            "UnrecognizedClientException",
            Body: "{}",
            Target: "AmazonSQS.ListQueues",
            AccessKeyOverride: "AKIACONFORMANCEUNKNOWN",
            SecretOverride: "any-secret-since-the-key-is-unknown-00000000"),
    };
}
