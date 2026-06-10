using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Conformance.S3;

namespace Aws2Azure.Conformance.DynamoDb;

/// <summary>
/// One proxy-side DynamoDB error scenario: a recipe that crafts a signed
/// request the proxy must reject before any Cosmos call — either during the
/// SigV4 stage or, for these cases, the AWS-JSON wire-protocol parser
/// (unknown <c>X-Amz-Target</c> operation, non-JSON body) — paired with the
/// AWS-contract outcome (HTTP status + error <c>__type</c> short code) that
/// real DynamoDB documents for that rejection. The outcomes are derived from
/// the AWS DynamoDB API contract, not the proxy's own output, so the
/// assertions are an independent oracle.
/// </summary>
public sealed record DynamoDbErrorCase(
    string Name,
    int ExpectedStatus,
    string ExpectedCode,
    string Target,
    string Body)
{
    public HttpRequestMessage BuildRequest(string accessKey, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(Body);
        var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("http://dynamodb.us-east-1.amazonaws.com/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-amz-json-1.0");
        request.Headers.TryAddWithoutValidation("X-Amz-Target", Target);

        // Sign with a valid key/secret and the dynamodb service so the request
        // clears SigV4; the rejection we assert comes from the wire-protocol
        // parser. X-Amz-Target is part of the signed-header set because the proxy
        // (faithfully to real DynamoDB, where SDKs always sign it) enforces it
        // via RequiredSignedHeaders — an unsigned target would be rejected with
        // SignatureDoesNotMatch before the parser runs.
        ConformanceSigV4Signer.SignHeader(
            request, bytes, accessKey, secret, service: "dynamodb",
            extraSignedHeaders: new[] { "x-amz-target" });
        return request;
    }
}

/// <summary>
/// The DynamoDB proxy-side error matrix. Every case rejects before any Cosmos
/// call — in the SigV4 stage or the AWS-JSON wire-protocol parser — so the
/// whole matrix runs offline on every PR and exercises the JSON-envelope
/// canonicalizer end-to-end against a real JSON-protocol service module.
/// </summary>
public static class DynamoDbErrorMatrix
{
    public static IReadOnlyList<DynamoDbErrorCase> Cases { get; } = new[]
    {
        // A syntactically valid, validly-signed request whose X-Amz-Target names
        // an operation DynamoDB does not expose. The AWS-JSON (coral) frontend
        // rejects it before dispatch with HTTP 400
        // com.amazon.coral.service#UnknownOperationException — the proxy must do
        // the same, byte-for-byte on the dispatch surface SDKs switch on.
        new DynamoDbErrorCase(
            "unknown-operation",
            400,
            "UnknownOperationException",
            Target: "DynamoDB_20120810.NotARealOperation",
            Body: "{}"),

        // A validly-signed request for a real operation whose body is not a JSON
        // object. The AWS-JSON frontend rejects the malformed payload with HTTP
        // 400 com.amazon.coral.service#SerializationException before the
        // operation handler runs.
        new DynamoDbErrorCase(
            "serialization-exception",
            400,
            "SerializationException",
            Target: "DynamoDB_20120810.GetItem",
            Body: "this is not json"),
    };
}
