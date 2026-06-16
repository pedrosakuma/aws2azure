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

public sealed class GetShardIteratorHandlerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("TRIM_HORIZON", null, null, ShardIteratorType.TrimHorizon, null)]
    [InlineData("LATEST", null, null, ShardIteratorType.Latest, null)]
    [InlineData("AT_SEQUENCE_NUMBER", "123", null, ShardIteratorType.AtSequenceNumber, "123")]
    [InlineData("AFTER_SEQUENCE_NUMBER", "124", null, ShardIteratorType.AfterSequenceNumber, "124")]
    public async Task HandleAsync_encodes_expected_token_payload(
        string iteratorType,
        string? startingSequenceNumber,
        double? timestamp,
        ShardIteratorType expectedType,
        string? expectedPosition)
    {
        var context = CreateContext();
        var codecFactory = NewCodecFactory();

        await GetShardIteratorHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody(iteratorType, startingSequenceNumber, timestamp)),
            NewCredentials(),
            NewMetadataCache(),
            codecFactory,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var token = DecodeResponseToken(context, codecFactory);
        Assert.Equal("orders", token.Stream);
        Assert.Equal("shardId-000000000001", token.Shard);
        Assert.Equal(expectedType, token.Type);
        Assert.Equal(expectedPosition, token.Position);
        Assert.Equal(FixedNow.ToUnixTimeSeconds(), token.IssuedAtUnixSeconds);
    }

    [Fact]
    public async Task HandleAsync_formats_at_timestamp_as_iso8601_utc()
    {
        var context = CreateContext();
        var codecFactory = NewCodecFactory();

        await GetShardIteratorHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody("AT_TIMESTAMP", null, 1_735_689_600.25d)),
            NewCredentials(),
            NewMetadataCache(),
            codecFactory,
            CancellationToken.None);

        var token = DecodeResponseToken(context, codecFactory);
        Assert.Equal(ShardIteratorType.AtTimestamp, token.Type);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(1_735_689_600_250d).UtcDateTime.ToString("O"), token.Position);
    }

    [Fact]
    public async Task HandleAsync_resolves_stream_arn_only_requests()
    {
        var context = CreateContext();
        var codecFactory = NewCodecFactory();
        var body = "{" +
            "\"StreamARN\":\"arn:aws:kinesis:azure:myns:stream/orders\"," +
            "\"ShardId\":\"shardId-000000000001\"," +
            "\"ShardIteratorType\":\"LATEST\"}";

        await GetShardIteratorHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            NewMetadataCache(),
            codecFactory,
            CancellationToken.None);

        var token = DecodeResponseToken(context, codecFactory);
        Assert.Equal("orders", token.Stream);
    }

    [Theory]
    [InlineData("{}", "One of StreamName or StreamARN is required.")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardIteratorType\":\"LATEST\"}", "ShardId is required.")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardId\":\"shardId-000000000001\",\"ShardIteratorType\":\"AT_SEQUENCE_NUMBER\"}", "StartingSequenceNumber is required")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardId\":\"shardId-000000000001\",\"ShardIteratorType\":\"AT_TIMESTAMP\"}", "Timestamp is required")]
    [InlineData("{\"StreamName\":\"orders\",\"ShardId\":\"shardId-000000000001\",\"ShardIteratorType\":\"LATEST\",\"StartingSequenceNumber\":\"1\"}", "StartingSequenceNumber is not supported")]
    public async Task HandleAsync_rejects_invalid_requests(string body, string expectedMessage)
    {
        var context = CreateContext();

        await GetShardIteratorHandler.HandleAsync(
            context,
            NewParseResult(body),
            NewCredentials(),
            NewMetadataCache(),
            NewCodecFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
        Assert.Contains(expectedMessage, ReadBody(context));
    }

    [Theory]
    [InlineData(1e18)]
    [InlineData(-1e18)]
    public async Task HandleAsync_rejects_out_of_range_at_timestamp_values(double timestamp)
    {
        var context = CreateContext();

        await GetShardIteratorHandler.HandleAsync(
            context,
            NewParseResult(BuildRequestBody("AT_TIMESTAMP", null, timestamp)),
            NewCredentials(),
            NewMetadataCache(),
            NewCodecFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
        Assert.Contains("Timestamp is invalid for AT_TIMESTAMP.", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_unknown_shards()
    {
        var context = CreateContext();

        await GetShardIteratorHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"ShardId\":\"shardId-000000000010\",\"ShardIteratorType\":\"LATEST\"}"),
            NewCredentials(),
            NewMetadataCache(),
            NewCodecFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_missing_streams_from_management_api()
    {
        var context = CreateContext();

        await GetShardIteratorHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"ShardId\":\"shardId-000000000001\",\"ShardIteratorType\":\"LATEST\"}"),
            NewCredentials(),
            new FakeMetadataCache((_, _, _, _) => throw new EventHubsManagementException(HttpStatusCode.NotFound, null)),
            NewCodecFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ResourceNotFoundException", ReadBody(context));
    }

    private static string BuildRequestBody(string iteratorType, string? startingSequenceNumber, double? timestamp)
    {
        var body = "{" +
            "\"StreamName\":\"orders\"," +
            "\"ShardId\":\"shardId-000000000001\"," +
            "\"ShardIteratorType\":\"" + iteratorType + "\"";
        if (startingSequenceNumber is not null)
        {
            body += ",\"StartingSequenceNumber\":\"" + startingSequenceNumber + "\"";
        }

        if (timestamp.HasValue)
        {
            body += ",\"Timestamp\":" + timestamp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return body + "}";
    }

    private static EventHubsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
        ShardIteratorSigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
        Streams = new Dictionary<string, KinesisStreamSettings>
        {
            ["orders"] = new KinesisStreamSettings { EventHubName = "orders-eh" },
        },
    };

    private static FakeMetadataCache NewMetadataCache()
        => new((_, namespaceFqdn, eventHubName, _) =>
        {
            Assert.Equal("myns.servicebus.windows.net", namespaceFqdn);
            Assert.Equal("orders-eh", eventHubName);
            return ValueTask.FromResult(new EventHubDescription(3, ["0", "1", "2"], 7, FixedNow));
        });

    private static ShardIteratorTokenCodecFactory NewCodecFactory()
        => new(NullLogger<ShardIteratorTokenCodecFactory>.Instance, new ManualTimeProvider(FixedNow));

    private static KinesisParseResult NewParseResult(string body)
        => new(KinesisOperation.GetShardIterator, "Kinesis_20131202.GetShardIterator", Encoding.UTF8.GetBytes(body), null);


    private static ShardIteratorToken DecodeResponseToken(HttpContext context, ShardIteratorTokenCodecFactory codecFactory)
    {
        using var document = JsonDocument.Parse(ReadBody(context));
        var encoded = document.RootElement.GetProperty("ShardIterator").GetString();
        var codec = codecFactory.Create(NewCredentials());
        Assert.True(codec.TryDecode(encoded!, out var token, out var error));
        Assert.Equal(ShardIteratorVerifyError.None, error);
        return token;
    }


    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
