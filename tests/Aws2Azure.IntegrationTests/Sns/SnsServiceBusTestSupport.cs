using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sns;

internal static class SnsServiceBusTestSupport
{
    public static async Task<string> CreateTopicAsync(HttpClient client, string prefix)
    {
        var topicName = SnsQueryApiClient.CreateTopicName(prefix);
        var response = await SnsQueryApiClient.CreateTopicAsync(client, topicName).ConfigureAwait(false);
        AssertStatus(response, HttpStatusCode.OK, "CreateTopic");
        return SnsQueryApiClient.ReadTopicArn(response);
    }

    public static async Task<string> CreateSubscriptionAsync(HttpClient client, string topicArn, string? endpoint = null)
    {
        var response = await SnsQueryApiClient.SubscribeAsync(
                client,
                topicArn,
                protocol: "sqs",
                endpoint ?? SnsQueryApiClient.CreateSubscriptionEndpoint())
            .ConfigureAwait(false);
        AssertStatus(response, HttpStatusCode.OK, "Subscribe");
        return SnsQueryApiClient.ReadSubscriptionArn(response);
    }

    public static async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
        ServiceBusReceiver receiver,
        int expectedCount,
        TimeSpan timeout)
    {
        var received = new List<ServiceBusReceivedMessage>(expectedCount);
        var deadline = DateTime.UtcNow + timeout;
        while (received.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var batch = await receiver.ReceiveMessagesAsync(
                    maxMessages: expectedCount - received.Count,
                    maxWaitTime: remaining > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : remaining)
                .ConfigureAwait(false);
            if (batch.Count == 0)
            {
                continue;
            }

            received.AddRange(batch);
        }

        return received;
    }

    public static async Task CompleteMessagesAsync(ServiceBusReceiver receiver, IEnumerable<ServiceBusReceivedMessage> messages)
    {
        foreach (var message in messages)
        {
            await receiver.CompleteMessageAsync(message).ConfigureAwait(false);
        }
    }

    public static async Task DeleteTopicAsync(HttpClient client, string topicArn)
    {
        var response = await SnsQueryApiClient.DeleteTopicAsync(client, topicArn).ConfigureAwait(false);
        AssertStatus(response, HttpStatusCode.OK, "DeleteTopic");
    }

    public static void AssertStatus(SnsXmlResponse response, HttpStatusCode expected, string operation)
    {
        Assert.True(
            response.StatusCode == expected,
            $"{operation} returned {(int)response.StatusCode}. Body={response.Body}");
    }
}
