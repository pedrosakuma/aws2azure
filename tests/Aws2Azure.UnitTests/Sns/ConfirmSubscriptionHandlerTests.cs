using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ConfirmSubscriptionHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_subscription_arn_without_confirmation_flow()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = "arn:aws:sns:us-west-2:000000000000:orders",
                    ["Token"] = "0123456789abcdefabcd",
                },
                null));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(
            "arn:aws:sns:us-west-2:000000000000:orders:0123456789abcdefabcd",
            SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_subscription_arn_token_for_a_different_topic()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = "arn:aws:sns:us-west-2:000000000000:orders",
                    ["Token"] = "arn:aws:sns:us-west-2:000000000000:payments:0123456789abcdefabcd",
                },
                null));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("does not belong", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_arbitrary_confirmation_tokens()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = "arn:aws:sns:us-west-2:000000000000:orders",
                    ["Token"] = "not-a-subscription-token",
                },
                null));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("valid auto-confirmed subscription token", SnsManagementClientTestSupport.ReadBody(context));
    }
}
