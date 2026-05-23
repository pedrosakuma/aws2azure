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
public sealed class SnsPublishServiceBusTests
{
    private readonly SnsServiceBusProxyFixture _fixture;

    public SnsPublishServiceBusTests(SnsServiceBusProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Publish_roundtrips_subject_and_message_attributes_to_service_bus_subscription()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        using var client = _fixture.CreateSnsClient();
        var topicArn = await SnsServiceBusTestSupport.CreateTopicAsync(client, "sns-publish").ConfigureAwait(false);
        var subscriptionArn = await SnsServiceBusTestSupport.CreateSubscriptionAsync(client, topicArn).ConfigureAwait(false);
        var topicName = topicArn.Split(':', 6)[5];
        var subscriptionName = SnsQueryApiClient.ExtractSubscriptionName(subscriptionArn);
        await using var serviceBusClient = CreateServiceBusClient();
        await using var receiver = serviceBusClient.CreateReceiver(topicName, subscriptionName);

        try
        {
            var body = "publish-body-" + Guid.NewGuid().ToString("N");
            var subject = "subject-" + Guid.NewGuid().ToString("N")[..12];
            var publish = await SnsQueryApiClient.SendActionAsync(client, "Publish", [
                new("TopicArn", topicArn),
                new("Message", body),
                new("Subject", subject),
                new("MessageAttributes.entry.1.Name", "color"),
                new("MessageAttributes.entry.1.Value.DataType", "String"),
                new("MessageAttributes.entry.1.Value.StringValue", "blue"),
                new("MessageAttributes.entry.2.Name", "priority"),
                new("MessageAttributes.entry.2.Value.DataType", "Number"),
                new("MessageAttributes.entry.2.Value.StringValue", "42")
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(publish, HttpStatusCode.OK, "Publish");

            var messages = await SnsServiceBusTestSupport.ReceiveMessagesAsync(receiver, expectedCount: 1, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var message = Assert.Single(messages);
            Assert.Equal(body, message.Body.ToString());
            Assert.Equal(subject, message.Subject);
            Assert.Equal(subject, Assert.IsType<string>(message.ApplicationProperties["aws.sns.Subject"]));
            Assert.Equal("blue", Assert.IsType<string>(message.ApplicationProperties["color"]));
            Assert.Equal("String", Assert.IsType<string>(message.ApplicationProperties["color.DataType"]));
            Assert.Equal("42", Assert.IsType<string>(message.ApplicationProperties["priority"]));
            Assert.Equal("Number", Assert.IsType<string>(message.ApplicationProperties["priority.DataType"]));

            await SnsServiceBusTestSupport.CompleteMessagesAsync(receiver, messages).ConfigureAwait(false);
        }
        finally
        {
            await SnsServiceBusTestSupport.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task PublishBatch_delivers_all_entries_to_service_bus_subscription()
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker not available.");

        using var client = _fixture.CreateSnsClient();
        var topicArn = await SnsServiceBusTestSupport.CreateTopicAsync(client, "sns-batch").ConfigureAwait(false);
        var subscriptionArn = await SnsServiceBusTestSupport.CreateSubscriptionAsync(client, topicArn).ConfigureAwait(false);
        var topicName = topicArn.Split(':', 6)[5];
        var subscriptionName = SnsQueryApiClient.ExtractSubscriptionName(subscriptionArn);
        await using var serviceBusClient = CreateServiceBusClient();
        await using var receiver = serviceBusClient.CreateReceiver(topicName, subscriptionName);

        try
        {
            var entries = Enumerable.Range(1, 5)
                .Select(index => (Id: $"m{index}", Body: $"publish-batch-{Guid.NewGuid():N}-{index}"))
                .ToArray();
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("TopicArn", topicArn),
            };
            for (var i = 0; i < entries.Length; i++)
            {
                var ordinal = i + 1;
                parameters.Add(new($"PublishBatchRequestEntries.member.{ordinal}.Id", entries[i].Id));
                parameters.Add(new($"PublishBatchRequestEntries.member.{ordinal}.Message", entries[i].Body));
            }

            var response = await SnsQueryApiClient.SendActionAsync(client, "PublishBatch", parameters).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "PublishBatch");
            Assert.Equal(entries.Length, SnsQueryApiClient.ReadPublishBatchSuccessIds(response).Count);
            Assert.Empty(SnsQueryApiClient.ReadPublishBatchFailures(response));

            var messages = await SnsServiceBusTestSupport.ReceiveMessagesAsync(receiver, expectedCount: entries.Length, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(entries.Length, messages.Count);
            Assert.Equal(
                entries.Select(entry => entry.Body).OrderBy(value => value).ToArray(),
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
