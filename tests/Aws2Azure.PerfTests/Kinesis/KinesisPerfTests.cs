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
    public string ProxyOutput => _inner.EmulatorLogs;

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

        // Optional diagnostics (opt-in, off by default — see issue #129):
        //   AWS2AZURE_PERF_CUSTOM_HTTP=1   wires a counting DelegatingHandler
        //                                  into the AWS SDK HttpClientFactory
        //                                  so we can compare HTTP calls vs
        //                                  AMQP sends (proves whether the SDK
        //                                  is retrying transparently).
        //   AWS2AZURE_AMQP_TIMING=1        flips per-send breadcrumbs in the
        //                                  proxy process (see AmqpTimingDiagnostics).
        //   AWS2AZURE_PROXY_LOG_DUMP=path  dumps the captured proxy stderr to a
        //                                  file for offline analysis.
        var useCustomHandler = string.Equals(
            Environment.GetEnvironmentVariable("AWS2AZURE_PERF_CUSTOM_HTTP"), "1", StringComparison.Ordinal);
        var httpCallCounter = useCustomHandler ? new HttpCallCounter() : null;
        using var client = fixture.Inner.CreateClient(httpCounter: httpCallCounter);
        var payload = Encoding.UTF8.GetBytes(new string('x', 256));

        // Closed-loop, single producer against one partition. Empirically
        // tracks the Azure SDK baseline (~80-100 ops/s against the EH
        // emulator on this hardware) so any future drop signals a real
        // proxy regression.
        var result = await PerfRunner.RunAsync(
            scenario: "kinesis.PutRecord (256 B)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(60),
            warmup: TimeSpan.Zero,
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

        var httpNote = httpCallCounter is null ? "n/a" : httpCallCounter.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PerfReport.Append(result, notes: $"Kinesis→EventHubs(AMQP) emulator — customHttp={useCustomHandler} HTTP={httpNote}");
        var dumpPath = Environment.GetEnvironmentVariable("AWS2AZURE_PROXY_LOG_DUMP");
        if (!string.IsNullOrEmpty(dumpPath))
        {
            await File.WriteAllTextAsync(dumpPath, fixture.ProxyOutput).ConfigureAwait(false);
        }
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
    }
}

/// <summary>
/// Diagnostics-only <see cref="System.Net.Http.DelegatingHandler"/> that
/// counts every outgoing HTTP request. Used to discriminate between an
/// AWS-SDK-side retry storm and a proxy-side amplification when a perf
/// number looks off (issue #129).
/// </summary>
internal sealed class HttpCallCounter : System.Net.Http.DelegatingHandler
{
    private long _count;
    public long Count => Interlocked.Read(ref _count);
    protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _count);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
