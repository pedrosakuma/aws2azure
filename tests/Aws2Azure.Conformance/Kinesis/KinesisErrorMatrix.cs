using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.Kinesis;

/// <summary>
/// One proxy-side Kinesis error scenario: a recipe that crafts a signed request
/// the proxy must reject before any Event Hubs call — either during the SigV4
/// stage or the AWS-JSON wire-protocol parser (unknown <c>X-Amz-Target</c>
/// operation, non-JSON body) — paired with the AWS-contract outcome (HTTP status
/// + error <c>__type</c> short code) that real Kinesis documents for that
/// rejection. The outcomes are derived from the AWS Kinesis API contract, not
/// the proxy's own output, so the assertions are an independent oracle.
///
/// <para>Kinesis speaks AWS JSON 1.1 (<c>application/x-amz-json-1.1</c>) and,
/// unlike DynamoDB's coral frontend, renders <c>__type</c> as the bare error
/// code with no <c>com.amazonaws…#</c> namespace prefix — so this matrix also
/// exercises the canonicalizer's prefix-free <c>__type</c> path.</para>
/// </summary>
public sealed record KinesisErrorCase(
    string Name,
    string Operation,
    int ExpectedStatus,
    string ExpectedCode,
    string Target,
    string Body,
    string? AccessKeyOverride = null,
    string? SecretOverride = null) : IConformanceCase
{
    /// <inheritdoc />
    public ConformanceCaseExpectation Expected =>
        ConformanceCaseExpectation.Error(
            ExpectedStatus,
            ExpectedCode,
            "Proxy-side Kinesis rejection asserted from the AWS JSON 1.1 contract.");

    public HttpRequestMessage BuildRequest(ConformanceCaseContext context)
    {
        var accessKey = context.AccessKeyId;
        var secret = context.SecretAccessKey;
        var sessionToken = context.SessionToken;
        var signKey = AccessKeyOverride ?? accessKey;
        var signSecret = SecretOverride ?? secret;
        var bytes = Encoding.UTF8.GetBytes(Body);
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(KinesisErrorMatrix.ResolveBaseAddress(context), "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-amz-json-1.1");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", Target);

        // Sign with the (possibly overridden) key/secret and the kinesis
        // service. Parser-stage cases sign with the fixture's valid key/secret
        // so the request clears SigV4 and the rejection comes from the
        // wire-protocol parser. Auth-stage cases deliberately sign with a wrong
        // secret (→ InvalidSignature) or an unconfigured key (→ UnknownAccessKey)
        // so the registry's SigV4 stage rejects first. X-Amz-Target is part of
        // the signed-header set because the proxy (faithfully to real Kinesis,
        // where SDKs always sign it) enforces it via RequiredSignedHeaders.
        ConformanceSigV4Signer.SignHeader(
            request, bytes, signKey, signSecret, service: "kinesis",
            extraSignedHeaders: new[] { "x-amz-target" },
            sessionToken: sessionToken);
        return request;
    }

    /// <inheritdoc />
    public ValueTask<ConformanceExecutionPlan> CreatePlanAsync(
        ConformanceCaseContext context,
        CancellationToken cancellationToken = default)
        => new(new ConformanceExecutionPlan(
            [new ConformanceRequestStep(
                Name,
                _ => BuildRequest(context))]));
}

/// <summary>
/// The Kinesis proxy-side error matrix. Every case rejects before any Event Hubs
/// call — in the SigV4 stage or the AWS-JSON wire-protocol parser — so the whole
/// matrix runs offline on every PR. It is the second JSON-protocol service (after
/// DynamoDB) to exercise the JSON-envelope canonicalizer end-to-end, and the
/// first to validate the prefix-free <c>__type</c> form and the issue #241 auth
/// vocabulary through a module that uses the default (un-overridden)
/// <c>EmitSigV4FailureAsync</c>.
/// </summary>
public static class KinesisErrorMatrix
{
    private static readonly Uri DefaultBaseAddress = new("http://kinesis.us-east-1.amazonaws.com/");

    public static IReadOnlyList<KinesisErrorCase> Cases { get; } = new[]
    {
        // A syntactically valid, validly-signed request whose X-Amz-Target names
        // an operation Kinesis does not expose. The AWS-JSON frontend rejects it
        // before dispatch with HTTP 400 UnknownOperationException — the proxy
        // must do the same on the dispatch surface SDKs switch on.
        new KinesisErrorCase(
            "unknown-operation",
            "kinesis:UnknownOperationException",
            400,
            "UnknownOperationException",
            Target: "Kinesis_20131202.NotARealOperation",
            Body: "{}"),

        // A validly-signed request for a real operation whose body is not a JSON
        // object. The AWS-JSON frontend rejects the malformed payload with HTTP
        // 400 SerializationException before the operation handler runs.
        new KinesisErrorCase(
            "serialization-exception",
            "kinesis:SerializationException",
            400,
            "SerializationException",
            Target: "Kinesis_20131202.DescribeStreamSummary",
            Body: "this is not json"),

        // SigV4 auth-stage rejections. Like DynamoDB and unlike S3 (REST-XML, 403
        // + S3 codes), AWS-JSON Kinesis answers SigV4 failures with HTTP 403 and
        // the JSON exception vocabulary emitted by the shared AWS front door
        // (corrected after a real-AWS SQS-JSON capture, workflow run 31347507212,
        // showed 403; independently corroborated by the Kinesis/DynamoDB
        // CommonErrors API reference, which documents UnrecognizedClientException
        // and IncompleteSignature at 403 for this same shared front door).
        // These pin the issue #241 fix on a module that uses the default
        // EmitSigV4FailureAsync (no SQS-style per-request override). Tier-1-only:
        // LocalStack can't be the oracle for these (it ignores signatures), so
        // the expected outcome is taken from the AWS JSON-protocol contract.

        // A well-formed request signed with the WRONG secret: the proxy
        // recomputes the signature from the configured secret, mismatches, and
        // must answer InvalidSignatureException / 403.
        new KinesisErrorCase(
            "invalid-signature",
            "kinesis:InvalidSignatureException",
            403,
            "InvalidSignatureException",
            Target: "Kinesis_20131202.DescribeStreamSummary",
            Body: "{\"StreamName\":\"s\"}",
            SecretOverride: "this-is-not-the-configured-secret-000000000"),

        // A well-formed request signed with an access key the proxy doesn't
        // know: the credential lookup fails before signature verification, and
        // the faithful JSON answer is UnrecognizedClientException / 403.
        new KinesisErrorCase(
            "unrecognized-client",
            "kinesis:UnrecognizedClientException",
            403,
            "UnrecognizedClientException",
            Target: "Kinesis_20131202.DescribeStreamSummary",
            Body: "{\"StreamName\":\"s\"}",
            AccessKeyOverride: "AKIACONFORMANCEUNKNOWN",
            SecretOverride: "any-secret-since-the-key-is-unknown-00000000"),
    };

    internal static Uri ResolveBaseAddress(ConformanceCaseContext context)
        => context.BaseAddress ?? DefaultBaseAddress;
}
