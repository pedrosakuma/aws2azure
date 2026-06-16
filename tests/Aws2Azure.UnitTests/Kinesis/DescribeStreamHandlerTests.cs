using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;
using Aws2Azure.TestSupport.Kinesis;
using static Aws2Azure.TestSupport.Http.TestHttpContext;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class DescribeStreamHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_expected_stream_description()
    {
        var context = CreateContext();
        var credentials = NewCredentials();
        var handler = new FakeManagementClient((_, namespaceFqdn, eventHubName, _) =>
        {
            Assert.Equal("myns.servicebus.windows.net", namespaceFqdn);
            Assert.Equal("orders-eh", eventHubName);
            return ValueTask.FromResult(new EventHubDescription(
                4,
                ["0", "1", "2", "3"],
                3,
                new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero)));
        });

        await DescribeStreamHandler.HandleAsync(
            context,
            NewParseResult(KinesisOperation.DescribeStream, "{" + "\"StreamName\":\"orders\"}"),
            credentials,
            handler,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = ReadJson(context);
        var description = document.RootElement.GetProperty("StreamDescription");
        Assert.Equal("orders", description.GetProperty("StreamName").GetString());
        Assert.Equal("arn:aws:kinesis:azure:myns:stream/orders", description.GetProperty("StreamARN").GetString());
        Assert.Equal("ACTIVE", description.GetProperty("StreamStatus").GetString());
        Assert.Equal(72, description.GetProperty("RetentionPeriodHours").GetInt32());
        Assert.Equal("NONE", description.GetProperty("EncryptionType").GetString());
        Assert.False(description.GetProperty("HasMoreShards").GetBoolean());
        Assert.Equal(1718873100d, description.GetProperty("StreamCreationTimestamp").GetDouble());
        var enhanced = description.GetProperty("EnhancedMonitoring");
        Assert.Single(enhanced.EnumerateArray());
        Assert.Equal(4, description.GetProperty("Shards").GetArrayLength());
        var firstShard = description.GetProperty("Shards")[0];
        Assert.Equal("shardId-000000000000", firstShard.GetProperty("ShardId").GetString());
        Assert.Equal("0", firstShard.GetProperty("SequenceNumberRange").GetProperty("StartingSequenceNumber").GetString());
    }

    [Fact]
    public async Task HandleAsync_paginates_from_exclusive_start_shard_id()
    {
        var context = CreateContext();

        await DescribeStreamHandler.HandleAsync(
            context,
            NewParseResult(KinesisOperation.DescribeStream, "{" + "\"StreamName\":\"orders\",\"ExclusiveStartShardId\":\"shardId-000000000000\",\"Limit\":2}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            CancellationToken.None);

        using var document = ReadJson(context);
        var description = document.RootElement.GetProperty("StreamDescription");
        var shards = description.GetProperty("Shards");
        Assert.Equal(2, shards.GetArrayLength());
        Assert.Equal("shardId-000000000001", shards[0].GetProperty("ShardId").GetString());
        Assert.Equal("shardId-000000000002", shards[1].GetProperty("ShardId").GetString());
        Assert.True(description.GetProperty("HasMoreShards").GetBoolean());
    }

    [Fact]
    public async Task HandleAsync_returns_validation_error_when_stream_name_and_arn_are_missing()
    {
        var context = CreateContext();

        await DescribeStreamHandler.HandleAsync(
            context,
            NewParseResult(KinesisOperation.DescribeStream, "{}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_not_found_to_resource_not_found_exception()
    {
        var context = CreateContext();

        await DescribeStreamHandler.HandleAsync(
            context,
            NewParseResult(KinesisOperation.DescribeStream, "{" + "\"StreamName\":\"orders\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => throw new EventHubsManagementException(HttpStatusCode.NotFound, null)),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadBody(context));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task HandleAsync_maps_auth_failures_to_access_denied(HttpStatusCode statusCode)
    {
        var context = CreateContext();

        await DescribeStreamHandler.HandleAsync(
            context,
            NewParseResult(KinesisOperation.DescribeStream, "{" + "\"StreamName\":\"orders\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => throw new EventHubsManagementException(statusCode, null)),
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

    private static EventHubDescription NewEventHubDescription()
        => new(4, ["0", "1", "2", "3"], 3, new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));

    private static KinesisParseResult NewParseResult(KinesisOperation operation, string body)
        => new(operation, "Kinesis_20131202." + operation, Encoding.UTF8.GetBytes(body), null);


    private static JsonDocument ReadJson(HttpContext context)
        => JsonDocument.Parse(ReadBody(context));

}
