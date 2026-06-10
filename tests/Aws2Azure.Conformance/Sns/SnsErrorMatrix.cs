using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.Sns;

/// <summary>
/// One proxy-side SNS error scenario. SNS is a single-protocol (legacy AWS
/// Query) service: form-url-encoded requests, <c>text/xml</c> responses, and the
/// AWS <c>&lt;ErrorResponse&gt;&lt;Error&gt;…&lt;/Error&gt;&lt;RequestId/&gt;&lt;/ErrorResponse&gt;</c>
/// envelope (the same wrapper SQS Query uses, unwrapped uniformly by the
/// canonicalizer). It uses the <em>default</em> <c>EmitSigV4FailureAsync</c>, so
/// its SigV4 auth vocabulary is the AWS Query XML one: <c>SignatureDoesNotMatch</c>
/// / <c>InvalidClientTokenId</c> at HTTP 403 (issue #247 — S3 is the only XML
/// service that answers <c>InvalidAccessKeyId</c>). Unlike SQS, SNS enforces
/// <c>RequiredSignedHeaders = ["content-type"]</c>, so the parser-stage case must
/// sign <c>content-type</c> to clear the signed-header check before reaching the
/// wire-protocol parser. Each case is paired with the AWS-contract outcome (HTTP
/// status + short error code) for that rejection — an independent oracle derived
/// from the AWS SNS API, not the proxy's own output.
/// </summary>
public sealed record SnsErrorCase(
    string Name,
    int ExpectedStatus,
    string ExpectedCode,
    string Body,
    string? AccessKeyOverride = null,
    string? SecretOverride = null)
{
    public HttpRequestMessage BuildRequest(string accessKey, string secret)
    {
        var signKey = AccessKeyOverride ?? accessKey;
        var signSecret = SecretOverride ?? secret;
        var bytes = Encoding.UTF8.GetBytes(Body);
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("http://sns.us-east-1.amazonaws.com/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        // SNS enforces RequiredSignedHeaders = ["content-type"]. The proxy only
        // reaches that check for a valid signature (the registry returns on an
        // invalid SigV4 result first), so it is load-bearing for the parser-stage
        // case and harmless-but-faithful for the auth cases.
        ConformanceSigV4Signer.SignHeader(
            request, bytes, signKey, signSecret, service: "sns",
            extraSignedHeaders: new[] { "content-type" });
        return request;
    }
}

/// <summary>
/// The SNS proxy-side error matrix. Every case rejects before any Service Bus /
/// Event Grid call — in the SigV4 stage or the AWS Query wire-protocol parser —
/// so the whole matrix runs offline on every PR. SNS completes the #234
/// "templatize the error matrix across services" checklist (S3, DynamoDB,
/// Kinesis, SQS, SNS) and is the second service (after SQS) to drive the AWS
/// Query <c>&lt;ErrorResponse&gt;</c> XML envelope through the canonicalizer's
/// unwrap path, this time via a module that uses the <em>default</em>
/// <c>EmitSigV4FailureAsync</c> (no per-request override).
/// </summary>
public static class SnsErrorMatrix
{
    public static IReadOnlyList<SnsErrorCase> Cases { get; } = new[]
    {
        // A validly-signed Query request naming an action SNS does not expose.
        // The wire-protocol parser answers HTTP 400 with the <ErrorResponse> XML
        // envelope and <Code>InvalidAction</Code>, before any backend call.
        new SnsErrorCase(
            "sns-invalid-action",
            400,
            "InvalidAction",
            Body: "Action=NotARealAction&Version=2010-03-31"),

        // A Query request signed with the WRONG secret. SNS's REST-XML auth
        // vocabulary (default EmitSigV4FailureAsync) answers
        // SignatureDoesNotMatch / 403.
        new SnsErrorCase(
            "sns-invalid-signature",
            403,
            "SignatureDoesNotMatch",
            Body: "Action=ListTopics&Version=2010-03-31",
            SecretOverride: "this-is-not-the-configured-secret-000000000"),

        // A Query request signed with an unknown access key. The shared AWS Query
        // auth front door answers InvalidClientTokenId / 403 (issue #247) — not
        // S3's bespoke InvalidAccessKeyId. NOTE: oracle is per-AWS docs; pending
        // a real-AWS spot-check (see #247).
        new SnsErrorCase(
            "sns-unrecognized-client",
            403,
            "InvalidClientTokenId",
            Body: "Action=ListTopics&Version=2010-03-31",
            AccessKeyOverride: "AKIACONFORMANCEUNKNOWN",
            SecretOverride: "any-secret-since-the-key-is-unknown-00000000"),
    };
}
