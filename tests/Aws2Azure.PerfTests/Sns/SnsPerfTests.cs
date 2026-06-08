using Amazon.SimpleNotificationService.Model;
using Xunit;

namespace Aws2Azure.PerfTests;

[Collection(SnsPerfCollection.Name)]
public sealed class SnsPerfTests(SnsPerfFixture fixture)
{
    [SkippableFact]
    public async Task Publish_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();
        var payload = new string('x', 256);

        var result = await PerfRunner.RunAsync(
            scenario: "sns.Publish (256 B)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (_, ct) =>
            {
                await client.PublishAsync(new PublishRequest
                {
                    TopicArn = fixture.TopicArn,
                    Message = payload,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "SNS→ServiceBusTopics(AMQP) emulator");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task SubscribeUnsubscribe_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        var result = await PerfRunner.RunAsync(
            scenario: "sns.Subscribe+Unsubscribe",
            // Management-plane ops (Service Bus subscription create/delete) are
            // an order of magnitude heavier than data-plane Publish, so a low
            // concurrency keeps the emulator from queueing behind itself.
            concurrency: 4,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (_, ct) =>
            {
                // Unique endpoint ⇒ unique subscription id (CreateSubscriptionId
                // is deterministic on protocol+endpoint). Create then delete so
                // the topic's subscription count stays bounded across the run.
                var endpoint = "arn:aws:sqs:us-east-1:000000000000:perf-" + Guid.NewGuid().ToString("N")[..12];
                var sub = await client.SubscribeAsync(new SubscribeRequest
                {
                    TopicArn = fixture.TopicArn,
                    Protocol = "sqs",
                    Endpoint = endpoint,
                    ReturnSubscriptionArn = true,
                }, ct).ConfigureAwait(false);

                await client.UnsubscribeAsync(new UnsubscribeRequest
                {
                    SubscriptionArn = sub.SubscriptionArn,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "SNS→ServiceBusTopics management — Subscribe+Unsubscribe pair; throughput conflates create+delete, use the proxy profiler to attribute each handler. Emulator does not persist subscription metadata (results are emulator-bound).");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task ListSubscriptionsByTopic_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        // Seed a handful of subscriptions so the list response has real content
        // to serialize. Seed cost is outside the perf window.
        const int seedCount = 20;
        for (var i = 0; i < seedCount; i++)
        {
            await client.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = fixture.TopicArn,
                Protocol = "sqs",
                Endpoint = $"arn:aws:sqs:us-east-1:000000000000:perf-list-{i:D2}-{Guid.NewGuid().ToString("N")[..8]}",
                ReturnSubscriptionArn = true,
            }).ConfigureAwait(false);
        }

        var result = await PerfRunner.RunAsync(
            scenario: "sns.ListSubscriptionsByTopic (20 subs)",
            concurrency: 8,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (_, ct) =>
            {
                await client.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
                {
                    TopicArn = fixture.TopicArn,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "SNS→ServiceBusTopics management — ListSubscriptionsByTopic over ~20 seeded subscriptions (emulator does not persist subscription metadata; results are emulator-bound).");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }

    [SkippableFact]
    public async Task SetSubscriptionAttributes_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();

        // Seed one subscription per worker slot so concurrent workers never
        // update the SAME Service Bus subscription — SB management uses
        // optimistic concurrency (ETag) and concurrent UpdateSubscription on
        // one entity returns HTTP 409.
        const int workers = 8;
        var subscriptionArns = new string[workers];
        for (var i = 0; i < workers; i++)
        {
            var seeded = await client.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = fixture.TopicArn,
                Protocol = "sqs",
                Endpoint = $"arn:aws:sqs:us-east-1:000000000000:perf-set-{i:D2}-{Guid.NewGuid().ToString("N")[..8]}",
                ReturnSubscriptionArn = true,
            }).ConfigureAwait(false);
            subscriptionArns[i] = seeded.SubscriptionArn;
        }

        var result = await PerfRunner.RunAsync(
            scenario: "sns.SetSubscriptionAttributes (FilterPolicy)",
            concurrency: workers,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                // Each worker owns a distinct subscription (warmup id -1 → slot 0).
                var slot = workerId < 0 ? 0 : workerId % subscriptionArns.Length;
                await client.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
                {
                    SubscriptionArn = subscriptionArns[slot],
                    AttributeName = "FilterPolicy",
                    AttributeValue = $"{{\"kind\":[\"perf-{slot}\"]}}",
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "SNS→ServiceBusTopics management — SetSubscriptionAttributes(FilterPolicy), one subscription per worker (avoids ETag 409); exercises get-then-update metadata path (emulator does not persist subscription metadata; results are emulator-bound).");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
