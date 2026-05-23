using System.Text;
using Amazon.Kinesis.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Kinesis perf reuses the existing
/// <see cref="KinesisEmulatorProxyFixture"/> from the integration suite
/// (already runs the proxy out-of-process so AWS SDK works). We only add
/// the <c>AWS2AZURE_PERF=1</c> gate so the heavy Event Hubs emulator
/// bring-up doesn't fire under a plain <c>dotnet test</c> at the
/// solution level.
/// </summary>
public sealed class KinesisPerfFixture : IAsyncLifetime
{
    private readonly KinesisEmulatorProxyFixture _inner = new();

    public bool Ready { get; private set; }
    public string? SkipReason { get; private set; }
    public KinesisEmulatorProxyFixture Inner => _inner;

    public async Task InitializeAsync()
    {
        if (!PerfGate.Enabled)
        {
            SkipReason = "AWS2AZURE_PERF=1 not set.";
            return;
        }
        await _inner.InitializeAsync().ConfigureAwait(false);
        if (!_inner.DockerAvailable)
        {
            SkipReason = _inner.SkipReason ?? "Docker not available for Kinesis fixture.";
            return;
        }
        Ready = true;
    }

    public Task DisposeAsync() => _inner.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class KinesisPerfCollection : ICollectionFixture<KinesisPerfFixture>
{
    public const string Name = "kinesis-perf";
}

[Collection(KinesisPerfCollection.Name)]
public sealed class KinesisPerfTests(KinesisPerfFixture fixture)
{
    [SkippableFact]
    public async Task PutRecord_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.Inner.CreateClient();
        var payload = Encoding.UTF8.GetBytes(new string('x', 256));

        var result = await PerfRunner.RunAsync(
            scenario: "kinesis.PutRecord (256 B)",
            concurrency: 16,
            duration: TimeSpan.FromSeconds(20),
            warmup: TimeSpan.FromSeconds(3),
            action: async (workerId, ct) =>
            {
                using var ms = new MemoryStream(payload, writable: false);
                await client.PutRecordAsync(new PutRecordRequest
                {
                    StreamName = KinesisEmulatorProxyFixture.StreamName,
                    PartitionKey = "perf-w" + workerId,
                    Data = ms,
                }, ct).ConfigureAwait(false);
            });

        PerfReport.Append(result, notes: "Kinesis→EventHubs(AMQP) emulator");
        Assert.True(result.Completed > 0, $"No completions. Failures={result.Failures}");
    }
}
