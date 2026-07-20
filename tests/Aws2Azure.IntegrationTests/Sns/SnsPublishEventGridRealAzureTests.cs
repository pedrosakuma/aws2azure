using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.Modules.Sns.EventGrid;
using Azure.Storage.Queues.Models;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

/// <summary>
/// Real-Azure evidence for the SNS -> Event Grid backend contract (issue #630):
/// a per-topic <c>backend=EventGrid</c> override still creates/deletes the
/// backing Service Bus topic (documented in <c>docs/gaps/sns/_design.yaml</c>),
/// but Publish/PublishBatch route to a live Event Grid custom topic. Delivery is
/// verified end to end through the Storage Queue the topic's event subscription
/// forwards to (provisioned by <c>deploy/realazure/main.bicep</c>), not only by
/// the proxy's HTTP-level publish acceptance.
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class SnsPublishEventGridRealAzureTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Publish_delivers_subject_and_message_attributes_via_event_grid()
    {
        Skip.IfNot(fixture.EventGridConfigured,
            "AZURE_EVENTGRID_TOPIC_ENDPOINT/AZURE_EVENTGRID_TOPIC_KEY not set — skipping real-Azure SNS Event Grid conformance.");

        using var client = fixture.CreateSnsClient();
        var topicArn = await SnsServiceBusTestSupport.CreateTopicAsync(
            client, RealAzureProxyFixture.EventGridTopicNamePrefix + "publish").ConfigureAwait(false);

        try
        {
            var body = "publish-body-" + Guid.NewGuid().ToString("N");
            var subject = "subject-" + Guid.NewGuid().ToString("N")[..12];
            var publish = await SendAsync(client, "Publish",
            [
                new("TopicArn", topicArn),
                new("Message", body),
                new("Subject", subject),
                new("MessageAttributes.entry.1.Name", "color"),
                new("MessageAttributes.entry.1.Value.DataType", "String"),
                new("MessageAttributes.entry.1.Value.StringValue", "blue"),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(publish, HttpStatusCode.OK, "Publish");
            var messageId = SnsQueryApiClient.ReadMessageId(publish);
            Assert.False(string.IsNullOrWhiteSpace(messageId));

            var delivered = await ReceiveEventGridEnvelopesAsync(expectedCount: 1, TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
            var envelope = Assert.Single(delivered);
            Assert.Equal("aws.sns.Message", envelope.EventType);
            Assert.Equal(topicArn, envelope.Subject);
            Assert.Equal(body, envelope.Data.Message);
            Assert.Equal(subject, envelope.Data.Subject);
            Assert.Equal(topicArn, envelope.Data.TopicArn);
            Assert.Equal("blue", envelope.Data.MessageAttributes["color"].Value);
            Assert.Equal("String", envelope.Data.MessageAttributes["color"].Type);
        }
        finally
        {
            await SnsServiceBusTestSupport.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task PublishBatch_delivers_all_entries_via_event_grid()
    {
        Skip.IfNot(fixture.EventGridConfigured,
            "AZURE_EVENTGRID_TOPIC_ENDPOINT/AZURE_EVENTGRID_TOPIC_KEY not set — skipping real-Azure SNS Event Grid conformance.");

        using var client = fixture.CreateSnsClient();
        var topicArn = await SnsServiceBusTestSupport.CreateTopicAsync(
            client, RealAzureProxyFixture.EventGridTopicNamePrefix + "batch").ConfigureAwait(false);

        try
        {
            var entries = Enumerable.Range(1, 3)
                .Select(index => (Id: $"m{index}", Body: $"publish-batch-{Guid.NewGuid():N}-{index}"))
                .ToArray();
            var parameters = new List<KeyValuePair<string, string>> { new("TopicArn", topicArn) };
            for (var i = 0; i < entries.Length; i++)
            {
                var ordinal = i + 1;
                parameters.Add(new($"PublishBatchRequestEntries.member.{ordinal}.Id", entries[i].Id));
                parameters.Add(new($"PublishBatchRequestEntries.member.{ordinal}.Message", entries[i].Body));
            }

            var response = await SendAsync(client, "PublishBatch", parameters).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "PublishBatch");
            Assert.Equal(entries.Length, SnsQueryApiClient.ReadPublishBatchSuccessIds(response).Count);
            Assert.Empty(SnsQueryApiClient.ReadPublishBatchFailures(response));

            var delivered = await ReceiveEventGridEnvelopesAsync(entries.Length, TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
            Assert.Equal(entries.Length, delivered.Count);
            Assert.Equal(
                entries.Select(entry => entry.Body).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                delivered.Select(envelope => envelope.Data.Message).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            await SnsServiceBusTestSupport.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One oversized entry is rejected before any HTTP call to Event Grid
    /// (<see cref="EventGridPublisher"/> pre-checks each serialized event against
    /// the 1 MB classic-schema limit), so this is a genuine per-entry partial
    /// failure against the live backend, not a simulated/mocked one: the small
    /// entry is delivered through the real Event Grid topic and queue while the
    /// oversized entry fails without ever leaving the proxy.
    /// </summary>
    [SkippableFact]
    public async Task PublishBatch_reports_per_entry_failure_for_oversized_entry_via_event_grid()
    {
        Skip.IfNot(fixture.EventGridConfigured,
            "AZURE_EVENTGRID_TOPIC_ENDPOINT/AZURE_EVENTGRID_TOPIC_KEY not set — skipping real-Azure SNS Event Grid conformance.");

        using var client = fixture.CreateSnsClient();
        var topicArn = await SnsServiceBusTestSupport.CreateTopicAsync(
            client, RealAzureProxyFixture.EventGridTopicNamePrefix + "partial").ConfigureAwait(false);

        try
        {
            var smallBody = "publish-batch-small-" + Guid.NewGuid().ToString("N");
            var oversizedBody = new string('x', 2 * 1024 * 1024);
            var response = await SendAsync(client, "PublishBatch",
            [
                new("TopicArn", topicArn),
                new("PublishBatchRequestEntries.member.1.Id", "small"),
                new("PublishBatchRequestEntries.member.1.Message", smallBody),
                new("PublishBatchRequestEntries.member.2.Id", "oversized"),
                new("PublishBatchRequestEntries.member.2.Message", oversizedBody),
            ]).ConfigureAwait(false);
            SnsServiceBusTestSupport.AssertStatus(response, HttpStatusCode.OK, "PublishBatch");

            var successIds = SnsQueryApiClient.ReadPublishBatchSuccessIds(response);
            var failures = SnsQueryApiClient.ReadPublishBatchFailures(response);
            Assert.Equal(["small"], successIds);
            var failure = Assert.Single(failures);
            Assert.Equal("oversized", failure.Id);
            Assert.True(failure.SenderFault);

            var delivered = await ReceiveEventGridEnvelopesAsync(expectedCount: 1, TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);
            var envelope = Assert.Single(delivered);
            Assert.Equal(smallBody, envelope.Data.Message);
        }
        finally
        {
            await SnsServiceBusTestSupport.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<SnsEventGridEnvelope>> ReceiveEventGridEnvelopesAsync(
        int expectedCount,
        TimeSpan timeout)
    {
        var queue = fixture.CreateEventGridEvidenceQueueClient();
        var received = new List<SnsEventGridEnvelope>(expectedCount);
        var deadline = DateTime.UtcNow + timeout;
        while (received.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            var batch = await queue.ReceiveMessagesAsync(
                    maxMessages: Math.Min(32, expectedCount - received.Count),
                    visibilityTimeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (batch.Value.Length == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                continue;
            }

            foreach (var message in batch.Value)
            {
                received.Add(DecodeEnvelope(message));
                await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt).ConfigureAwait(false);
            }
        }

        return received;
    }

    private static SnsEventGridEnvelope DecodeEnvelope(QueueMessage message)
    {
        // Event Grid base64-encodes the event JSON when the destination is a
        // Storage Queue.
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(message.MessageText));
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement[0]
            : document.RootElement;
        return element.Deserialize(SnsEventGridJsonContext.Default.SnsEventGridEnvelope)
            ?? throw new InvalidOperationException("Queue message did not contain a decodable Event Grid envelope.");
    }

    private static Task<SnsXmlResponse> SendAsync(
        HttpClient client,
        string action,
        IEnumerable<KeyValuePair<string, string>> parameters)
        => SnsQueryApiClient.SendActionAsync(
            client,
            action,
            parameters,
            RealAzureProxyFixture.AwsAccessKey,
            RealAzureProxyFixture.AwsSecret);
}
