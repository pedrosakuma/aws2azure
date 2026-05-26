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
        var stopwatch = Stopwatch.StartNew();

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCts.CancelAfter(duration);

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

        var samples = latenciesUs.ToArray();
        Array.Sort(samples);
        return new PerfResult(
            Scenario: scenario,
            Concurrency: concurrency,
            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds,
            Completed: Interlocked.Read(ref completed),
            Failures: Interlocked.Read(ref failures),
            ThroughputPerSec: stopwatch.Elapsed.TotalSeconds > 0
                ? Interlocked.Read(ref completed) / stopwatch.Elapsed.TotalSeconds
                : 0,
            P50Us: Percentile(samples, 0.50),
            P95Us: Percentile(samples, 0.95),
            P99Us: Percentile(samples, 0.99),
            MaxUs: samples.Length == 0 ? 0 : samples[^1],
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
    Exception? FirstFailure = null)
{
    public double FailureRate => Completed + Failures == 0 ? 0 : (double)Failures / (Completed + Failures);

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
    /// Compares throughput and p99 against the committed reference in
    /// <c>docs/perf/baseline-reference.json</c>. No-op when the scenario is
    /// not listed there (newly added scenarios stay non-gated until an
    /// operator records a reference number).
    /// </summary>
    public void AssertNoRegression()
    {
        var entry = PerfReferenceBaseline.TryGet(Scenario);
        if (entry is null) return;

        var p99Ms = P99Us / 1000.0;
        var problems = new System.Collections.Generic.List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (entry.MinThroughputPerSec > 0 && ThroughputPerSec < entry.MinThroughputPerSec)
        {
            problems.Add(string.Format(inv,
                "throughput {0:0.0} ops/s < floor {1:0.0} ops/s",
                ThroughputPerSec, entry.MinThroughputPerSec));
        }
        if (entry.MaxP99Ms > 0 && p99Ms > entry.MaxP99Ms)
        {
            problems.Add(string.Format(inv,
                "p99 {0:0.0} ms > ceiling {1:0.0} ms",
                p99Ms, entry.MaxP99Ms));
        }
        if (problems.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"{Scenario}: regression — " + string.Join("; ", problems));
        }
    }
}
