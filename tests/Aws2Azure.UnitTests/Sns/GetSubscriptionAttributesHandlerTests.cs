using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class GetSubscriptionAttributesHandlerTests
{
    [Fact]
    public async Task HandleAsync_maps_subscription_metadata_to_sns_attributes()
    {
        var metadata = SnsManagementClientTestSupport.SerializeMetadata(
            protocol: "https",
            endpoint: "https://example.com/hooks/orders",
            filterPolicyJson: "{\"tenant\":[\"blue\"]}",
            rawDeliveryEnabled: true,
            filterPolicyScope: SnsSubscriptionMetadata.MessageBodyScope);

        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://myns.servicebus.windows.net/orders/subscriptions/sub123?api-version=2021-05", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionEntry("sub123", metadata), Encoding.UTF8, "application/atom+xml"),
            });
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await GetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:123456789012:orders:sub123"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var attributes = SnsManagementClientTestSupport.ReadAttributes(SnsManagementClientTestSupport.ReadBody(context));
        Assert.Equal("arn:aws:sns:us-west-2:123456789012:orders:sub123", attributes["SubscriptionArn"]);
        Assert.Equal("arn:aws:sns:us-west-2:123456789012:orders", attributes["TopicArn"]);
        Assert.Equal("123456789012", attributes["Owner"]);
        Assert.Equal("https", attributes["Protocol"]);
        Assert.Equal("https://example.com/hooks/orders", attributes["Endpoint"]);
        Assert.Equal("true", attributes["ConfirmationWasAuthenticated"]);
        Assert.Equal("false", attributes["PendingConfirmation"]);
        Assert.Equal("true", attributes["RawMessageDelivery"]);
        Assert.Equal("{\"tenant\":[\"blue\"]}", attributes["FilterPolicy"]);
        Assert.Equal("MessageBody", attributes["FilterPolicyScope"]);
        Assert.False(attributes.ContainsKey("RedrivePolicy"));
    }

    [Fact]
    public async Task HandleAsync_defaults_missing_user_metadata_to_empty_protocol_and_endpoint()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await GetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders:sub123"),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildSubscriptionEntry("sub123", null), Encoding.UTF8, "application/atom+xml"),
            })),
            CancellationToken.None);

        var attributes = SnsManagementClientTestSupport.ReadAttributes(SnsManagementClientTestSupport.ReadBody(context));
        Assert.Equal(string.Empty, attributes["Protocol"]);
        Assert.Equal(string.Empty, attributes["Endpoint"]);
        Assert.Equal("false", attributes["RawMessageDelivery"]);
        Assert.False(attributes.ContainsKey("FilterPolicy"));
    }

    [Fact]
    public async Task HandleAsync_returns_not_found_for_missing_subscription()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await GetSubscriptionAttributesHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders:sub123"),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains("NotFound", SnsManagementClientTestSupport.ReadBody(context));
    }

    private static SnsParseResult NewParseResult(string subscriptionArn)
        => new(SnsOperation.GetSubscriptionAttributes, new Dictionary<string, string> { ["SubscriptionArn"] = subscriptionArn }, null);
}
