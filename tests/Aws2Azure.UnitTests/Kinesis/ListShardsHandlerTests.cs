using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Aws2Azure.TestSupport.Kinesis;
using static Aws2Azure.TestSupport.Http.TestHttpContext;

namespace Aws2Azure.UnitTests.Kinesis;

public sealed class ListShardsHandlerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_emits_next_token_and_round_trips_it()
    {
        var credentials = NewCredentials();
        var factory = NewFactory();
        var managementClient = new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription()));

        var firstPageContext = CreateContext();
        await ListShardsHandler.HandleAsync(
            firstPageContext,
            NewParseResult("{\"StreamName\":\"orders\",\"MaxResults\":2,\"ShardFilter\":{\"Type\":\"AT_LATEST\"}}"),
            credentials,
            managementClient,
            factory,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, firstPageContext.Response.StatusCode);
        using var firstPage = ReadJson(firstPageContext);
        var firstShards = firstPage.RootElement.GetProperty("Shards");
        Assert.Equal(2, firstShards.GetArrayLength());
        Assert.Equal("shardId-000000000000", firstShards[0].GetProperty("ShardId").GetString());
        Assert.Equal("shardId-000000000001", firstShards[1].GetProperty("ShardId").GetString());
        var nextToken = firstPage.RootElement.GetProperty("NextToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(nextToken));

        var secondPageContext = CreateContext();
        await ListShardsHandler.HandleAsync(
            secondPageContext,
            NewParseResult("{\"NextToken\":\"" + nextToken + "\"}"),
            credentials,
            managementClient,
            factory,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, secondPageContext.Response.StatusCode);
        using var secondPage = ReadJson(secondPageContext);
        var secondShards = secondPage.RootElement.GetProperty("Shards");
        Assert.Equal(2, secondShards.GetArrayLength());
        Assert.Equal("shardId-000000000002", secondShards[0].GetProperty("ShardId").GetString());
        Assert.Equal("shardId-000000000003", secondShards[1].GetProperty("ShardId").GetString());
        Assert.False(secondPage.RootElement.TryGetProperty("NextToken", out _));
    }

    [Fact]
    public async Task HandleAsync_rejects_stream_name_with_next_token()
    {
        var credentials = NewCredentials();
        var factory = NewFactory();
        var codec = factory.Create(credentials);
        var token = codec.Encode(new ListShardsCursor("orders", "shardId-000000000001", FixedNow.ToUnixTimeSeconds()));
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"NextToken\":\"" + token + "\"}"),
            credentials,
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            factory,
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", responseBody);
        Assert.Contains("StreamName cannot be used together with NextToken.", responseBody);
    }

    [Fact]
    public async Task HandleAsync_rejects_tampered_next_token()
    {
        var credentials = NewCredentials();
        var factory = NewFactory();
        var codec = factory.Create(credentials);
        var token = codec.Encode(new ListShardsCursor("orders", "shardId-000000000001", FixedNow.ToUnixTimeSeconds()));
        var tampered = FlipChar(token, token.Length - 1);
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"NextToken\":\"" + tampered + "\"}"),
            credentials,
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            factory,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ExpiredNextTokenException", ReadBody(context));
    }

    [Fact]
    public void Cursor_codec_rejects_future_dated_tokens()
    {
        var codec = NewFactory().Create(NewCredentials());
        var token = codec.Encode(new ListShardsCursor("orders", "shardId-000000000001", FixedNow.AddSeconds(1).ToUnixTimeSeconds()));

        Assert.False(codec.TryDecode(token, out _, out var error));
        Assert.Equal(ListShardsCursorVerifyError.Expired, error);
    }

    [Fact]
    public async Task HandleAsync_returns_validation_error_when_stream_name_is_missing()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_supports_stream_arn_without_stream_name()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamARN\":\"arn:aws:kinesis:azure:myns:stream/orders\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(
            ["shardId-000000000000", "shardId-000000000001", "shardId-000000000002", "shardId-000000000003"],
            ReadShardIds(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_mismatched_stream_name_and_stream_arn()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"StreamARN\":\"arn:aws:kinesis:azure:myns:stream/payments\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", responseBody);
        Assert.Contains("StreamName and StreamARN must refer to the same stream.", responseBody);
    }

    [Fact]
    public async Task HandleAsync_rejects_stream_arn_with_mismatched_identity_prefix()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamARN\":\"arn:aws:kinesis:azure:otherns:stream/orders\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", responseBody);
        Assert.Contains("StreamARN must refer to the configured stream identity.", responseBody);
    }

    [Fact]
    public async Task HandleAsync_supports_matching_stream_creation_timestamp()
    {
        var createdAt = new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero).AddMilliseconds(123);
        var timestamp = KinesisMetadataSupport.ToUnixTimeSeconds(createdAt).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"StreamCreationTimestamp\":" + timestamp + "}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription(createdAt: createdAt))),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(
            ["shardId-000000000000", "shardId-000000000001", "shardId-000000000002", "shardId-000000000003"],
            ReadShardIds(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_mismatched_stream_creation_timestamp()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"StreamCreationTimestamp\":1718873101}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", responseBody);
        Assert.Contains("StreamCreationTimestamp", responseBody);
    }

    [Fact]
    public async Task HandleAsync_rejects_stream_creation_timestamp_with_next_token()
    {
        var credentials = NewCredentials();
        var factory = NewFactory();
        var codec = factory.Create(credentials);
        var token = codec.Encode(new ListShardsCursor("orders", "shardId-000000000001", FixedNow.ToUnixTimeSeconds()));
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"NextToken\":\"" + token + "\",\"StreamCreationTimestamp\":1718873100}"),
            credentials,
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            factory,
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", responseBody);
        Assert.Contains("StreamCreationTimestamp cannot be used together with NextToken.", responseBody);
    }

    [Fact]
    public async Task HandleAsync_rejects_shard_filter_with_next_token()
    {
        var credentials = NewCredentials();
        var factory = NewFactory();
        var codec = factory.Create(credentials);
        var token = codec.Encode(new ListShardsCursor("orders", "shardId-000000000001", FixedNow.ToUnixTimeSeconds()));
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"NextToken\":\"" + token + "\",\"ShardFilter\":{\"Type\":\"AT_LATEST\"}}"),
            credentials,
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            factory,
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", responseBody);
        Assert.Contains("ShardFilter cannot be used together with NextToken.", responseBody);
    }

    [Fact]
    public async Task HandleAsync_caps_max_results_to_aws_page_limit()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"MaxResults\":1001}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription(partitionCount: 1001))),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var response = ReadJson(context);
        Assert.Equal(1000, response.RootElement.GetProperty("Shards").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(response.RootElement.GetProperty("NextToken").GetString()));
    }

    [Fact]
    public async Task HandleAsync_maps_not_found_to_resource_not_found_exception()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => throw new EventHubsManagementException(HttpStatusCode.NotFound, null)),
            NewFactory(),
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

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => throw new EventHubsManagementException(statusCode, null)),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("AccessDeniedException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_supports_after_shard_id_filter()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"AFTER_SHARD_ID\",\"ShardId\":\"shardId-000000000001\"}}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(
            ["shardId-000000000002", "shardId-000000000003"],
            ReadShardIds(context));
    }

    [Fact]
    public async Task HandleAsync_after_shard_id_filter_returns_empty_page_after_last_shard()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"AFTER_SHARD_ID\",\"ShardId\":\"shardId-000000000003\"}}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(ReadShardIds(context));
    }

    [Fact]
    public async Task HandleAsync_supports_from_timestamp_filter_before_stream_creation()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"FROM_TIMESTAMP\",\"Timestamp\":0}}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(
            ["shardId-000000000000", "shardId-000000000001", "shardId-000000000002", "shardId-000000000003"],
            ReadShardIds(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_non_positive_max_results()
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"MaxResults\":0}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", responseBody);
        Assert.Contains("Limit/MaxResults must be greater than zero.", responseBody);
    }

    [Theory]
    [InlineData("{\"StreamName\":\"orders\",\"MaxResults\":10001}", "Limit/MaxResults must be less than or equal to 10000.")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardFilter\":{}}", "ShardFilter.Type is required when ShardFilter is specified.")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"AFTER_SHARD_ID\"}}", "ShardFilter.ShardId is required for AFTER_SHARD_ID.")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"AT_TRIM_HORIZON\"}}", "AT_TRIM_HORIZON")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"AT_TIMESTAMP\",\"Timestamp\":1718873100}}", "AT_TIMESTAMP")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"FROM_TIMESTAMP\",\"Timestamp\":1.7976931348623157E+308}}", "ShardFilter.Timestamp is invalid for FROM_TIMESTAMP.")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"FROM_TIMESTAMP\",\"ShardId\":\"shardId-000000000001\",\"Timestamp\":1718873100}}", "ShardFilter.ShardId is not supported for FROM_TIMESTAMP.")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"UNSUPPORTED\"}}", "UNSUPPORTED")]
    public async Task HandleAsync_rejects_invalid_filter_shapes(string body, string expectedMessage)
    {
        var context = CreateContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        var responseBody = ReadBody(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidArgumentException", responseBody);
        Assert.Contains(expectedMessage, responseBody);
    }

    private static EventHubsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
        ShardIteratorSigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
    };

    private static EventHubDescription NewEventHubDescription(int partitionCount = 4, DateTimeOffset? createdAt = null)
    {
        var partitionIds = new string[partitionCount];
        for (var i = 0; i < partitionIds.Length; i++)
        {
            partitionIds[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new EventHubDescription(partitionCount, partitionIds, 3, createdAt ?? new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));
    }

    private static ListShardsCursorCodecFactory NewFactory()
        => new(NullLogger<ListShardsCursorCodecFactory>.Instance, new ManualTimeProvider(FixedNow));

    private static KinesisParseResult NewParseResult(string body)
        => new(KinesisOperation.ListShards, "Kinesis_20131202.ListShards", Encoding.UTF8.GetBytes(body), null);


    private static JsonDocument ReadJson(HttpContext context)
        => JsonDocument.Parse(ReadBody(context));

    private static string[] ReadShardIds(HttpContext context)
    {
        using var document = ReadJson(context);
        var shards = document.RootElement.GetProperty("Shards");
        var shardIds = new string[shards.GetArrayLength()];
        for (var i = 0; i < shardIds.Length; i++)
        {
            shardIds[i] = shards[i].GetProperty("ShardId").GetString()!;
        }

        return shardIds;
    }

    private static string FlipChar(string value, int index)
    {
        var chars = value.ToCharArray();
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }


    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
