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
}
