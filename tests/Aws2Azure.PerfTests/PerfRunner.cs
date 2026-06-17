using System.Diagnostics;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Closed-loop concurrent perf driver. Spawns <paramref name="concurrency"/>
/// worker tasks that invoke <paramref name="action"/> in a tight loop until
/// either <paramref name="duration"/> elapses or <paramref name="maxOps"/>
/// operations have completed across all workers. Captures per-call latency
/// in microseconds and aggregates throughput + percentile stats.
///
/// <para>Failures are counted and re-thrown summary-style — the harness is
/// not designed to mask broken transport. A handful of trailing failures
/// during shutdown are tolerated (recorded but not fatal).</para>
/// </summary>
internal static class PerfRunner
{
    public static async Task<PerfResult> RunAsync(
        string scenario,
        int concurrency,
        TimeSpan duration,
        Func<int, CancellationToken, Task> action,
        TimeSpan? warmup = null,
        int? maxOps = null,
        ProxyMemoryProbe? memoryProbe = null,
        CancellationToken cancellationToken = default)
    {
        // Warmup — closed-loop at concurrency 1 (enough to JIT hot paths and
        // open the AMQP/HTTP connection pool, without flooding the backend
        // before the measure window starts). Failures discarded.
        if (warmup is { } warmupSpan && warmupSpan > TimeSpan.Zero)
        {
            using var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            warmupCts.CancelAfter(warmupSpan);
            try
            {
                while (!warmupCts.IsCancellationRequested)
                {
                    try { await action(-1, warmupCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (warmupCts.IsCancellationRequested) { break; }
                    catch { /* warmup failures swallowed */ }
                }
            }
            catch (OperationCanceledException) { }
        }

        var latenciesUs = new System.Collections.Concurrent.ConcurrentQueue<long>();
        long completed = 0;
        long failures = 0;
        Exception? firstFailure = null;

        // Memory characterization (#274). Capture a baseline snapshot of the
        // proxy's self-reported runtime gauges, sample its working set during the
        // measure window, then a final snapshot to diff cumulative allocated bytes
        // and gen2 collections. Best-effort: if the probe is absent or the scrape
        // fails the scenario still runs, with memory simply marked unmeasured.
        var baselineSnapshot = memoryProbe is null
            ? null
            : await memoryProbe.SampleAsync(cancellationToken).ConfigureAwait(false);
        long peakWorkingSet = baselineSnapshot?.WorkingSetBytes ?? 0;
        long peakGcHeap = baselineSnapshot?.GcHeapBytes ?? 0;
        var sampleLock = new object();

        var stopwatch = Stopwatch.StartNew();

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCts.CancelAfter(duration);

        Task? sampler = null;
        using var samplerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (memoryProbe is not null && baselineSnapshot is not null)
        {
            var probe = memoryProbe;
            sampler = Task.Run(async () =>
            {
                while (!samplerCts.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(200), samplerCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    var snap = await probe.SampleAsync(samplerCts.Token).ConfigureAwait(false);
                    if (snap is { } s)
                    {
                        lock (sampleLock)
                        {
                            if (s.WorkingSetBytes > peakWorkingSet) peakWorkingSet = s.WorkingSetBytes;
                            if (s.GcHeapBytes > peakGcHeap) peakGcHeap = s.GcHeapBytes;
                        }
                    }
                }
            }, samplerCts.Token);
        }

        var workers = Enumerable.Range(0, concurrency).Select(workerId => Task.Run(async () =>
        {
            var sw = new Stopwatch();
            while (!runCts.IsCancellationRequested)
            {
                if (maxOps is { } cap && Interlocked.Read(ref completed) >= cap)
                {
                    break;
                }
                sw.Restart();
                try
                {
                    await action(workerId, runCts.Token).ConfigureAwait(false);
                    sw.Stop();
                    latenciesUs.Enqueue(sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);
                    Interlocked.Increment(ref completed);
                }
                catch (OperationCanceledException) when (runCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    Interlocked.Increment(ref failures);
                }
            }
        }, runCts.Token)).ToArray();

        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        stopwatch.Stop();

        // Stop sampling and take a final snapshot to diff cumulative counters.
        samplerCts.Cancel();
        if (sampler is not null)
        {
            try { await sampler.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        bool memoryMeasured = false;
        long allocatedBytesDelta = 0;
        long gen2Collections = 0;
        if (baselineSnapshot is { } baseline && memoryProbe is not null)
        {
            var finalSnapshot = await memoryProbe.SampleAsync(cancellationToken).ConfigureAwait(false);
            if (finalSnapshot is { } final)
            {
                memoryMeasured = true;
                if (final.WorkingSetBytes > peakWorkingSet) peakWorkingSet = final.WorkingSetBytes;
                if (final.GcHeapBytes > peakGcHeap) peakGcHeap = final.GcHeapBytes;
                allocatedBytesDelta = Math.Max(0, final.AllocatedBytesTotal - baseline.AllocatedBytesTotal);
                gen2Collections = Math.Max(0, final.Gen2Collections - baseline.Gen2Collections);
            }
        }

        var samples = latenciesUs.ToArray();
        Array.Sort(samples);
        var completedCount = Interlocked.Read(ref completed);
        return new PerfResult(
            Scenario: scenario,
            Concurrency: concurrency,
            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
            Completed: completedCount,
            Failures: Interlocked.Read(ref failures),
            ThroughputPerSec: stopwatch.Elapsed.TotalSeconds > 0
                ? completedCount / stopwatch.Elapsed.TotalSeconds
                : 0,
            P50Us: Percentile(samples, 0.50),
            P95Us: Percentile(samples, 0.95),
            P99Us: Percentile(samples, 0.99),
            MaxUs: samples.Length == 0 ? 0 : samples[^1],
            MemoryMeasured: memoryMeasured,
            PeakWorkingSetBytes: memoryMeasured ? peakWorkingSet : 0,
            PeakGcHeapBytes: memoryMeasured ? peakGcHeap : 0,
            AllocatedBytesDelta: allocatedBytesDelta,
            Gen2Collections: gen2Collections,
            FirstFailure: firstFailure);
    }

    private static long Percentile(long[] sortedSamples, double p)
    {
        if (sortedSamples.Length == 0) return 0;
        var rank = (int)Math.Clamp(Math.Ceiling(p * sortedSamples.Length) - 1, 0, sortedSamples.Length - 1);
        return sortedSamples[rank];
    }
}

internal sealed record PerfResult(
    string Scenario,
    int Concurrency,
    double ElapsedSeconds,
    long Completed,
    long Failures,
    double ThroughputPerSec,
    long P50Us,
    long P95Us,
    long P99Us,
    long MaxUs,
    bool MemoryMeasured = false,
    long PeakWorkingSetBytes = 0,
    long PeakGcHeapBytes = 0,
    long AllocatedBytesDelta = 0,
    long Gen2Collections = 0,
    Exception? FirstFailure = null)
{
    public double FailureRate => Completed + Failures == 0 ? 0 : (double)Failures / (Completed + Failures);

    public double PeakWorkingSetMb => PeakWorkingSetBytes / (1024.0 * 1024.0);

    /// <summary>Mean managed bytes allocated by the proxy per completed op over the measure window.</summary>
    public double AllocBytesPerOp => Completed > 0 ? (double)AllocatedBytesDelta / Completed : 0;

    public void AssertHealthy(double maxFailureRate = 0.10, string? proxyOutput = null)
    {
        if (Completed == 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"{Scenario}: no completions. Failures={Failures}. FirstFailure={FirstFailure}\nProxy stdout:\n{proxyOutput}");
        }
        if (FailureRate > maxFailureRate)
        {
            throw new Xunit.Sdk.XunitException(
                $"{Scenario}: failure rate {FailureRate:P1} exceeds budget {maxFailureRate:P1}. " +
                $"Completed={Completed} Failures={Failures}. FirstFailure={FirstFailure}\nProxy stdout:\n{proxyOutput}");
        }
    }

    /// <summary>
    /// Compares throughput, p99, and under-load memory against the committed
    /// reference in <c>docs/perf/baseline-reference.json</c>. No-op when the
    /// scenario is not listed there (newly added scenarios stay non-gated until
    /// an operator records a reference number). Each individual ceiling/floor
    /// is also independently opt-out: a value of 0 disables that dimension, and
    /// the memory ceilings additionally no-op when memory was not measured for
    /// the run (probe absent or unreachable).
    /// <para>When <c>AWS2AZURE_PERF_RESOURCE_ONLY=1</c> (the Tier 2 real-Azure
    /// regime, issue #420) the throughput-floor and p99-ceiling dimensions are
    /// skipped because they are network-bound against live Azure and would flap;
    /// only the backend-independent resource ceilings (alloc/op, peak working
    /// set) are enforced. The reference floors are emulator-derived, so absolute
    /// latency/throughput gating against real Azure is never meaningful.</para>
    /// </summary>
    public void AssertNoRegression()
    {
        var entry = PerfReferenceBaseline.TryGet(Scenario);
        if (entry is null) return;

        var resourceOnly = string.Equals(
            Environment.GetEnvironmentVariable("AWS2AZURE_PERF_RESOURCE_ONLY"), "1",
            StringComparison.Ordinal);

        var p99Ms = P99Us / 1000.0;
        var problems = new System.Collections.Generic.List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!resourceOnly && entry.MinThroughputPerSec > 0 && ThroughputPerSec < entry.MinThroughputPerSec)
        {
            problems.Add(string.Format(inv,
                "throughput {0:0.0} ops/s < floor {1:0.0} ops/s",
                ThroughputPerSec, entry.MinThroughputPerSec));
        }
        if (!resourceOnly && entry.MaxP99Ms > 0 && p99Ms > entry.MaxP99Ms)
        {
            problems.Add(string.Format(inv,
                "p99 {0:0.0} ms > ceiling {1:0.0} ms",
                p99Ms, entry.MaxP99Ms));
        }
        if (MemoryMeasured && entry.MaxPeakWorkingSetMb > 0 && PeakWorkingSetMb > entry.MaxPeakWorkingSetMb)
        {
            problems.Add(string.Format(inv,
                "peak working set {0:0.0} MB > ceiling {1:0.0} MB",
                PeakWorkingSetMb, entry.MaxPeakWorkingSetMb));
        }
        if (MemoryMeasured && entry.MaxAllocBytesPerOp > 0 && AllocBytesPerOp > entry.MaxAllocBytesPerOp)
        {
            problems.Add(string.Format(inv,
                "alloc {0:0} B/op > ceiling {1:0} B/op",
                AllocBytesPerOp, entry.MaxAllocBytesPerOp));
        }
        if (problems.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"{Scenario}: regression — " + string.Join("; ", problems));
        }
    }
}
