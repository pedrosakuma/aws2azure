using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

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

        var firstPageContext = NewContext();
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

        var secondPageContext = NewContext();
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
    public async Task HandleAsync_rejects_tampered_next_token()
    {
        var credentials = NewCredentials();
        var factory = NewFactory();
        var codec = factory.Create(credentials);
        var token = codec.Encode(new ListShardsCursor("orders", "shardId-000000000001", FixedNow.ToUnixTimeSeconds()));
        var tampered = FlipChar(token, token.Length - 1);
        var context = NewContext();

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
        var context = NewContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_maps_not_found_to_resource_not_found_exception()
    {
        var context = NewContext();

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
        var context = NewContext();

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
    public async Task HandleAsync_rejects_unsupported_filter_types()
    {
        var context = NewContext();

        await ListShardsHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\",\"ShardFilter\":{\"Type\":\"AFTER_SHARD_ID\"}}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            NewFactory(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("ValidationException", ReadBody(context));
    }

    private static EventHubsCredentials NewCredentials() => new()
    {
        Namespace = "myns",
        SasKeyName = "Root",
        SasKey = "secret",
        ShardIteratorSigningKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
    };

    private static EventHubDescription NewEventHubDescription()
        => new(4, ["0", "1", "2", "3"], 3, new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));

    private static ListShardsCursorCodecFactory NewFactory()
        => new(NullLogger<ListShardsCursorCodecFactory>.Instance, new ManualTimeProvider(FixedNow));

    private static KinesisParseResult NewParseResult(string body)
        => new(KinesisOperation.ListShards, "Kinesis_20131202.ListShards", Encoding.UTF8.GetBytes(body), null);

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

    private static string FlipChar(string value, int index)
    {
        var chars = value.ToCharArray();
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    private sealed class FakeManagementClient(Func<EventHubsCredentials, string, string, CancellationToken, ValueTask<EventHubDescription>> handler)
        : IEventHubsManagementClient
    {
        public ValueTask<EventHubDescription> GetEventHubAsync(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName, CancellationToken cancellationToken)
            => handler(credentials, namespaceFqdn, eventHubName, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
