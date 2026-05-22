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

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class PutRecordsHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_success_entries_in_request_order()
    {
        var context = NewContext();
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

        var requestBody = JsonSerializer.Serialize(new
        {
            StreamName = "orders",
            Records = new object[]
            {
                new { Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("one")), PartitionKey = "o" },
                new { Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("two")), PartitionKey = "a" },
                new { Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("three")), PartitionKey = "e" },
            },
        });

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult(requestBody),
            NewCredentials(),
            metadataCache,
            sender,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(3, sender.BatchCalls.Count);
        Assert.Contains(sender.BatchCalls, c => c.EntityPath == "orders-eh/Partitions/0");
        Assert.Contains(sender.BatchCalls, c => c.EntityPath == "orders-eh/Partitions/1");
        Assert.Contains(sender.BatchCalls, c => c.EntityPath == "orders-eh/Partitions/2");

        using var document = ReadJson(context);
        Assert.Equal(0, document.RootElement.GetProperty("FailedRecordCount").GetInt32());
        Assert.Equal("NONE", document.RootElement.GetProperty("EncryptionType").GetString());
        var records = document.RootElement.GetProperty("Records");
        Assert.Equal(3, records.GetArrayLength());

        AssertSuccess(records[0], PutRecordHandler.FormatShardId(0));
        AssertSuccess(records[1], PutRecordHandler.FormatShardId(1));
        AssertSuccess(records[2], PutRecordHandler.FormatShardId(2));

        var firstSequence = long.Parse(records[0].GetProperty("SequenceNumber").GetString()!);
        var secondSequence = long.Parse(records[1].GetProperty("SequenceNumber").GetString()!);
        var thirdSequence = long.Parse(records[2].GetProperty("SequenceNumber").GetString()!);
        Assert.True(firstSequence < secondSequence);
        Assert.True(secondSequence < thirdSequence);
    }

    [Fact]
    public async Task HandleAsync_returns_per_record_validation_failures_without_failing_request()
    {
        var context = NewContext();
        var sender = new FakeAmqpSender();
        var oversizePayload = Convert.ToBase64String(new byte[PutRecordCommon.MaxDataBytes + 1]);
        var requestBody = JsonSerializer.Serialize(new
        {
            StreamName = "orders",
            Records = new object[]
            {
                new { Data = "YQ==", PartitionKey = "o" },
                new { Data = "not-base64", PartitionKey = "a" },
                new { Data = "Yg==", PartitionKey = (string?)null },
                new { Data = oversizePayload, PartitionKey = "e" },
                new { Data = "Yw==", PartitionKey = "b" },
            },
        });

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult(requestBody),
            NewCredentials(),
            NewMetadataCache(),
            sender,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(2, sender.BatchCalls.Count);

        using var document = ReadJson(context);
        Assert.Equal(3, document.RootElement.GetProperty("FailedRecordCount").GetInt32());
        var records = document.RootElement.GetProperty("Records");
        Assert.Equal(5, records.GetArrayLength());
        AssertSuccess(records[0], PutRecordHandler.FormatShardId(0));
        AssertFailure(records[1], "ValidationException", "valid base64");
        AssertFailure(records[2], "ValidationException", "PartitionKey is required");
        AssertFailure(records[3], "ValidationException", PutRecordCommon.MaxDataBytes.ToString());
        AssertSuccess(records[4], PutRecordHandler.FormatShardId(3));
    }

    [Theory]
    [InlineData("{\"StreamName\":\"orders\"}", "Records is required.")]
    [InlineData("{\"StreamName\":\"orders\",\"Records\":[]}", "Records must contain at least one entry.")]
    [InlineData("{\"Records\":[{\"Data\":\"YQ==\",\"PartitionKey\":\"pk\"}]}", "One of StreamName or StreamARN is required.")]
    public async Task HandleAsync_returns_whole_request_validation_errors(string body, string expectedMessage)
    {
        var context = NewContext();

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            NewMetadataCache(),
            new FakeAmqpSender(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
        Assert.Contains(expectedMessage, ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_batches_larger_than_five_hundred_records()
    {
        var records = Enumerable.Range(0, PutRecordCommon.MaxRecordsPerRequest + 1)
            .Select(i => $"{{\"Data\":\"YQ==\",\"PartitionKey\":\"pk-{i}\"}}")
            .ToArray();
        var body = "{\"StreamName\":\"orders\",\"Records\":[" + string.Join(',', records) + "]}";
        var context = NewContext();

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            NewMetadataCache(),
            new FakeAmqpSender(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
        Assert.Contains("500", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_payloads_larger_than_five_mebibytes()
    {
        var oversizedData = new string('A', PutRecordCommon.MaxRequestPayloadBytes);
        var body = "{\"StreamName\":\"orders\",\"Records\":[{\"Data\":\"" + oversizedData + "\",\"PartitionKey\":\"pk\"}]}";
        var context = NewContext();

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            NewMetadataCache(),
            new FakeAmqpSender(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
        Assert.Contains(PutRecordCommon.MaxRequestPayloadBytes.ToString(), ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_marks_only_failed_partition_group_entries_when_batch_send_fails()
    {
        var context = NewContext();
        var sender = new FakeAmqpSender((_, _, entityPath, _, _) =>
        {
            if (entityPath.EndsWith("/1", StringComparison.Ordinal))
            {
                throw new EventHubsAmqpException("failed", new InvalidOperationException(), EventHubsAmqpFailureKind.Unknown);
            }

            return Task.CompletedTask;
        });
        var requestBody = JsonSerializer.Serialize(new
        {
            StreamName = "orders",
            Records = new object[]
            {
                new { Data = "YQ==", PartitionKey = "a" },
                new { Data = "Yg==", PartitionKey = "d" },
                new { Data = "Yw==", PartitionKey = "o" },
            },
        });

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult(requestBody),
            NewCredentials(),
            NewMetadataCache(),
            sender,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        using var document = ReadJson(context);
        Assert.Equal(2, document.RootElement.GetProperty("FailedRecordCount").GetInt32());
        var records = document.RootElement.GetProperty("Records");
        Assert.Equal(3, records.GetArrayLength());
        AssertFailure(records[0], "InternalFailure", "AMQP send failed");
        AssertFailure(records[1], "InternalFailure", "AMQP send failed");
        AssertSuccess(records[2], PutRecordHandler.FormatShardId(0));
    }

    [Fact]
    public async Task HandleAsync_routes_partition_keys_deterministically()
    {
        var context = NewContext();
        var sender = new FakeAmqpSender();
        var body = "{\"StreamName\":\"orders\",\"Records\":[{\"Data\":\"YQ==\",\"PartitionKey\":\"abc\"}]}";

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            NewMetadataCache(),
            sender,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Single(sender.BatchCalls);
        Assert.Equal("orders-eh/Partitions/2", sender.BatchCalls[0].EntityPath);

        using var document = ReadJson(context);
        var record = document.RootElement.GetProperty("Records")[0];
        AssertSuccess(record, "shardId-000000000002");
    }

    [Fact]
    public async Task HandleAsync_maps_management_not_found_to_resource_not_found()
    {
        var context = NewContext();

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"Records\":[{\"Data\":\"YQ==\",\"PartitionKey\":\"pk\"}] }"),
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
        var context = NewContext();

        await PutRecordsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"Records\":[{\"Data\":\"YQ==\",\"PartitionKey\":\"pk\"}] }"),
            NewCredentials(),
            NewMetadataCache(),
            new FakeAmqpSender((_, _, _, _, _) => throw new EventHubsAmqpException("denied", new InvalidOperationException(), EventHubsAmqpFailureKind.Auth)),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("AccessDeniedException", ReadBody(context));
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

    private static FakeMetadataCache NewMetadataCache()
        => new((_, _, _, _) => ValueTask.FromResult(new EventHubDescription(4, ["0", "1", "2", "3"], 7, DateTimeOffset.UtcNow)));

    private static KinesisParseResult NewParseResult(string body)
        => new(KinesisOperation.PutRecords, "Kinesis_20131202.PutRecords", Encoding.UTF8.GetBytes(body), null);

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    private static JsonDocument ReadJson(HttpContext context)
        => JsonDocument.Parse(ReadBody(context));

    private static void AssertSuccess(JsonElement record, string expectedShardId)
    {
        Assert.Equal(expectedShardId, record.GetProperty("ShardId").GetString());
        Assert.True(long.TryParse(record.GetProperty("SequenceNumber").GetString(), out var sequenceNumber));
        Assert.True(sequenceNumber > 0);
        Assert.False(record.TryGetProperty("ErrorCode", out _));
        Assert.False(record.TryGetProperty("ErrorMessage", out _));
    }

    private static void AssertFailure(JsonElement record, string expectedCode, string expectedMessageFragment)
    {
        Assert.Equal(expectedCode, record.GetProperty("ErrorCode").GetString());
        Assert.Contains(expectedMessageFragment, record.GetProperty("ErrorMessage").GetString());
        Assert.False(record.TryGetProperty("ShardId", out _));
        Assert.False(record.TryGetProperty("SequenceNumber", out _));
    }

    private sealed class FakeMetadataCache(Func<EventHubsCredentials, string, string, CancellationToken, ValueTask<EventHubDescription>> handler)
        : IEventHubMetadataCache
    {
        public ValueTask<EventHubDescription> GetEventHubAsync(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName, CancellationToken cancellationToken)
            => handler(credentials, namespaceFqdn, eventHubName, cancellationToken);
    }

    private sealed class FakeAmqpSender(Func<EventHubsCredentials, string, string, IReadOnlyList<(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations)>, CancellationToken, Task>? batchHandler = null)
        : IEventHubsAmqpSender
    {
        public List<BatchCall> BatchCalls { get; } = [];

        public Task SendAsync(EventHubsCredentials credentials, string namespaceFqdn, string entityPath, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations, CancellationToken cancellationToken)
            => SendBatchAsync(credentials, namespaceFqdn, entityPath, [(body, annotations)], cancellationToken);

        public async Task SendBatchAsync(
            EventHubsCredentials credentials,
            string namespaceFqdn,
            string entityPath,
            IReadOnlyList<(ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations)> messages,
            CancellationToken cancellationToken)
        {
            BatchCalls.Add(new BatchCall(
                namespaceFqdn,
                entityPath,
                messages.Select(m => new BatchMessage(
                    m.body.ToArray(),
                    m.annotations is null
                        ? null
                        : m.annotations.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal))).ToArray()));

            if (batchHandler is not null)
            {
                await batchHandler(credentials, namespaceFqdn, entityPath, messages, cancellationToken);
            }
        }
    }

    private sealed record BatchCall(string NamespaceFqdn, string EntityPath, IReadOnlyList<BatchMessage> Messages);
    private sealed record BatchMessage(byte[] Body, IReadOnlyDictionary<string, object>? Annotations);
}
