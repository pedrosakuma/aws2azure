using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
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

    public static async Task AssertNoMessagesAsync(
        ServiceBusReceiver receiver,
        TimeSpan quietWindow,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval is { } value && value > TimeSpan.Zero
            ? value
            : TimeSpan.FromMilliseconds(250);
        var deadline = DateTime.UtcNow + quietWindow;

        while (DateTime.UtcNow < deadline)
        {
            var batch = await receiver.PeekMessagesAsync(maxMessages: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            Assert.Empty(batch);

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Bounded retry for a documented, unconfirmed real-Azure quirk (#691): the very first
    /// write to a subscription's reserved <c>$Default</c> Service Bus rule immediately after
    /// <c>Subscribe</c> has been observed to intermittently return an authorization-denied,
    /// empty-body response on real Azure, even though the identical request normally succeeds.
    /// Extensive investigation (sequencing, auth path, propagation delay, an interleaved
    /// warm-up read) ruled out every reproducible code-level cause, so this re-issues the same
    /// AWS-shaped call a bounded number of times before failing the test. Do not widen this for
    /// unrelated failures — it exists solely to absorb this one documented quirk.
    /// </summary>
    public static async Task<SnsXmlResponse> AssertStatusWithKnownRealAzureRetryAsync(
        Func<Task<SnsXmlResponse>> send,
        HttpStatusCode expected,
        string operation,
        int maxAttempts = 3,
        TimeSpan? delayBetweenAttempts = null)
    {
        var delay = delayBetweenAttempts ?? TimeSpan.FromSeconds(5);
        SnsXmlResponse response;
        for (var attempt = 1; ; attempt++)
        {
            response = await send().ConfigureAwait(false);
            if (response.StatusCode == expected || attempt >= maxAttempts)
            {
                break;
            }

            await Task.Delay(delay).ConfigureAwait(false);
        }

        AssertStatus(response, expected, operation);
        return response;
    }
}
