using Azure.Messaging.ServiceBus;
using Xunit;

namespace Aws2Azure.PerfTests.Sns;

/// <summary>
/// Baseline that hits the Service Bus emulator topic directly with
/// <c>Azure.Messaging.ServiceBus</c> — the proxy is left idle. Mirrors
/// <see cref="SnsPerfTests.Publish_throughput"/> shape (c=16, 20s, 256 B)
/// so the two rows in baseline-latest.md are directly comparable and
/// surface the "proxy tax" (issue #131).
/// </summary>
[Collection(SnsPerfCollection.Name)]
public sealed class AzureServiceBusTopicSdkBaselinePerfTests(SnsPerfFixture fixture)
{
    [SkippableFact]
    public async Task SendMessageAsync_throughput_AzureSdk_topic()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        await using var sb = new ServiceBusClient(fixture.ServiceBusConnectionString);
        await using var sender = sb.CreateSender(fixture.TopicName);
        var payload = new string('x', 256);

        var result = await PerfRunner.RunAsync(
            scenario: "azure-sdk.ServiceBusTopics.SendMessage (256 B)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (_, ct) =>
            {
                await sender.SendMessageAsync(new ServiceBusMessage(payload), ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "Azure SDK baseline — direct ServiceBusSender against SB emulator topic (no proxy)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
