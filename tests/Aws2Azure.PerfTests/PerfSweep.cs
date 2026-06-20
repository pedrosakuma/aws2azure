using System.Globalization;
using System.Text;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Concurrency-saturation sweep on top of <see cref="PerfRunner"/> (issue #420,
/// Tier 2). A single fixed-concurrency closed-loop measurement obeys Little's law
/// — throughput = concurrency / latency — so at one worker count it cannot tell
/// whether the <em>proxy</em> or the <em>harness</em> is the binding constraint.
/// The sweep instead drives the same workload up a concurrency ladder until
/// throughput stops climbing, exposing the <b>knee</b>: the smallest concurrency
/// that reaches (within a tolerance) the maximum sustained throughput. Beyond the
/// knee, added workers only inflate latency.
///
/// <para>This is what makes the Tier 2 real-Azure run an <i>honest falsifier</i>
/// of a CPU/alloc optimization: under a CPU-constrained sidecar
/// (<c>AWS2AZURE_PERF_PROXY_CPUS</c>) the A/B is compared at the knee — max
/// sustained throughput and p99-under-load — not at an arbitrary low concurrency
/// where the path is network-bound and any CPU win is invisible. If the optimized
/// arm does not raise throughput-at-knee nor lower p99-at-knee, the optimization
/// is rejected <i>for that deployment shape</i> and documented as such.</para>
///
/// <para><see cref="DetectKnee"/> is a pure function over the per-level points so
/// it is unit-testable with synthetic curves and no backend.</para>
/// </summary>
internal static class PerfSweep
{
    /// <summary>
    /// Fraction of the observed maximum throughput a level must reach to count as
    /// "saturated". The knee is the smallest concurrency at or above this fraction
    /// — i.e. the cheapest worker count that already buys ~all the throughput the
    /// proxy can sustain in this regime.
    /// </summary>
    public const double DefaultKneeFraction = 0.95;

    /// <summary>
    /// Runs <paramref name="action"/> at each concurrency in <paramref name="levels"/>
    /// (ascending) for <paramref name="perLevelDuration"/> each, sharing one warmup
    /// before the first level, and returns the per-level results plus the detected
    /// knee. Levels are de-duplicated and sorted; non-positive levels are dropped.
    /// </summary>
    public static async Task<PerfSweepResult> RunSweepAsync(
        string scenario,
        IReadOnlyList<int> levels,
        TimeSpan perLevelDuration,
        Func<int, CancellationToken, Task> action,
        TimeSpan? warmup = null,
        double kneeFraction = DefaultKneeFraction,
        Func<ProxyMemoryProbe?>? memoryProbeFactory = null,
        CancellationToken cancellationToken = default)
    {
        var ladder = levels
            .Where(c => c > 0)
            .Distinct()
            .OrderBy(c => c)
            .ToArray();
        if (ladder.Length == 0)
        {
            throw new ArgumentException("Sweep requires at least one positive concurrency level.", nameof(levels));
        }

        var results = new List<PerfResult>(ladder.Length);
        for (var i = 0; i < ladder.Length; i++)
        {
            // Warmup once, before the first level only: the connection pool and JIT
            // are hot for every subsequent level, so re-warming would just burn
            // backend budget. Each level keeps the same scenario base name with the
            // concurrency appended so the rows stay distinct in the report.
            using var probe = memoryProbeFactory?.Invoke();
            var levelResult = await PerfRunner.RunAsync(
                scenario: $"{scenario} (sweep c={ladder[i].ToString(CultureInfo.InvariantCulture)})",
                concurrency: ladder[i],
                duration: perLevelDuration,
                action: action,
                warmup: i == 0 ? warmup : null,
                memoryProbe: probe,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            results.Add(levelResult);
        }

        var knee = DetectKnee(
            results.Select(r => (r.Concurrency, r.ThroughputPerSec, r.P99Us / 1000.0)).ToArray(),
            kneeFraction);

        return new PerfSweepResult(scenario, results, knee);
    }

    /// <summary>
    /// Pure knee detection over <c>(concurrency, throughput, p99Ms)</c> points.
    /// The knee is the smallest concurrency whose throughput is at least
    /// <paramref name="kneeFraction"/> of the maximum observed throughput.
    /// <c>ReachedSaturation</c> is true only when the ladder extended <i>beyond</i>
    /// the knee (so the plateau was actually observed); when the peak is at the
    /// highest level tested, throughput was still climbing and the ladder was too
    /// short to find saturation — reported honestly rather than guessed.
    /// </summary>
    public static SweepKnee DetectKnee(
        IReadOnlyList<(int Concurrency, double ThroughputPerSec, double P99Ms)> points,
        double kneeFraction = DefaultKneeFraction)
    {
        if (points.Count == 0)
        {
            return new SweepKnee(0, 0, 0, 0, 0, ReachedSaturation: false);
        }

        var sorted = points.OrderBy(p => p.Concurrency).ToArray();

        var maxThroughput = double.NegativeInfinity;
        var maxConcurrency = sorted[0].Concurrency;
        foreach (var p in sorted)
        {
            if (p.ThroughputPerSec > maxThroughput)
            {
                maxThroughput = p.ThroughputPerSec;
                maxConcurrency = p.Concurrency;
            }
        }

        if (maxThroughput <= 0)
        {
            var first = sorted[0];
            return new SweepKnee(first.Concurrency, first.ThroughputPerSec, first.P99Ms, 0, maxConcurrency, false);
        }

        var threshold = maxThroughput * kneeFraction;
        var knee = sorted.First(p => p.ThroughputPerSec >= threshold);
        var highestConcurrency = sorted[^1].Concurrency;

        // Saturation was observed iff we tested a concurrency strictly beyond the
        // knee. If the knee is the last rung, throughput had not flattened yet.
        var reachedSaturation = sorted.Length >= 2 && knee.Concurrency < highestConcurrency;

        return new SweepKnee(
            KneeConcurrency: knee.Concurrency,
            ThroughputAtKnee: knee.ThroughputPerSec,
            P99AtKneeMs: knee.P99Ms,
            MaxThroughput: maxThroughput,
            MaxThroughputConcurrency: maxConcurrency,
            ReachedSaturation: reachedSaturation);
    }
}

/// <summary>The saturation knee distilled from a concurrency sweep.</summary>
internal readonly record struct SweepKnee(
    int KneeConcurrency,
    double ThroughputAtKnee,
    double P99AtKneeMs,
    double MaxThroughput,
    int MaxThroughputConcurrency,
    bool ReachedSaturation);

/// <summary>Per-level results of a concurrency sweep plus its detected knee.</summary>
internal sealed record PerfSweepResult(
    string Scenario,
    IReadOnlyList<PerfResult> Levels,
    SweepKnee Knee)
{
    /// <summary>
    /// The level whose <see cref="PerfResult.Concurrency"/> equals the knee — the
    /// row worth recording for an A/B (throughput-at-knee + p99-at-knee). Falls
    /// back to the highest level if no exact match (defensive; should not happen).
    /// </summary>
    public PerfResult KneeLevel =>
        Levels.FirstOrDefault(l => l.Concurrency == Knee.KneeConcurrency) ?? Levels[^1];

    /// <summary>Human-readable one-paragraph summary for the test log.</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"sweep '{Scenario}': {Levels.Count} levels");
        foreach (var l in Levels)
        {
            sb.AppendLine(inv,
                $"  c={l.Concurrency,4}  tput={l.ThroughputPerSec,9:0.0}/s  p99={l.P99Us / 1000.0,7:0.0}ms  completed={l.Completed}");
        }
        sb.Append(inv,
            $"knee: c={Knee.KneeConcurrency} tput={Knee.ThroughputAtKnee:0.0}/s p99={Knee.P99AtKneeMs:0.0}ms; " +
            $"max tput={Knee.MaxThroughput:0.0}/s @ c={Knee.MaxThroughputConcurrency}; " +
            $"reachedSaturation={(Knee.ReachedSaturation ? "yes" : "no (ladder did not pass the knee)")}");
        return sb.ToString();
    }
}
