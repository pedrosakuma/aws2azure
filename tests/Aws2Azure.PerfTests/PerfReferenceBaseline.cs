using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Reference thresholds loaded from <c>docs/perf/baseline-reference.json</c>.
/// Used by <see cref="PerfResult.AssertNoRegression"/> to fail the run when a
/// scenario degrades beyond the committed floor / ceiling. Static scenarios
/// absent from the JSON fail their regression check; zero-value thresholds are
/// the explicit waiver used while an operator captures a reference number.
/// </summary>
internal static class PerfReferenceBaseline
{
    private static Lazy<PerfBaselineDocument?> _doc = new(LoadOrNull);

    public static PerfBaselineEntry? TryGet(string scenario)
    {
        var doc = _doc.Value;
        if (doc?.Scenarios is null) return null;
        return doc.Scenarios.TryGetValue(scenario, out var entry) ? entry : null;
    }

    /// <summary>
    /// Relative proxy-vs-baseline pairings keyed by proxy scenario name.
    /// Empty when the reference file is absent or declares no pairings.
    /// Consumed by <see cref="RelativeRegressionGate"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, PerfBaselinePairing> Pairings
        => _doc.Value?.Pairings ?? new Dictionary<string, PerfBaselinePairing>();

    // Test hook: clears the lazy cache so a freshly-written reference file is
    // picked up on the next TryGet. Not used by production scenarios.
    internal static void ResetForTests() => _doc = new Lazy<PerfBaselineDocument?>(LoadOrNull);

    private static PerfBaselineDocument? LoadOrNull()
    {
        var path = GetReferencePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, PerfBaselineJsonContext.Default.PerfBaselineDocument);
    }

    private static string GetReferencePath()
    {
        var overrideDir = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
        {
            // AWS2AZURE_PERF_DIR redirects where run RESULTS are written. A test
            // (or CI) may also drop a synthetic baseline-reference.json there to
            // inject thresholds — honor it when present. But the reference is a
            // COMMITTED config, not a run output: the real-Azure perf workflow
            // points AWS2AZURE_PERF_DIR at a results-only temp dir, so when the
            // override dir has no reference fall back to the repo's committed
            // docs/perf/baseline-reference.json instead of silently disabling the
            // scenario resource ceilings and the relative gate.
            var overridePath = Path.Combine(overrideDir, "baseline-reference.json");
            if (File.Exists(overridePath))
            {
                return overridePath;
            }
        }
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine(dir, "docs", "perf", "baseline-reference.json");
            }
            dir = Path.GetDirectoryName(dir);
        }
        return string.Empty;
    }
}

internal sealed class PerfBaselineDocument
{
    [JsonPropertyName("scenarios")]
    public Dictionary<string, PerfBaselineEntry>? Scenarios { get; set; }

    [JsonPropertyName("pairings")]
    public Dictionary<string, PerfBaselinePairing>? Pairings { get; set; }
}

internal sealed class PerfBaselineEntry
{
    [JsonPropertyName("minThroughputPerSec")]
    public double MinThroughputPerSec { get; set; }

    [JsonPropertyName("maxP99Ms")]
    public double MaxP99Ms { get; set; }

    /// <summary>
    /// Under-load peak working-set ceiling in MB for the proxy process (#274).
    /// 0 (default / absent) opts out — newly added scenarios stay non-gated on
    /// memory until an operator records a number. Only enforced when the run
    /// actually measured memory (perf probe reachable).
    /// </summary>
    [JsonPropertyName("maxPeakWorkingSetMb")]
    public double MaxPeakWorkingSetMb { get; set; }

    /// <summary>
    /// Optional ceiling on mean managed bytes allocated by the proxy per
    /// completed op over the measure window (#274). 0 opts out. This is the
    /// scenario-attributable allocation-churn signal (peak working set is a
    /// cumulative high-water mark across all scenarios in a shared proxy).
    /// </summary>
    [JsonPropertyName("maxAllocBytesPerOp")]
    public double MaxAllocBytesPerOp { get; set; }
}

/// <summary>
/// One relative gate: the named proxy scenario must stay within a multiple of
/// its <see cref="Baseline"/> azure-sdk.* scenario, which measures the same
/// operation against the same emulator with no proxy in the path. Because the
/// emulator's tail-latency jitter hits both sides, the ratio cancels the noise
/// and only fires when the proxy itself regresses. A 0 ratio opts out of that
/// dimension.
///
/// <para>Metric choice by path shape:</para>
/// <list type="bullet">
///   <item><b>REST + AMQP receive</b> pairs gate on <see cref="MaxP99Ratio"/>
///   (and usually <see cref="MinThroughputRatio"/>): their latency distribution
///   is unimodal so p99 is a stable signal.</item>
///   <item><b>AMQP send</b> pairs gate on <see cref="MaxP50Ratio"/> ONLY. A
///   send's distribution is bimodal — a steady mode plus rare multi-second cold
///   link-attach spikes — and which side those spikes land in p99 (vs max) is
///   essentially random per run and per pool/warmup dynamics, so the p99 ratio
///   swings wildly (observed 0.06×–11× between two structurally identical send
///   pairs in one run). The median ignores the cold-attach tail and captures the
///   real steady-state proxy overhead; throughput is likewise opted out because
///   cold stalls skew completions over a short window.</item>
/// </list>
/// </summary>
internal sealed class PerfBaselinePairing
{
    [JsonPropertyName("baseline")]
    public string Baseline { get; set; } = string.Empty;

    [JsonPropertyName("minThroughputRatio")]
    public double MinThroughputRatio { get; set; }

    [JsonPropertyName("maxP50Ratio")]
    public double MaxP50Ratio { get; set; }

    [JsonPropertyName("maxP99Ratio")]
    public double MaxP99Ratio { get; set; }
}

[JsonSerializable(typeof(PerfBaselineDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal partial class PerfBaselineJsonContext : JsonSerializerContext
{
}
