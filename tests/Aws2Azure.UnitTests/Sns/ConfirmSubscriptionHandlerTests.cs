using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class ConfirmSubscriptionHandlerTests
{
    private const string TopicArn = "arn:aws:sns:us-west-2:000000000000:orders";
    private const string Protocol = "sqs";
    private const string Endpoint = "queue";

    [Fact]
    public async Task HandleAsync_returns_subscription_arn_without_confirmation_flow()
    {
        var subscriptionId = SnsSubscriptionSupport.CreateSubscriptionId(TopicArn, Protocol, Endpoint);
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = TopicArn,
                    ["Token"] = subscriptionId,
                },
                null),
            SnsManagementClientTestSupport.NewCredentials(),
            ExistingSubscriptionClient(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(
            $"{TopicArn}:{subscriptionId}",
            SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_subscription_arn_token_for_a_different_topic()
    {
        var subscriptionId = SnsSubscriptionSupport.CreateSubscriptionId(TopicArn, Protocol, Endpoint);
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = TopicArn,
                    ["Token"] = $"arn:aws:sns:us-west-2:000000000000:payments:{subscriptionId}",
                },
                null),
            SnsManagementClientTestSupport.NewCredentials(),
            ExistingSubscriptionClient(),
            CancellationToken.None);

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
                    ["TopicArn"] = TopicArn,
                    ["Token"] = "not-a-subscription-token",
                },
                null),
            SnsManagementClientTestSupport.NewCredentials(),
            ExistingSubscriptionClient(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("valid auto-confirmed subscription token", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_subscription_arn_with_non_deterministic_identifier()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = TopicArn,
                    ["Token"] = "arn:aws:sns:us-west-2:000000000000:orders:not-a-real-subscription",
                },
                null),
            SnsManagementClientTestSupport.NewCredentials(),
            ExistingSubscriptionClient(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("valid auto-confirmed subscription token", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_returns_not_found_when_subscription_does_not_exist()
    {
        var subscriptionId = SnsSubscriptionSupport.CreateSubscriptionId(TopicArn, Protocol, Endpoint);
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = TopicArn,
                    ["Token"] = subscriptionId,
                },
                null),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient(
                (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains("NotFound", SnsManagementClientTestSupport.ReadBody(context));
    }

    [Fact]
    public async Task HandleAsync_rejects_existing_hex_subscription_with_foreign_metadata()
    {
        const string foreignSubscriptionId = "0123456789abcdefabcd";
        var context = SnsManagementClientTestSupport.NewContext();
        await ConfirmSubscriptionHandler.HandleAsync(
            context,
            new SnsParseResult(
                SnsOperation.ConfirmSubscription,
                new Dictionary<string, string>
                {
                    ["TopicArn"] = TopicArn,
                    ["Token"] = foreignSubscriptionId,
                },
                null),
            SnsManagementClientTestSupport.NewCredentials(),
            ExistingSubscriptionClient(foreignSubscriptionId),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("valid auto-confirmed subscription token", SnsManagementClientTestSupport.ReadBody(context));
    }

    private static Aws2Azure.Modules.Sns.Management.IServiceBusTopicsManagementClient ExistingSubscriptionClient()
        => ExistingSubscriptionClient(
            SnsSubscriptionSupport.CreateSubscriptionId(TopicArn, Protocol, Endpoint));

    private static Aws2Azure.Modules.Sns.Management.IServiceBusTopicsManagementClient ExistingSubscriptionClient(
        string subscriptionId)
        => SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    SnsManagementClientTestSupport.BuildSubscriptionEntry(
                        subscriptionId,
                        SnsManagementClientTestSupport.SerializeMetadata(Protocol, Endpoint)),
                    Encoding.UTF8,
                    "application/atom+xml"),
            });
        });
}
