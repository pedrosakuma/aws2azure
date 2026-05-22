using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Kinesis;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aws2Azure.UnitTests.Kinesis;

public class KinesisServiceModuleTests
{
    [Theory]
    [InlineData("kinesis.us-east-1.amazonaws.com", true)]
    [InlineData("kinesis-fips.us-gov-west-1.amazonaws.com", true)]
    [InlineData("KINESIS.eu-west-1.amazonaws.com", true)]
    [InlineData("kinesis", true)]
    [InlineData("sqs.us-east-1.amazonaws.com", false)]
    [InlineData("dynamodb.us-east-1.amazonaws.com", false)]
    [InlineData("s3.amazonaws.com", false)]
    [InlineData("", false)]
    public void MatchesHost_recognises_kinesis_hostnames(string host, bool expected)
    {
        var module = NewModule();
        Assert.Equal(expected, module.MatchesHost(host));
    }

    [Theory]
    [InlineData("Kinesis_20131202.PutRecords")]
    [InlineData("Kinesis_20131202.GetRecords")]
    [InlineData("Kinesis_20131202.GetShardIterator")]
    public async Task HandleAsync_returns_501_for_remaining_stub_ops(string target)
    {
        var module = NewModule();
        var ctx = NewCtx(target, body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status501NotImplemented, ctx.Response.StatusCode);
        var body = ReadBody(ctx);
        Assert.Contains("\"__type\"", body);
        Assert.Contains("InternalFailure", body);
        Assert.Contains(target.Substring("Kinesis_20131202.".Length), body);
        Assert.Equal("application/x-amz-json-1.1", ctx.Response.ContentType);
    }

    [Fact]
    public async Task HandleAsync_dispatches_describe_stream_to_real_handler()
    {
        var module = NewModule();
        var ctx = NewCtx("Kinesis_20131202.DescribeStream", body: "{\"StreamName\":\"orders\"}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("StreamDescription", ReadBody(ctx));
    }

    [Fact]
    public async Task HandleAsync_dispatches_put_record_to_real_handler()
    {
        var module = NewModule();
        var ctx = NewCtx("Kinesis_20131202.PutRecord", body: "{\"StreamName\":\"orders\",\"Data\":\"aGVsbG8=\",\"PartitionKey\":\"pk-1\"}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("ShardId", ReadBody(ctx));
    }

    [Fact]
    public async Task HandleAsync_emits_unknown_op_for_unmapped_target()
    {
        var module = NewModule();
        var ctx = NewCtx("Kinesis_20131202.NotARealOp", body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.Contains("UnknownOperationException", ReadBody(ctx));
    }

    [Fact]
    public async Task HandleAsync_requires_aws_access_key_in_items()
    {
        var module = NewModule();
        var ctx = NewCtx("Kinesis_20131202.PutRecord", body: "{}");

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("MissingAuthenticationTokenException", ReadBody(ctx));
    }

    [Fact]
    public async Task HandleAsync_returns_access_denied_when_no_event_hubs_credentials()
    {
        var module = NewModule(includeEventHubs: false);
        var ctx = NewCtx("Kinesis_20131202.PutRecord", body: "{}");
        ctx.Items["aws2azure.accessKeyId"] = "AKIAEXAMPLE";

        await module.HandleAsync(ctx);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("AccessDeniedException", ReadBody(ctx));
    }

    [Fact]
    public void Module_requires_x_amz_target_in_signed_headers()
    {
        var module = NewModule();
        Assert.Contains("x-amz-target", module.RequiredSignedHeaders);
        Assert.True(module.RequiresSigV4);
        Assert.True(module.BuffersRequestBodyForSigV4);
    }

    private static KinesisServiceModule NewModule(bool includeEventHubs = true)
    {
        var config = new ProxyConfig
        {
            Credentials = {
                new CredentialEntry
                {
                    AwsAccessKeyId = "AKIAEXAMPLE",
                    AwsSecretAccessKey = "secret",
                    Azure = includeEventHubs
                        ? new AzureCredentials
                          {
                              EventHubs = new EventHubsCredentials
                              {
                                  Namespace = "myns",
                                  SasKeyName = "RootManageSharedAccessKey",
                                  SasKey = "ZGVhZGJlZWZkZWFkYmVlZmRlYWRiZWVmZGVhZGJlZWY=",
                                  ShardIteratorSigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
                              },
                          }
                        : new AzureCredentials(),
                },
            },
        };
        var resolver = new StaticCredentialResolver(config);
        return new KinesisServiceModule(
            resolver,
            new FakeManagementClient(),
            new FakeMetadataCache(),
            new FakeAmqpSender(),
            new ListShardsCursorCodecFactory(NullLogger<ListShardsCursorCodecFactory>.Instance),
            new CapabilityMatrix("kinesis", Array.Empty<OperationCapability>()));
    }

    private static DefaultHttpContext NewCtx(string target, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/x-amz-json-1.1";
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

    private sealed class FakeManagementClient : IEventHubsManagementClient
    {
        public ValueTask<EventHubDescription> GetEventHubAsync(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName, System.Threading.CancellationToken cancellationToken)
            => ValueTask.FromResult(new EventHubDescription(
                1,
                ["0"],
                1,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    private sealed class FakeMetadataCache : IEventHubMetadataCache
    {
        public ValueTask<EventHubDescription> GetEventHubAsync(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName, System.Threading.CancellationToken cancellationToken)
            => ValueTask.FromResult(new EventHubDescription(
                1,
                ["0"],
                1,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    private sealed class FakeAmqpSender : IEventHubsAmqpSender
    {
        public Task SendAsync(EventHubsCredentials credentials, string namespaceFqdn, string entityPath, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations, System.Threading.CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
