using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.IntegrationTests.Fixtures;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Helper that builds a SigV4-signed DynamoDB POST mirroring what
/// boto3 / AWSSDK.NET produce: <c>application/x-amz-json-1.0</c> body,
/// <c>X-Amz-Target = DynamoDB_20120810.&lt;Op&gt;</c>, region <c>us-east-1</c>.
///
/// Test-only: the proxy never builds outbound DynamoDB requests, so this
/// helper only needs to match what AWS SDKs emit on the wire.
/// </summary>
internal static class DynamoDbRequestBuilder
{
    public static HttpRequestMessage Build(string operation, string jsonBody,
        string accessKey, string secret, Uri baseAddress)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonBody);
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "/"))
        {
            Content = new ByteArrayContent(bytes),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-amz-json-1.0");
        req.Headers.TryAddWithoutValidation("X-Amz-Target", "DynamoDB_20120810." + operation);

        TestSigV4Signer.SignHeader(req, bytes, accessKey, secret,
            region: "us-east-1", service: "dynamodb",
            extraSignedHeaders: new[] { "x-amz-target" });
        return req;
    }
}
