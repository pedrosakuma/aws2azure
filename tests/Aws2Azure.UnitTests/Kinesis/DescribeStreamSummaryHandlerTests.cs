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

public sealed class DescribeStreamSummaryHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_expected_summary_shape()
    {
        var context = CreateContext();

        await DescribeStreamSummaryHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamARN\":\"arn:aws:kinesis:azure:myns:stream/orders\"}"),
            NewCredentials(),
            new FakeManagementClient((_, _, _, _) => ValueTask.FromResult(NewEventHubDescription())),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var document = ReadJson(context);
        var summary = document.RootElement.GetProperty("StreamDescriptionSummary");
        Assert.Equal("orders", summary.GetProperty("StreamName").GetString());
        Assert.Equal("arn:aws:kinesis:azure:myns:stream/orders", summary.GetProperty("StreamARN").GetString());
        Assert.Equal("ACTIVE", summary.GetProperty("StreamStatus").GetString());
        Assert.Equal(72, summary.GetProperty("RetentionPeriodHours").GetInt32());
        Assert.Equal(4, summary.GetProperty("OpenShardCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("ConsumerCount").GetInt32());
        Assert.Equal("NONE", summary.GetProperty("EncryptionType").GetString());
    }

    [Fact]
    public async Task HandleAsync_returns_validation_error_when_stream_name_and_arn_are_missing()
    {
        var context = CreateContext();

        await DescribeStreamSummaryHandler.HandleAsync(
            context,
            NewParseResult("{}"),
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

        await DescribeStreamSummaryHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\"}"),
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

        await DescribeStreamSummaryHandler.HandleAsync(
            context,
            NewParseResult("{\"StreamName\":\"orders\"}"),
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
    };

    private static EventHubDescription NewEventHubDescription()
        => new(4, ["0", "1", "2", "3"], 3, new DateTimeOffset(2024, 6, 20, 8, 45, 0, TimeSpan.Zero));

    private static KinesisParseResult NewParseResult(string body)
        => new(KinesisOperation.DescribeStreamSummary, "Kinesis_20131202.DescribeStreamSummary", Encoding.UTF8.GetBytes(body), null);


    private static JsonDocument ReadJson(HttpContext context)
        => JsonDocument.Parse(ReadBody(context));

}
