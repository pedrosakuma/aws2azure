using System.Net;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

[Trait("Category", "Integration")]
[Collection(SnsServiceBusProxyCollection.Name)]
public sealed class SnsTopicLifecycleServiceBusTests
{
    private readonly SnsServiceBusProxyFixture _fixture;

    public SnsTopicLifecycleServiceBusTests(SnsServiceBusProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Create_list_get_delete_and_idempotent_calls_roundtrip_on_service_bus_emulator()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        using var client = _fixture.CreateSnsClient();
        var topicName = SnsQueryApiClient.CreateTopicName("sns-lifecycle");

        var create1 = await SnsQueryApiClient.CreateTopicAsync(client, topicName).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(create1, HttpStatusCode.OK, "CreateTopic[first]");
        var topicArn = SnsQueryApiClient.ReadTopicArn(create1);

        var create2 = await SnsQueryApiClient.CreateTopicAsync(client, topicName).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(create2, HttpStatusCode.OK, "CreateTopic[second]");
        Assert.Equal(topicArn, SnsQueryApiClient.ReadTopicArn(create2));

        var listAfterCreate = await SnsQueryApiClient.ListTopicsAsync(client).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(listAfterCreate, HttpStatusCode.OK, "ListTopics[after create]");
        Assert.Contains(topicArn, SnsQueryApiClient.ReadTopicArns(listAfterCreate));

        var attrsResponse = await SnsQueryApiClient.GetTopicAttributesAsync(client, topicArn).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(attrsResponse, HttpStatusCode.OK, "GetTopicAttributes");
        var attributes = SnsQueryApiClient.ReadAttributes(attrsResponse);
        Assert.Equal(topicArn, attributes["TopicArn"]);
        Assert.Equal("0", attributes["SubscriptionsConfirmed"]);
        Assert.Equal("false", attributes["FifoTopic"]);

        var delete1 = await SnsQueryApiClient.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(delete1, HttpStatusCode.OK, "DeleteTopic[first]");

        var delete2 = await SnsQueryApiClient.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(delete2, HttpStatusCode.OK, "DeleteTopic[second]");

        var listAfterDelete = await SnsQueryApiClient.ListTopicsAsync(client).ConfigureAwait(false);
        SnsServiceBusTestSupport.AssertStatus(listAfterDelete, HttpStatusCode.OK, "ListTopics[after delete]");
        Assert.DoesNotContain(topicArn, SnsQueryApiClient.ReadTopicArns(listAfterDelete));
    }
}
