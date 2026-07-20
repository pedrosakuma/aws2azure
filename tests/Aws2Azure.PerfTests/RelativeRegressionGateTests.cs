using Xunit;
using Xunit.Abstractions;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Pure unit tests for <see cref="RelativeRegressionGate.Evaluate"/>. No
/// emulator, no files — exercises the comparison logic directly. Runs in the
/// scenario test step (not tagged <c>RelativeGate</c>).
/// </summary>
public sealed class RelativeRegressionGateEvaluatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PerfResultRow Row(double throughput, double p99Ms, double p50Ms = 0, long completed = 1000, DateTime? at = null)
        => new()
        {
            ThroughputPerSec = throughput,
            P50Ms = p50Ms,
            P99Ms = p99Ms,
            Completed = completed,
            CapturedAtUtc = at ?? T0,
        };

    private static PerfBaselinePairing Pair(string baseline, double minThroughputRatio, double maxP99Ratio, double maxP50Ratio = 0)
        => new()
        {
            Baseline = baseline,
            MinThroughputRatio = minThroughputRatio,
            MaxP50Ratio = maxP50Ratio,
            MaxP99Ratio = maxP99Ratio,
        };

    [Fact]
    public void Passes_when_proxy_within_both_ratios()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 80, p99Ms: 120),
            ["base"] = Row(throughput: 100, p99Ms: 100),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
        Assert.Single(report.Checked);
    }

    [Fact]
    public void Fails_when_throughput_below_ratio_floor()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 40, p99Ms: 100), // 0.4× baseline, floor is 0.5×
            ["base"] = Row(throughput: 100, p99Ms: 100),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        var v = Assert.Single(report.Violations);
        Assert.Contains("throughput", v);
    }

    [Fact]
    public void Fails_when_p99_above_ratio_ceiling()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 100, p99Ms: 260), // 2.6× baseline, ceiling is 2.0×
            ["base"] = Row(throughput: 100, p99Ms: 100),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        var v = Assert.Single(report.Violations);
        Assert.Contains("p99", v);
    }

    [Fact]
    public void Fails_when_p50_above_ratio_ceiling()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 100, p99Ms: 100, p50Ms: 60), // 6× baseline median, ceiling 5×
            ["base"] = Row(throughput: 100, p99Ms: 100, p50Ms: 10),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing>
        {
            ["proxy"] = Pair("base", minThroughputRatio: 0.0, maxP99Ratio: 0.0, maxP50Ratio: 5.0),
        };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        var v = Assert.Single(report.Violations);
        Assert.Contains("p50", v);
    }

    [Fact]
    public void Send_pair_gates_on_p50_and_ignores_the_bimodal_p99_tail()
    {
        // The real sqs.SendMessage signature: a cold AMQP link-attach spike
        // lands in the proxy's p99 (11× the baseline's) but the steady-state
        // median is healthy. A send pair gates on p50 ONLY (p99 opted out), so
        // this must PASS — gating it on p99 would be pure cold-attach noise.
        var results = new Dictionary<string, PerfResultRow>
        {
            ["sqs.SendMessage"] = Row(throughput: 113, p99Ms: 234, p50Ms: 28),
            ["sdk"] = Row(throughput: 95, p99Ms: 21, p50Ms: 8),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing>
        {
            ["sqs.SendMessage"] = Pair("sdk", minThroughputRatio: 0.0, maxP99Ratio: 0.0, maxP50Ratio: 5.0),
        };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
        Assert.Single(report.Checked);
    }

    [Theory]
    [InlineData("dynamodb", 858.7, 16.0, 73.1, 1094.0, 14.5, 21.4, 0.40, 0.0, 4.5)]
    [InlineData("dynamodb", 660.0, 21.2, 58.5, 1363.1, 11.6, 15.4, 0.40, 0.0, 4.5)]
    [InlineData("dynamodb", 817.8, 18.4, 42.9, 1435.9, 11.1, 13.5, 0.40, 0.0, 4.5)]
    [InlineData("s3", 351.1, 27.7, 282.6, 282.3, 25.6, 527.8, 0.50, 2.0, 0.0)]
    [InlineData("s3", 355.4, 40.0, 216.4, 358.1, 36.4, 112.5, 0.50, 2.0, 0.0)]
    [InlineData("s3", 406.8, 35.3, 99.9, 484.4, 26.5, 72.7, 0.50, 2.0, 0.0)]
    public void Calibrated_gate_accepts_observed_unchanged_code_runs(
        string scenario,
        double proxyThroughput,
        double proxyP50,
        double proxyP99,
        double baselineThroughput,
        double baselineP50,
        double baselineP99,
        double minThroughputRatio,
        double maxP50Ratio,
        double maxP99Ratio)
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            [scenario] = Row(proxyThroughput, proxyP99, proxyP50),
            ["sdk"] = Row(baselineThroughput, baselineP99, baselineP50),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing>
        {
            [scenario] = Pair(
                "sdk",
                minThroughputRatio,
                maxP99Ratio,
                maxP50Ratio),
        };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
        Assert.Single(report.Checked);
    }

    [Theory]
    [InlineData(2.01, 0.0)]
    [InlineData(1.0, 4.51)]
    public void Calibrated_latency_gate_rejects_a_ratio_above_its_ceiling(
        double proxyP50Ratio,
        double proxyP99Ratio)
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(
                throughput: 100,
                p99Ms: proxyP99Ratio == 0 ? 100 : proxyP99Ratio * 100,
                p50Ms: proxyP50Ratio * 100),
            ["sdk"] = Row(throughput: 100, p99Ms: 100, p50Ms: 100),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing>
        {
            ["proxy"] = Pair(
                "sdk",
                minThroughputRatio: 0,
                maxP99Ratio: proxyP99Ratio == 0 ? 0 : 4.5,
                maxP50Ratio: proxyP99Ratio == 0 ? 2.0 : 0),
        };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Single(report.Violations);
    }

    [Fact]
    public void Zero_p50_ratio_opts_out_of_median_half()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 100, p99Ms: 100, p50Ms: 9999), // huge median, but p50 ratio disabled
            ["base"] = Row(throughput: 100, p99Ms: 100, p50Ms: 10),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
    }

    [Fact]
    public void Emulator_jitter_on_both_sides_cancels_out()
    {
        // Baseline p99 ballooned to 10 s by an emulator cold-connect stall; the
        // proxy is a healthy 0.5 s. A relative p99 gate must PASS — this is the
        // whole point. (An absolute 800 ms ceiling would have failed it.)
        var results = new Dictionary<string, PerfResultRow>
        {
            ["sns.Publish"] = Row(throughput: 130, p99Ms: 500),
            ["sdk"] = Row(throughput: 33, p99Ms: 10000),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing>
        {
            ["sns.Publish"] = Pair("sdk", minThroughputRatio: 0.0, maxP99Ratio: 1.5),
        };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
        Assert.Single(report.Checked);
    }

    [Fact]
    public void Zero_throughput_ratio_opts_out_of_throughput_half()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 1, p99Ms: 100), // tiny throughput, but ratio disabled
            ["base"] = Row(throughput: 100, p99Ms: 100),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.0, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
    }

    [Fact]
    public void Skips_when_baseline_result_absent()
    {
        var results = new Dictionary<string, PerfResultRow> { ["proxy"] = Row(80, 120) };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
        Assert.Empty(report.Checked);
        Assert.Single(report.Skipped);
    }

    [Fact]
    public void Skips_when_proxy_result_absent()
    {
        var results = new Dictionary<string, PerfResultRow> { ["base"] = Row(100, 100) };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
        Assert.Single(report.Skipped);
    }

    [Fact]
    public void Skips_when_rows_are_from_different_runs()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 10, p99Ms: 100, at: T0), // would fail throughput…
            ["base"] = Row(throughput: 100, p99Ms: 100, at: T0.AddHours(5)), // …but 5 h apart
        };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings, freshnessWindow: TimeSpan.FromHours(2));

        Assert.Empty(report.Violations);
        Assert.Single(report.Skipped);
    }

    [Fact]
    public void Skips_when_either_side_has_zero_completions()
    {
        var results = new Dictionary<string, PerfResultRow>
        {
            ["proxy"] = Row(throughput: 0, p99Ms: 0, completed: 0),
            ["base"] = Row(throughput: 100, p99Ms: 100),
        };
        var pairings = new Dictionary<string, PerfBaselinePairing> { ["proxy"] = Pair("base", 0.5, 2.0) };

        var report = RelativeRegressionGate.Evaluate(results, pairings);

        Assert.Empty(report.Violations);
        Assert.Single(report.Skipped);
    }

    [Theory]
    // (hasViolation, reportOnly) -> blocks?
    [InlineData(true, false, true)]   // breach + normal (emulator) run -> fail the build
    [InlineData(true, true, false)]   // breach + real-Azure report-only -> do NOT fail
    [InlineData(false, false, false)] // no breach -> never fails
    [InlineData(false, true, false)]  // no breach, report-only -> never fails
    public void IsBlockingFailure_only_when_breach_and_not_report_only(
        bool hasViolation, bool reportOnly, bool expectedBlocks)
    {
        var violations = hasViolation ? new[] { "proxy ratio breach" } : System.Array.Empty<string>();
        var report = new GateReport(violations, System.Array.Empty<string>(), System.Array.Empty<string>());

        Assert.Equal(expectedBlocks, report.IsBlockingFailure(reportOnly));
    }
}

/// <summary>
/// The end-to-end relative gate, run as a dedicated second <c>dotnet test</c>
/// step (filter <c>Category=RelativeGate</c>) AFTER all scenarios have written
/// <c>baseline-latest.json</c>. Splitting it into its own invocation sidesteps
/// xUnit's lack of cross-collection ordering — by the time this runs every
/// scenario row is on disk.
/// </summary>
[Trait("Category", "RelativeGate")]
public sealed class RelativeRegressionGateTests
{
    private readonly ITestOutputHelper _output;

    public RelativeRegressionGateTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void Proxy_stays_within_baseline_ratios()
    {
        var path = PerfReport.GetResultsJsonPath();
        Skip.IfNot(File.Exists(path),
            $"No perf results at {path} — the scenario step did not run (emulator unavailable?). Nothing to gate.");

        var doc = PerfResultsFile.LoadOrEmpty(path);
        Skip.If(doc.Scenarios.Count == 0, "Perf results file is empty — nothing to gate.");

        var report = RelativeRegressionGate.Evaluate(doc.Scenarios, PerfReferenceBaseline.Pairings);

        foreach (var ok in report.Checked) _output.WriteLine("OK    " + ok);
        foreach (var skip in report.Skipped) _output.WriteLine("SKIP  " + skip);
        foreach (var bad in report.Violations) _output.WriteLine("FAIL  " + bad);

        // Tier 2 / real-Azure (#420): the relative ratios in baseline-reference.json
        // are calibrated against the EMULATOR. The proxy-vs-SDK ratio is the right
        // signal on real Azure too (it cancels network/throttle noise by hitting
        // both sides), but until enough real-Azure runs accrue to set a deliberate,
        // data-backed real-Azure ratio, the real-Azure workflow runs this gate in
        // REPORT-ONLY mode: the OK/SKIP/FAIL lines above are captured as artifacts
        // for the A/B, but a ratio breach does not fail the (weekly) job. This
        // mirrors the repo convention that new/untuned scenarios pass through
        // silently until an operator bumps the reference deliberately. Flip the
        // flag off in the workflow once a real-Azure ratio floor is established.
        bool reportOnly = string.Equals(
            Environment.GetEnvironmentVariable("AWS2AZURE_PERF_RELATIVE_REPORT_ONLY"),
            "1", StringComparison.Ordinal);

        if (!report.IsBlockingFailure(reportOnly))
        {
            if (reportOnly && report.Violations.Count > 0)
            {
                _output.WriteLine(
                    "[REPORT-ONLY] Relative ratios exceeded the emulator-tuned baseline but the build "
                    + "is NOT failed (AWS2AZURE_PERF_RELATIVE_REPORT_ONLY=1). Real-Azure ratios are "
                    + "informational until a deliberate real-Azure floor is set.");
            }
            return;
        }

        Assert.True(report.Violations.Count == 0,
            "Relative perf regression — the proxy exceeded its SDK-baseline ratio on the same emulator:\n  - "
            + string.Join("\n  - ", report.Violations)
            + "\n\nThis gate cancels emulator jitter (it hits both sides), so a failure here is a genuine "
            + "proxy-overhead regression, not flakiness. Bump the ratio in docs/perf/baseline-reference.json "
            + "only if the change is a deliberate, understood trade-off.");
    }
}
