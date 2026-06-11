using Amazon.SQS.Model;
using Xunit;

namespace Aws2Azure.PerfTests;

[Collection(SqsPerfCollection.Name)]
public sealed class SqsPerfTests(SqsPerfFixture fixture)
{
    [SkippableFact]
    public async Task SendMessage_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.CreateClient();
        var payload = new string('x', 256);

        using var memProbe = fixture.CreateMemoryProbe();
        var result = await PerfRunner.RunAsync(
            scenario: "sqs.SendMessage (256 B)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            memoryProbe: memProbe,
            action: async (_, ct) =>
            {
                await client.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = fixture.QueueUrl,
                    MessageBody = payload,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "SQS→ServiceBus(AMQP) emulator");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
    }
}
