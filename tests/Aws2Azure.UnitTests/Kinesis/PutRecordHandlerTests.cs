using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;
using Aws2Azure.TestSupport.Kinesis;
using static Aws2Azure.TestSupport.Http.TestHttpContext;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class PutRecordHandlerTests
{
    [Fact]
    public async Task HandleAsync_sends_decoded_payload_to_partition_and_returns_shape()
    {
        var context = CreateContext();
        var sender = new FakeAmqpSender();
        var metadataCache = new FakeMetadataCache((_, namespaceFqdn, eventHubName, _) =>
        {
            Assert.Equal("myns.servicebus.windows.net", namespaceFqdn);
            Assert.Equal("orders-eh", eventHubName);
            return ValueTask.FromResult(new EventHubDescription(
                4,
                ["0", "1", "2", "3"],
                7,
                new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero)));
        });
        var requestBody = "{" + "\"StreamName\":\"orders\",\"Data\":\"aGVsbG8=\",\"PartitionKey\":\"customer-1\"}";

        await PutRecordHandler.HandleAsync(
            context,
            NewParseResult(requestBody),
            NewCredentials(),
            metadataCache,
            sender,
            CancellationToken.None);

        var expectedPartition = PutRecordHandler.ComputePartitionIndex("customer-1", 4);
        Assert.Equal("orders-eh/Partitions/" + expectedPartition, sender.EntityPath);
        Assert.Equal("myns.servicebus.windows.net", sender.NamespaceFqdn);
        Assert.Equal("hello", Encoding.UTF8.GetString(sender.BodyBytes!));
        Assert.Equal("customer-1", sender.Annotations!["x-opt-partition-key"]);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = ReadJson(context);
        Assert.Equal(PutRecordHandler.FormatShardId(expectedPartition), document.RootElement.GetProperty("ShardId").GetString());
        Assert.Equal("NONE", document.RootElement.GetProperty("EncryptionType").GetString());
        Assert.True(long.TryParse(document.RootElement.GetProperty("SequenceNumber").GetString(), out var sequenceNumber));
        Assert.True(sequenceNumber > 0);
    }

    [Fact]
    public async Task HandleAsync_routes_same_partition_key_to_same_partition()
    {
        var sender = new FakeAmqpSender();
        var metadataCache = new FakeMetadataCache((_, _, _, _) => ValueTask.FromResult(
            new EventHubDescription(8, ["0", "1", "2", "3", "4", "5", "6", "7"], 7, DateTimeOffset.UtcNow)));

        var first = CreateContext();
        await PutRecordHandler.HandleAsync(
            first,
            NewParseResult("{" + "\"StreamName\":\"orders\",\"Data\":\"YQ==\",\"PartitionKey\":\"stable-key\"}"),
            NewCredentials(),
            metadataCache,
            sender,
            CancellationToken.None);
        var firstPath = sender.EntityPath;

        var second = CreateContext();
        await PutRecordHandler.HandleAsync(
            second,
            NewParseResult("{" + "\"StreamName\":\"orders\",\"Data\":\"Yg==\",\"PartitionKey\":\"stable-key\"}"),
            NewCredentials(),
            metadataCache,
            sender,
            CancellationToken.None);

        Assert.Equal(firstPath, sender.EntityPath);
    }

    [Theory]
    [InlineData("{}", "One of StreamName or StreamARN is required.")]
    [InlineData("{\"StreamName\":\"orders\"}", "Data is required.")]
    [InlineData("{\"StreamName\":\"orders\",\"Data\":\"YQ==\"}", "PartitionKey is required.")]
    [InlineData("{\"StreamName\":\"orders\",\"Data\":\"not-base64\",\"PartitionKey\":\"pk\"}", "Data must be valid base64.")]
    public async Task HandleAsync_returns_validation_error_for_invalid_requests(string body, string expectedMessage)
    {
        var context = CreateContext();
        var sender = new FakeAmqpSender();

        await PutRecordHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            new FakeMetadataCache((_, _, _, _) => ValueTask.FromResult(new EventHubDescription(1, ["0"], 1, DateTimeOffset.UtcNow))),
            sender,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var responseBody = ReadBody(context);
        Assert.Contains("ValidationException", responseBody);
        Assert.Contains(expectedMessage, responseBody);
        Assert.Null(sender.BodyBytes);
    }

    [Fact]
    public async Task HandleAsync_rejects_payloads_larger_than_one_mebibyte()
    {
        var context = CreateContext();
        var sender = new FakeAmqpSender();
        var bytes = new byte[1_048_577];
        var body = JsonSerializer.Serialize(new
        {
            StreamName = "orders",
            Data = Convert.ToBase64String(bytes),
            PartitionKey = "pk",
        });

        await PutRecordHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            new FakeMetadataCache((_, _, _, _) => ValueTask.FromResult(new EventHubDescription(1, ["0"], 1, DateTimeOffset.UtcNow))),
            sender,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
        Assert.Contains("1048576", ReadBody(context));
        Assert.Null(sender.BodyBytes);
    }

    [Fact]
    public async Task HandleAsync_maps_management_not_found_to_resource_not_found()
    {
        var context = CreateContext();

        await PutRecordHandler.HandleAsync(
            context,
            NewParseResult("{" + "\"StreamName\":\"orders\",\"Data\":\"YQ==\",\"PartitionKey\":\"pk\"}"),
            NewCredentials(),
            new FakeMetadataCache((_, _, _, _) => throw new EventHubsManagementException(HttpStatusCode.NotFound, null)),
            new FakeAmqpSender(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_amqp_auth_failures_to_access_denied()
    {
        var context = CreateContext();

        await PutRecordHandler.HandleAsync(
            context,
            NewParseResult("{" + "\"StreamName\":\"orders\",\"Data\":\"YQ==\",\"PartitionKey\":\"pk\"}"),
            NewCredentials(),
            new FakeMetadataCache((_, _, _, _) => ValueTask.FromResult(new EventHubDescription(1, ["0"], 1, DateTimeOffset.UtcNow))),
            new FakeAmqpSender((_, _, _, _, _, _) => throw new EventHubsAmqpException("denied", new InvalidOperationException(), EventHubsAmqpFailureKind.Auth)),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("AccessDeniedException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_amqp_throttling_to_provisioned_throughput_exceeded()
    {
        var context = CreateContext();

        await PutRecordHandler.HandleAsync(
            context,
            NewParseResult("{" + "\"StreamName\":\"orders\",\"Data\":\"YQ==\",\"PartitionKey\":\"pk\"}"),
            NewCredentials(),
            new FakeMetadataCache((_, _, _, _) => ValueTask.FromResult(new EventHubDescription(1, ["0"], 1, DateTimeOffset.UtcNow))),
            new FakeAmqpSender((_, _, _, _, _, _) => throw new EventHubsAmqpException("busy", new InvalidOperationException(), EventHubsAmqpFailureKind.Throttled)),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ProvisionedThroughputExceededException", ReadBody(context));
    }

    private static EventHubsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
        Streams = new Dictionary<string, KinesisStreamSettings>
        {
            ["orders"] = new KinesisStreamSettings { EventHubName = "orders-eh" },
        },
    };

    private static KinesisParseResult NewParseResult(string body)
        => new(KinesisOperation.PutRecord, "Kinesis_20131202.PutRecord", Encoding.UTF8.GetBytes(body), null);


    private static JsonDocument ReadJson(HttpContext context)
        => JsonDocument.Parse(ReadBody(context));


    private sealed class FakeAmqpSender(Func<EventHubsCredentials, string, string, ReadOnlyMemory<byte>, IReadOnlyDictionary<string, object>?, CancellationToken, Task>? handler = null)
        : IEventHubsAmqpSender
    {
        public string? NamespaceFqdn { get; private set; }
        public string? EntityPath { get; private set; }
        public byte[]? BodyBytes { get; private set; }
        public Dictionary<string, object>? Annotations { get; private set; }

        public async Task SendAsync(EventHubsCredentials credentials, string namespaceFqdn, string entityPath, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations, CancellationToken cancellationToken)
        {
            NamespaceFqdn = namespaceFqdn;
            EntityPath = entityPath;
            BodyBytes = body.ToArray();
            Annotations = annotations is null ? null : annotations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
            if (handler is not null)
            {
                await handler(credentials, namespaceFqdn, entityPath, body, annotations, cancellationToken);
            }
        }

        public async Task<EventHubsBatchSendResult> SendBatchAsync(
            EventHubsCredentials credentials,
            string namespaceFqdn,
            string entityPath,
            IReadOnlyList<(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations)> messages,
            CancellationToken cancellationToken)
        {
            if (messages.Count == 0)
            {
                return new EventHubsBatchSendResult([]);
            }

            await SendAsync(credentials, namespaceFqdn, entityPath, messages[0].body, messages[0].annotations, cancellationToken);
            return new EventHubsBatchSendResult([new EventHubsBatchSendOutcome(true, null, null)]);
        }
    }
}
