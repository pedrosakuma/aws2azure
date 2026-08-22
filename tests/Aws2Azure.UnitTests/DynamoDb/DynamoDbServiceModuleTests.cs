using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.DynamoDb;
using Aws2Azure.Modules.DynamoDb.Operations;
using Aws2Azure.Modules.DynamoDb.Errors;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aws2Azure.UnitTests.DynamoDb;

public class DynamoDbServiceModuleTests
{
    [Theory]
    [InlineData("dynamodb.us-east-1.amazonaws.com", true)]
    [InlineData("dynamodb-fips.us-gov-west-1.amazonaws.com", true)]
    [InlineData("DYNAMODB.eu-west-1.amazonaws.com", true)]
    [InlineData("dynamodb", true)]
    [InlineData("sqs.us-east-1.amazonaws.com", false)]
    [InlineData("s3.amazonaws.com", false)]
    [InlineData("", false)]
    public void MatchesHost_recognises_dynamodb_hostnames(string host, bool expected)
    {
        var module = NewModule();
        Assert.Equal(expected, module.MatchesHost(host));
    }

    [Fact]
    public async Task HandleAsync_returns_not_implemented_for_recognised_op()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.UpdateTable", body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status501NotImplemented, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("\"__type\"", body);
        Assert.Contains("com.amazonaws.dynamodb.v20120810#InternalServerError", body);
        Assert.Contains("UpdateTable", body);
    }

    [Fact]
    public async Task HandleAsync_emits_unknown_op_in_dynamodb_envelope()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.NotARealOp", body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        // Protocol-level errors use the coral namespace.
        Assert.Contains("com.amazon.coral.service#UnknownOperationException", body);
    }

    // Issue #854: real AWS DynamoDB emits SerializationException and
    // UnknownOperationException with NO "message" field on the frontend
    // parser-rejection path.
    [Fact]
    public async Task HandleAsync_unknown_op_envelope_omits_message_field()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.NotARealOp", body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        using var doc = System.Text.Json.JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal(
            "com.amazon.coral.service#UnknownOperationException",
            doc.RootElement.GetProperty("__type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task HandleAsync_serialization_exception_envelope_omits_message_field()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.GetItem", body: "this is not json");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        using var doc = System.Text.Json.JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal(
            "com.amazon.coral.service#SerializationException",
            doc.RootElement.GetProperty("__type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
    }

    // Regression guard for the PR #863 code-review finding: the parser also
    // returns InvalidAction, MissingActionException, RequestEntityTooLarge
    // through the same parsed.Error branch — they must retain their message
    // field on the wire.
    [Fact]
    public async Task HandleAsync_non_post_returns_invalid_action_with_message_field()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.PutItem", body: "{}");
        ctx.Request.Method = HttpMethods.Get;

        await module.HandleAsync(ctx);

        using var doc = System.Text.Json.JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal(
            "com.amazon.coral.service#InvalidAction",
            doc.RootElement.GetProperty("__type").GetString());
        Assert.True(doc.RootElement.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrEmpty(msg.GetString()));
    }

    [Fact]
    public async Task HandleAsync_missing_target_returns_missing_action_with_message_field()
    {
        var module = NewModule();
        var ctx = NewCtx("does-not-matter", body: "{}");
        ctx.Request.Headers.Remove("X-Amz-Target");

        await module.HandleAsync(ctx);

        using var doc = System.Text.Json.JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal(
            "com.amazon.coral.service#MissingActionException",
            doc.RootElement.GetProperty("__type").GetString());
        Assert.True(doc.RootElement.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrEmpty(msg.GetString()));
    }

    [Fact]
    public async Task HandleAsync_oversized_body_returns_request_entity_too_large_with_message_field()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.PutItem", body: "{}");
        ctx.Request.ContentLength = long.MaxValue;

        await module.HandleAsync(ctx);

        using var doc = System.Text.Json.JsonDocument.Parse(ReadBody(ctx));
        Assert.Equal(
            "com.amazon.coral.service#RequestEntityTooLarge",
            doc.RootElement.GetProperty("__type").GetString());
        Assert.True(doc.RootElement.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrEmpty(msg.GetString()));
    }

    [Fact]
    public async Task HandleAsync_requires_aws_access_key_in_items()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.PutItem", body: "{}");

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("MissingAuthenticationTokenException", ReadBody(ctx));
    }

    [Fact]
    public async Task HandleAsync_returns_access_denied_when_no_cosmos_credentials()
    {
        var module = NewModule(includeCosmos: false);
        var ctx = NewCtx("DynamoDB_20120810.PutItem", body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("AccessDeniedException", ReadBody(ctx));
    }


    [Fact]
    public void ComputeAwsCrc32Decimal_matches_known_values()
    {
        Assert.Equal("2745614147", CosmosOpsShared.ComputeAwsCrc32Decimal("{}"u8));
        Assert.Equal("4247354728", CosmosOpsShared.ComputeAwsCrc32Decimal("""{"Item":{"Id":{"S":"123"}}}"""u8));
    }

    [Fact]
    public async Task HandleAsync_errors_emit_crc32_header()
    {
        var module = NewModule();
        var ctx = NewCtx("DynamoDB_20120810.NotARealOp", body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        var body = ReadBody(ctx);
        Assert.True(ctx.Response.Headers.TryGetValue("x-amz-crc32", out var crc));
        Assert.Equal(DynamoDbErrorResponse.ComputeAwsCrc32Decimal(body), crc.ToString());
    }

    [Theory]
    // Core item/table ops were always labelled.
    [InlineData("GetItem")]
    [InlineData("TransactWriteItems")]
    // Regression: these are handled by HandleAsync but previously drifted out
    // of the metrics allowlist, so they were mislabelled "unknown".
    [InlineData("DescribeTimeToLive")]
    [InlineData("UpdateTimeToLive")]
    [InlineData("TagResource")]
    [InlineData("UntagResource")]
    [InlineData("ListTagsOfResource")]
    public void ExtractOperationName_labels_every_parseable_op_by_name(string op)
    {
        var module = NewModule();
        var ctx = NewCtx($"DynamoDB_20120810.{op}", body: "{}");

        Assert.Equal(op, module.ExtractOperationName(ctx));
    }

    [Theory]
    // Unrecognised op name.
    [InlineData("DynamoDB_20120810.NotARealOp")]
    // Malformed target with extra dots: extraction must agree with the wire
    // parser (which rejects it) and NOT label it by the first dotted segment.
    [InlineData("DynamoDB_20120810.DescribeStream.Extra")]
    [InlineData("DynamoDB_20120810.GetItem.tampered")]
    // Wrong/absent service prefix.
    [InlineData("SomethingElse.GetItem")]
    [InlineData("")]
    public void ExtractOperationName_collapses_unrecognised_target_to_unknown(string target)
    {
        var module = NewModule();
        var ctx = NewCtx(target, body: "{}");

        Assert.Equal("unknown", module.ExtractOperationName(ctx));
    }

    private static DynamoDbServiceModule NewModule(bool includeCosmos = true)
    {
        var http = new AzureHttpClient(new NoopHandler(), ownsHandler: false);
        var config = new ProxyConfig
        {
            Credentials = {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIAEXAMPLE",
                    AwsSecretAccessKey = "secret",
                    Azure = includeCosmos
                        ? new AzureCredentials
                          {
                              Cosmos = new CosmosCredentials
                              {
                                  Endpoint = "https://test.documents.azure.com:443/",
                                  PrimaryKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                                  DatabaseName = "main",
                              }
                          }
                        : new AzureCredentials(),
                },
            },
        };
        var resolver = new StaticCredentialResolver(config);
        return new DynamoDbServiceModule(http, resolver, new CapabilityMatrix("dynamodb", System.Array.Empty<OperationCapability>()));
    }

    private static DefaultHttpContext NewCtx(string target, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/x-amz-json-1.0";
        ctx.Request.Headers["X-Amz-Target"] = target;
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>
/// Envelope-shape checks for <see cref="DynamoDbErrorResponse"/>. Real AWS
/// DynamoDB returns <c>{"__type":"…#SerializationException"}</c> and
/// <c>{"__type":"com.amazon.coral.service#UnknownOperationException"}</c>
/// with no <c>message</c> field on frontend-rejection paths — see issue #854.
/// </summary>
public class DynamoDbErrorResponseEnvelopeTests
{
    [Theory]
    [InlineData("SerializationException", false)]
    [InlineData("SerializationException", true)]
    [InlineData("UnknownOperationException", true)]
    public async Task Frontend_rejection_writer_omits_message_field(string code, bool isProtocolLevel)
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = "req-x" };
        ctx.Response.Body = new MemoryStream();

        await DynamoDbErrorResponse.WriteAsync(
            ctx, 400, code, "diagnostic text",
            isProtocolLevel: isProtocolLevel, omitMessage: true);

        var body = ReadBody(ctx);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var expectedPrefix = isProtocolLevel
            ? DynamoDbErrorResponse.CoralTypePrefix
            : DynamoDbErrorResponse.ServiceTypePrefix;
        Assert.Equal(expectedPrefix + code, doc.RootElement.GetProperty("__type").GetString());
        Assert.False(
            doc.RootElement.TryGetProperty("message", out _),
            $"expected no 'message' property for {code}");

        // CRC32 header must still match the emitted body byte-for-byte.
        Assert.True(ctx.Response.Headers.TryGetValue("x-amz-crc32", out var crc));
        Assert.Equal(DynamoDbErrorResponse.ComputeAwsCrc32Decimal(body), crc.ToString());
    }

    // Guardrail: handler-level SerializationException (post-parse) still
    // carries its message field — real AWS only strips it on the frontend
    // rejection path. Callers control the behaviour via the omitMessage flag.
    [Theory]
    [InlineData("SerializationException")]
    [InlineData("ValidationException")]
    [InlineData("ResourceNotFoundException")]
    [InlineData("MissingAuthenticationTokenException")]
    [InlineData("AccessDeniedException")]
    public async Task Default_writer_preserves_message_field(string code)
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = "req-y" };
        ctx.Response.Body = new MemoryStream();

        await DynamoDbErrorResponse.WriteAsync(ctx, 400, code, "diagnostic text");

        var body = ReadBody(ctx);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("diagnostic text", doc.RootElement.GetProperty("message").GetString());
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
    }
}
