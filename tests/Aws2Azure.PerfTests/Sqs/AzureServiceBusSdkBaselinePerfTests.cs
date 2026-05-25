using Azure.Messaging.ServiceBus;
using Xunit;

namespace Aws2Azure.PerfTests.Sqs;

/// <summary>
/// Baselines that hit the Service Bus emulator directly with
/// <c>Azure.Messaging.ServiceBus</c> — the proxy is left idle. Mirror
/// the SQS send+receive shapes so the rows in baseline-latest.md are
/// directly comparable and surface the "proxy tax" (issue #131).
/// </summary>
[Collection(SqsPerfCollection.Name)]
public sealed class AzureServiceBusSdkBaselinePerfTests(SqsPerfFixture fixture)
{
    private const int PrefillCount = 1_500;

    [SkippableFact]
    public async Task SendMessageAsync_throughput_AzureSdk()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        await using var sb = new ServiceBusClient(fixture.ServiceBusConnectionString);
        await using var sender = sb.CreateSender(fixture.QueueName);
        var payload = new string('x', 256);

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.ServiceBus.SendMessage (256 B, queue)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (_, ct) =>
            {
                await sender.SendMessageAsync(new ServiceBusMessage(payload), ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "Azure SDK baseline — direct ServiceBusSender against SB emulator queue (no proxy)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
    }

    [SkippableFact]
    public async Task ReceiveMessageAsync_throughput_AzureSdk()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        await using var sb = new ServiceBusClient(fixture.ServiceBusConnectionString);
        await using var sender = sb.CreateSender(fixture.QueueName);
        await using var receiver = sb.CreateReceiver(fixture.QueueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            PrefetchCount = 0,
        });

        // Drain anything left over from the proxy-side test on the same queue.
        await DrainAsync(receiver).ConfigureAwait(false);

        // Pre-fill the queue.
        var payload = new string('x', 256);
        for (var produced = 0; produced < PrefillCount;)
        {
            var batch = await sender.CreateMessageBatchAsync().ConfigureAwait(false);
            for (var i = 0; produced < PrefillCount && batch.TryAddMessage(new ServiceBusMessage(payload)); i++)
            {
                produced++;
            }
            await sender.SendMessagesAsync(batch).ConfigureAwait(false);
        }

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.ServiceBus.ReceiveMessage+Complete (1)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (_, ct) =>
            {
                var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(50), ct)
                    .ConfigureAwait(false);
                if (msg is not null)
                {
                    await receiver.CompleteMessageAsync(msg, ct).ConfigureAwait(false);
                }
            });

        PerfReport.Append(result, notes: "Azure SDK baseline — direct ServiceBusReceiver receive+complete against SB emulator queue (no proxy); empty receives count as no-op calls");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
    }

    private static async Task DrainAsync(ServiceBusReceiver receiver)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var consecutiveEmpty = 0;
        while (DateTime.UtcNow < deadline && consecutiveEmpty < 3)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 100, maxWaitTime: TimeSpan.FromMilliseconds(200))
                .ConfigureAwait(false);
            if (batch is null || batch.Count == 0)
            {
                consecutiveEmpty++;
                continue;
            }
            consecutiveEmpty = 0;
            foreach (var msg in batch)
            {
                await receiver.CompleteMessageAsync(msg).ConfigureAwait(false);
            }
        }
    }
}
