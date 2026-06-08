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

        // Seed one subscription whose FilterPolicy we rewrite each iteration.
        var seeded = await client.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = fixture.TopicArn,
            Protocol = "sqs",
            Endpoint = "arn:aws:sqs:us-east-1:000000000000:perf-set-" + Guid.NewGuid().ToString("N")[..12],
            ReturnSubscriptionArn = true,
        }).ConfigureAwait(false);
        var subscriptionArn = seeded.SubscriptionArn;

        var result = await PerfRunner.RunAsync(
            scenario: "sns.SetSubscriptionAttributes (FilterPolicy)",
            concurrency: 8,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                await client.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
                {
                    SubscriptionArn = subscriptionArn,
                    AttributeName = "FilterPolicy",
                    AttributeValue = $"{{\"kind\":[\"perf-{(workerId < 0 ? 0 : workerId)}\"]}}",
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "SNS→ServiceBusTopics management — SetSubscriptionAttributes(FilterPolicy) rewriting one subscription; exercises get-then-update metadata path (emulator does not persist subscription metadata; results are emulator-bound).");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
