using System.Net;
using System.Net.Http;
using System.Text;
using Aws2Azure.Modules.Sns;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.UnitTests.Sns;

public sealed class GetTopicAttributesHandlerTests
{
    [Fact]
    public async Task HandleAsync_maps_service_bus_topic_properties_to_sns_attributes()
    {
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://myns.servicebus.windows.net/orders?api-version=2021-05", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders", subscriptionCount: 7, requiresDuplicateDetection: true), Encoding.UTF8, "application/atom+xml"),
            });
        });

        var context = SnsManagementClientTestSupport.NewContext();
        await GetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:123456789012:orders"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var attributes = SnsManagementClientTestSupport.ReadAttributes(SnsManagementClientTestSupport.ReadBody(context));
        Assert.Equal("arn:aws:sns:us-west-2:123456789012:orders", attributes["TopicArn"]);
        Assert.Equal("123456789012", attributes["Owner"]);
        Assert.Equal(string.Empty, attributes["DisplayName"]);
        Assert.Equal("{}", attributes["Policy"]);
        Assert.Equal("7", attributes["SubscriptionsConfirmed"]);
        Assert.Equal("0", attributes["SubscriptionsPending"]);
        Assert.Equal("0", attributes["SubscriptionsDeleted"]);
        Assert.Equal(string.Empty, attributes["KmsMasterKeyId"]);
        Assert.Equal("true", attributes["FifoTopic"]);
        Assert.Equal("true", attributes["ContentBasedDeduplication"]);
    }

    [Fact]
    public async Task HandleAsync_detects_fifo_topics_from_name_suffix()
    {
        var managementClient = SnsManagementClientTestSupport.NewManagementClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SnsManagementClientTestSupport.BuildTopicEntry("orders.fifo", subscriptionCount: 2, requiresDuplicateDetection: false), Encoding.UTF8, "application/atom+xml"),
            }));

        var context = SnsManagementClientTestSupport.NewContext();
        await GetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders.fifo"),
            SnsManagementClientTestSupport.NewCredentials(),
            managementClient,
            CancellationToken.None);

        var attributes = SnsManagementClientTestSupport.ReadAttributes(SnsManagementClientTestSupport.ReadBody(context));
        Assert.Equal("true", attributes["FifoTopic"]);
        Assert.Equal("false", attributes["ContentBasedDeduplication"]);
    }

    [Fact]
    public async Task HandleAsync_returns_not_found_for_missing_topic()
    {
        var context = SnsManagementClientTestSupport.NewContext();
        await GetTopicAttributesHandler.HandleAsync(
            context,
            NewParseResult("arn:aws:sns:us-west-2:000000000000:orders"),
            SnsManagementClientTestSupport.NewCredentials(),
            SnsManagementClientTestSupport.NewManagementClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Contains("NotFound", SnsManagementClientTestSupport.ReadBody(context));
    }

    private static SnsParseResult NewParseResult(string topicArn)
        => new(SnsOperation.GetTopicAttributes, new Dictionary<string, string> { ["TopicArn"] = topicArn }, null);
}
