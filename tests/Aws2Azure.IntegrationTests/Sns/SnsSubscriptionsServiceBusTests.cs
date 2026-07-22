using System.Net;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

[Trait("Category", "Integration")]
[Collection(SnsServiceBusProxyCollection.Name)]
public sealed class SnsSubscriptionsServiceBusTests
{
    private readonly SnsServiceBusProxyFixture _fixture;

    public SnsSubscriptionsServiceBusTests(SnsServiceBusProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Subscribe_list_get_and_unsubscribe_roundtrip_on_service_bus_emulator()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        using var client = _fixture.CreateSnsClient();
        var topicArn = await SnsServiceBusTestSupport.CreateTopicAsync(client, "sns-subs").ConfigureAwait(false);
        var endpoint = SnsQueryApiClient.CreateSubscriptionEndpoint();

        try
        {
            var subscribe = await SnsQueryApiClient.SubscribeAsync(client, topicArn, "sqs", endpoint).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(subscribe, HttpStatusCode.OK, "Subscribe");
            var subscriptionArn = SnsQueryApiClient.ReadSubscriptionArn(subscribe);

            var duplicateSubscribe = await SnsQueryApiClient.SubscribeAsync(client, topicArn, "sqs", endpoint).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(duplicateSubscribe, HttpStatusCode.OK, "Subscribe[idempotent]");
            Assert.Equal(subscriptionArn, SnsQueryApiClient.ReadSubscriptionArn(duplicateSubscribe));

            var list = await SnsQueryApiClient.ListSubscriptionsByTopicAsync(client, topicArn).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(list, HttpStatusCode.OK, "ListSubscriptionsByTopic");
            var listed = Assert.Single(SnsQueryApiClient.ReadListedSubscriptions(list));
            Assert.Equal(subscriptionArn, listed.SubscriptionArn);
            Assert.Equal(topicArn, listed.TopicArn);

            var getBefore = await SnsQueryApiClient.GetSubscriptionAttributesAsync(client, subscriptionArn).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(getBefore, HttpStatusCode.OK, "GetSubscriptionAttributes[before set]");
            var attrsBefore = SnsQueryApiClient.ReadAttributes(getBefore);
            Assert.Equal(subscriptionArn, attrsBefore["SubscriptionArn"]);
            Assert.Equal(topicArn, attrsBefore["TopicArn"]);

            var unsubscribe = await SnsQueryApiClient.UnsubscribeAsync(client, subscriptionArn).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(unsubscribe, HttpStatusCode.OK, "Unsubscribe");

            var listAfterDelete = await SnsQueryApiClient.ListSubscriptionsByTopicAsync(client, topicArn).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(listAfterDelete, HttpStatusCode.OK, "ListSubscriptionsByTopic[after unsubscribe]");
            Assert.Empty(SnsQueryApiClient.ReadListedSubscriptions(listAfterDelete));
        }
        finally
        {
            await SnsServiceBusTestSupport.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        }
    }
}
