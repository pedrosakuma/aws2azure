using System.Net;
using System.Net.Http;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class UnsubscribeHandlerTests
{
    [Fact]
    public async Task HandleAsync_deletes_subscription()
    {
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("https://myns.servicebus.windows.net/orders/subscriptions/sub123?api-version=2021-05", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await UnsubscribeHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders:sub123"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("<UnsubscribeResponse", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_treats_not_found_as_success()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await UnsubscribeHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders:sub123"),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-arn")]
    [InlineData("arn:aws:sns:us-west-2:000000000000:orders")]
    public async Task HandleAsync_rejects_malformed_subscription_arn(string subscriptionArn)
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await UnsubscribeHandler.HandleAsync(
            context,
            NewParseResult(subscriptionArn),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => throw new InvalidOperationException("HTTP should not be called.")),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("InvalidParameter", SnsManagementClientTestSupport.ReadBody(context));
    }

    private static SnsParseResult NewParseResult(string subscriptionArn)
        => new(SnsOperation.Unsubscribe, new Dictionary<string, string> { ["SubscriptionArn"] = subscriptionArn }, null);
}
