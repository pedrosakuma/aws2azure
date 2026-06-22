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

        // Readiness gate (#436 signature 1): the AMQP sender link for the
        // measured partition is attached lazily on the first send to it. A cold
        // attach can take long enough to swallow the entire measure window —
        // observed as 60 s of zero completions AND zero failures (the single
        // in-flight PutRecord was still attaching when the window cancelled).
        // Warm the exact partition key worker 0 will use BEFORE the timed
        // window so the closed loop can't measure an empty window.
        await WarmUpPartitionAsync(client, payload, PartitionKeyFor(0), TimeSpan.FromSeconds(30));

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
                    PartitionKey = PartitionKeyFor(workerId),
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
        result.AssertNoRegression();
    }

    private static string PartitionKeyFor(int workerId) => "perf-w" + workerId;

    private static string BatchPartitionKeyFor(int workerId, int recordIndex) => $"perf-w{workerId}-r{recordIndex}";

    private static List<PutRecordsRequestEntry> CreatePutRecordsEntries(byte[] payload, int workerId, int recordsPerCall)
    {
        var entries = new List<PutRecordsRequestEntry>(recordsPerCall);
        for (var i = 0; i < recordsPerCall; i++)
        {
            entries.Add(new PutRecordsRequestEntry
            {
                Data = new MemoryStream(payload, writable: false),
                PartitionKey = BatchPartitionKeyFor(workerId, i),
            });
        }

        return entries;
    }

    /// <summary>
    /// Issues PutRecord to <paramref name="partitionKey"/> with bounded retries
    /// until one succeeds, attaching that partition's AMQP sender link OUTSIDE
    /// the measured window (see #436 signature 1). Throws if readiness can't be
    /// reached within <paramref name="timeout"/> so a genuinely broken transport
    /// still fails loudly rather than masquerading as an empty window.
    /// </summary>
    private static async Task WarmUpPartitionAsync(
        Amazon.Kinesis.IAmazonKinesis client, byte[] payload, string partitionKey, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        TimeSpan remaining;
        while ((remaining = deadline - DateTime.UtcNow) > TimeSpan.Zero)
        {
            // Bound each attempt by the remaining deadline so a hung cold
            // link-attach (the very failure this gate guards against) can't
            // outlast the timeout — the gate must fail loudly, not hang.
            using var attemptCts = new CancellationTokenSource(remaining);
            try
            {
                using var ms = new MemoryStream(payload, writable: false);
                await client.PutRecordAsync(new PutRecordRequest
                {
                    StreamName = KinesisEmulatorProxyFixture.StreamName,
                    PartitionKey = partitionKey,
                    Data = ms,
                }, attemptCts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { await Task.Delay(500, attemptCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Kinesis PutRecord readiness gate timed out after {timeout.TotalSeconds:0}s warming partition " +
            $"'{partitionKey}'. Last error: {lastError?.Message}");
    }

    /// <summary>
    /// Issues one PutRecords batch for the measured worker's exact partition
    /// keys with bounded retries until all records succeed, attaching those
    /// AMQP sender links OUTSIDE the measured window (see #436 signature 1).
    /// Throws if readiness can't be reached within <paramref name="timeout"/>
    /// so a genuinely broken transport still fails loudly rather than
    /// masquerading as an empty window.
    /// </summary>
    private static async Task WarmUpBatchPartitionsAsync(
        Amazon.Kinesis.IAmazonKinesis client, byte[] payload, int workerId, int recordsPerCall, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        TimeSpan remaining;
        while ((remaining = deadline - DateTime.UtcNow) > TimeSpan.Zero)
        {
            // Bound each attempt by the remaining deadline so a hung cold
            // link-attach (the very failure this gate guards against) can't
            // outlast the timeout — the gate must fail loudly, not hang.
            using var attemptCts = new CancellationTokenSource(remaining);
            try
            {
                var resp = await client.PutRecordsAsync(new PutRecordsRequest
                {
                    StreamName = KinesisEmulatorProxyFixture.StreamName,
                    Records = CreatePutRecordsEntries(payload, workerId, recordsPerCall),
                }, attemptCts.Token).ConfigureAwait(false);
                if (resp.FailedRecordCount == 0)
                {
                    return;
                }

                lastError = new InvalidOperationException($"PutRecords readiness: {resp.FailedRecordCount} failed.");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try { await Task.Delay(500, attemptCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        throw new Xunit.Sdk.XunitException(
            $"Kinesis PutRecords readiness gate timed out after {timeout.TotalSeconds:0}s warming " +
            $"{recordsPerCall} partitions for worker {workerId}. Last error: {lastError?.Message}");
    }

    [SkippableFact]
    public async Task PutRecords_batch_throughput()
    {
        Skip.IfNot(fixture.Ready, fixture.SkipReason);

        using var client = fixture.Inner.CreateClient();
        var payload = Encoding.UTF8.GetBytes(new string('y', 256));
        const int recordsPerCall = 25;

        // Readiness gate (#436 signature 1): PutRecords uses 25 distinct
        // partition keys for worker 0, and the Event Hubs emulator attaches
        // AMQP sender links lazily on first send to each key. Warm those exact
        // keys before the timed window so the closed loop can't measure an
        // empty window while the first batch is still attaching.
        await WarmUpBatchPartitionsAsync(client, payload, workerId: 0, recordsPerCall, TimeSpan.FromSeconds(30));

        var result = await PerfRunner.RunAsync(
            scenario: "kinesis.PutRecords (25×256 B)",
            concurrency: 1,
            duration: TimeSpan.FromSeconds(30),
            warmup: TimeSpan.Zero,
            action: async (workerId, ct) =>
            {
                var resp = await client.PutRecordsAsync(new PutRecordsRequest
                {
                    StreamName = KinesisEmulatorProxyFixture.StreamName,
                    Records = CreatePutRecordsEntries(payload, workerId, recordsPerCall),
                }, ct).ConfigureAwait(false);
                if (resp.FailedRecordCount > 0)
                {
                    throw new InvalidOperationException($"PutRecords: {resp.FailedRecordCount} failed.");
                }
            });

        PerfReport.Append(result, notes: $"Kinesis→EventHubs(AMQP) emulator — PutRecords ({recordsPerCall} records/call)");
        result.AssertHealthy(proxyOutput: fixture.ProxyOutput);
        result.AssertNoRegression();
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
