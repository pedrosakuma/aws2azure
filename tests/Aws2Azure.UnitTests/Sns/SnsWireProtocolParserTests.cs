using System.IO;
using System.Text;
using System.Threading;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public class SnsWireProtocolParserTests
{
    [Fact]
    public async Task Get_is_rejected()
    {
        var ctx = NewContext(method: HttpMethods.Get, contentType: "application/x-www-form-urlencoded", body: "Action=ListTopics");

        var result = await SnsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(SnsParseErrorType.InvalidMethod, result.Error!.Type);
        Assert.Equal("InvalidParameter", result.Error.Code);
    }

    [Fact]
    public async Task Missing_action_is_rejected()
    {
        var ctx = NewContext(contentType: "application/x-www-form-urlencoded", body: "Version=2010-03-31");

        var result = await SnsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(SnsParseErrorType.MissingAction, result.Error!.Type);
        Assert.Equal("MissingAction", result.Error.Code);
    }

    [Fact]
    public async Task Unknown_action_maps_to_InvalidAction()
    {
        var ctx = NewContext(contentType: "application/x-www-form-urlencoded", body: "Action=Nope");

        var result = await SnsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(SnsParseErrorType.UnknownOperation, result.Error!.Type);
        Assert.Equal("InvalidAction", result.Error.Code);
        Assert.Equal(SnsOperation.Unknown, result.Operation);
    }

    [Theory]
    [InlineData("CreateTopic", SnsOperation.CreateTopic)]
    [InlineData("DeleteTopic", SnsOperation.DeleteTopic)]
    [InlineData("ListTopics", SnsOperation.ListTopics)]
    [InlineData("Publish", SnsOperation.Publish)]
    [InlineData("PublishBatch", SnsOperation.PublishBatch)]
    [InlineData("Subscribe", SnsOperation.Subscribe)]
    [InlineData("Unsubscribe", SnsOperation.Unsubscribe)]
    [InlineData("ListSubscriptions", SnsOperation.ListSubscriptions)]
    [InlineData("ListSubscriptionsByTopic", SnsOperation.ListSubscriptionsByTopic)]
    [InlineData("GetTopicAttributes", SnsOperation.GetTopicAttributes)]
    [InlineData("SetTopicAttributes", SnsOperation.SetTopicAttributes)]
    public async Task Recognised_actions_parse_with_flat_parameters(string action, SnsOperation expected)
    {
        var ctx = NewContext(
            contentType: "application/x-www-form-urlencoded; charset=utf-8",
            body: $"Action={action}&Version=2010-03-31&MessageAttributes.entry.1.Name=color&MessageAttributes.entry.1.Value.StringValue=blue");

        var result = await SnsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Operation);
        Assert.Equal("2010-03-31", result.Parameters["Version"]);
        Assert.Equal("color", result.Parameters["MessageAttributes.entry.1.Name"]);
        Assert.False(result.Parameters.ContainsKey("Action"));
    }

    [Fact]
    public async Task Body_over_cap_is_rejected()
    {
        var ctx = NewContext(contentType: "application/x-www-form-urlencoded", body: "Action=Publish");
        ctx.Request.ContentLength = SnsWireProtocolParser.MaxBodyBytes + 1L;

        var result = await SnsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(SnsParseErrorType.InvalidRequest, result.Error!.Type);
        Assert.Equal("InvalidParameter", result.Error.Code);
    }

    [Fact]
    public async Task Json_content_type_is_rejected_with_clear_error()
    {
        var ctx = NewContext(contentType: "application/json", body: "{\"Action\":\"Publish\"}");

        var result = await SnsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(SnsParseErrorType.UnsupportedJsonProtocol, result.Error!.Type);
        Assert.Equal("InvalidParameter", result.Error.Code);
        Assert.Contains("JSON protocol is not supported", result.Error.Message);
    }

    [Fact]
    public async Task Non_form_content_type_is_rejected()
    {
        var ctx = NewContext(contentType: "text/plain", body: "Action=Publish");

        var result = await SnsWireProtocolParser.ParseAsync(ctx, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(SnsParseErrorType.InvalidContentType, result.Error!.Type);
        Assert.Equal("InvalidParameter", result.Error.Code);
    }

    private static DefaultHttpContext NewContext(string method = "POST", string? contentType = null, string? body = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = "/";
        if (!string.IsNullOrEmpty(contentType))
        {
            ctx.Request.ContentType = contentType;
        }
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }

        return ctx;
    }
}
