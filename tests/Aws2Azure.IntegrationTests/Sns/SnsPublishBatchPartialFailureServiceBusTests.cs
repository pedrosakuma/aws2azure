using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

[Trait("Category", "Integration")]
[Collection(SnsServiceBusProxyCollection.Name)]
public sealed class SnsPublishBatchPartialFailureServiceBusTests
{
    private readonly SnsServiceBusProxyFixture _fixture;

    public SnsPublishBatchPartialFailureServiceBusTests(SnsServiceBusProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Oversized_entry_produces_per_entry_failure_while_good_entries_succeed()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        using var client = _fixture.CreateSnsClient();
        var topicArn = await SnsServiceBusTestSupport.CreateTopicAsync(client, "sns-partial").ConfigureAwait(false);
        var subscriptionArn = await SnsServiceBusTestSupport.CreateSubscriptionAsync(client, topicArn).ConfigureAwait(false);
        var topicName = topicArn.Split(':', 6)[5];
        var subscriptionName = SnsQueryApiClient.ExtractSubscriptionName(subscriptionArn);
        await using var serviceBusClient = CreateServiceBusClient();
        await using var receiver = serviceBusClient.CreateReceiver(topicName, subscriptionName);

        try
        {
            var goodEntries = Enumerable.Range(1, 4)
                .Select(index => (Id: $"ok{index}", Body: $"partial-ok-{Guid.NewGuid():N}-{index}"))
                .ToArray();
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("TopicArn", topicArn),
            };
            for (var i = 0; i < goodEntries.Length; i++)
            {
                var ordinal = i + 1;
                parameters.Add(new($"PublishBatchRequestEntries.member.{ordinal}.Id", goodEntries[i].Id));
                parameters.Add(new($"PublishBatchRequestEntries.member.{ordinal}.Message", goodEntries[i].Body));
            }

            parameters.Add(new("PublishBatchRequestEntries.member.5.Id", "too-big"));
            parameters.Add(new("PublishBatchRequestEntries.member.5.Message", new string('x', 270 * 1024)));

            var response = await SnsQueryApiClient.SendActionAsync(client, "PublishBatch", parameters).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "PublishBatch[partial failure]");

            var successfulIds = SnsQueryApiClient.ReadPublishBatchSuccessIds(response);
            var failures = SnsQueryApiClient.ReadPublishBatchFailures(response);
            Assert.Equal(goodEntries.Select(entry => entry.Id).OrderBy(id => id).ToArray(), successfulIds.OrderBy(id => id).ToArray());
            var failure = Assert.Single(failures);
            Assert.Equal("too-big", failure.Id);
            Assert.False(string.IsNullOrWhiteSpace(failure.Code));
            Assert.False(string.IsNullOrWhiteSpace(failure.Message));

            var messages = await SnsServiceBusTestSupport.ReceiveMessagesAsync(receiver, expectedCount: goodEntries.Length, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(goodEntries.Length, messages.Count);
            Assert.Equal(
                goodEntries.Select(entry => entry.Body).OrderBy(value => value).ToArray(),
                messages.Select(message => message.Body.ToString()).OrderBy(value => value).ToArray());

            await SnsServiceBusTestSupport.CompleteMessagesAsync(receiver, messages).ConfigureAwait(false);
        }
        finally
        {
            await SnsServiceBusTestSupport.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        }
    }

    private ServiceBusClient CreateServiceBusClient()
        => new(
            _fixture.CreateServiceBusConnectionString(),
            new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpTcp,
            });
}
