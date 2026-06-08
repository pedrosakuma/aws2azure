using System.Globalization;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Relative perf gate: compares each proxy scenario against its paired
/// <c>azure-sdk.*</c> baseline (same operation, same emulator, no proxy in the
/// path) and fails only when the proxy's throughput drops below — or its p99
/// climbs above — a configured multiple of the baseline.
///
/// <para>Why relative: the emulators (Service Bus / Event Hubs AMQP especially)
/// exhibit multi-second cold-connect tail-latency stalls that move p99 by 20×
/// over a short window. That jitter hits the proxy AND its SDK baseline equally,
/// so the ratio cancels it — the gate fires only on genuine proxy overhead, not
/// emulator noise. This is the meaningful signal the absolute floors/ceilings in
/// <c>baseline-reference.json</c> could never provide on emulator-bound paths.</para>
///
/// <para>The evaluation is a pure function over already-captured results so it is
/// unit-testable without an emulator; the xUnit wrapper
/// (<see cref="RelativeRegressionGateTests"/>) only loads the files and renders
/// the report.</para>
/// </summary>
internal static class RelativeRegressionGate
{
    /// <summary>
    /// Cross-run guard: a proxy row and its baseline row must have been captured
    /// within this window of each other to be compared. Prevents a fresh proxy
    /// row being judged against a stale baseline row left over from an earlier
    /// (e.g. partial local) run. A full CI perf run completes well inside this.
    /// </summary>
    public static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromHours(2);

    public static GateReport Evaluate(
        IReadOnlyDictionary<string, PerfResultRow> results,
        IReadOnlyDictionary<string, PerfBaselinePairing> pairings,
        TimeSpan? freshnessWindow = null)
    {
        var window = freshnessWindow ?? DefaultFreshnessWindow;
        var inv = CultureInfo.InvariantCulture;
        var violations = new List<string>();
        var checked_ = new List<string>();
        var skipped = new List<string>();

        foreach (var (proxyScenario, pairing) in pairings.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var baselineScenario = pairing.Baseline;

            if (!results.TryGetValue(proxyScenario, out var proxy))
            {
                skipped.Add($"{proxyScenario}: proxy result absent (scenario did not run)");
                continue;
            }
            if (!results.TryGetValue(baselineScenario, out var baseline))
            {
                skipped.Add($"{proxyScenario}: baseline '{baselineScenario}' result absent (scenario did not run)");
                continue;
            }
            if (proxy.Completed == 0 || baseline.Completed == 0)
            {
                skipped.Add($"{proxyScenario}: zero completions (proxy={proxy.Completed}, baseline={baseline.Completed})");
                continue;
            }

            var gap = (proxy.CapturedAtUtc - baseline.CapturedAtUtc).Duration();
            if (gap > window)
            {
                skipped.Add(string.Format(inv,
                    "{0}: proxy and baseline captured {1:0.0} h apart (> {2:0.0} h window) — not from the same run",
                    proxyScenario, gap.TotalHours, window.TotalHours));
                continue;
            }

            var problems = new List<string>();

            if (pairing.MinThroughputRatio > 0 && baseline.ThroughputPerSec > 0)
            {
                var floor = baseline.ThroughputPerSec * pairing.MinThroughputRatio;
                if (proxy.ThroughputPerSec < floor)
                {
                    problems.Add(string.Format(inv,
                        "throughput {0:0.0} ops/s < {1:0.0} ops/s ({2:0.##}× baseline {3:0.0})",
                        proxy.ThroughputPerSec, floor, pairing.MinThroughputRatio, baseline.ThroughputPerSec));
                }
            }

            if (pairing.MaxP50Ratio > 0 && baseline.P50Ms > 0)
            {
                var ceiling = baseline.P50Ms * pairing.MaxP50Ratio;
                if (proxy.P50Ms > ceiling)
                {
                    problems.Add(string.Format(inv,
                        "p50 {0:0.0} ms > {1:0.0} ms ({2:0.##}× baseline {3:0.0})",
                        proxy.P50Ms, ceiling, pairing.MaxP50Ratio, baseline.P50Ms));
                }
            }

            if (pairing.MaxP99Ratio > 0 && baseline.P99Ms > 0)
            {
                var ceiling = baseline.P99Ms * pairing.MaxP99Ratio;
                if (proxy.P99Ms > ceiling)
                {
                    problems.Add(string.Format(inv,
                        "p99 {0:0.0} ms > {1:0.0} ms ({2:0.##}× baseline {3:0.0})",
                        proxy.P99Ms, ceiling, pairing.MaxP99Ratio, baseline.P99Ms));
                }
            }

            if (problems.Count > 0)
            {
                violations.Add($"{proxyScenario} vs {baselineScenario}: " + string.Join("; ", problems));
            }
            else
            {
                checked_.Add(string.Format(inv,
                    "{0} vs {1}: throughput {2:0.0}/{3:0.0} ops/s, p50 {4:0.0}/{5:0.0} ms, p99 {6:0.0}/{7:0.0} ms — OK",
                    proxyScenario, baselineScenario,
                    proxy.ThroughputPerSec, baseline.ThroughputPerSec,
                    proxy.P50Ms, baseline.P50Ms, proxy.P99Ms, baseline.P99Ms));
            }
        }

        return new GateReport(violations, checked_, skipped);
    }
}

internal sealed record GateReport(
    IReadOnlyList<string> Violations,
    IReadOnlyList<string> Checked,
    IReadOnlyList<string> Skipped);
