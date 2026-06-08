using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Reference thresholds loaded from <c>docs/perf/baseline-reference.json</c>.
/// Used by <see cref="PerfResult.AssertNoRegression"/> to fail the run when a
/// scenario degrades beyond the committed floor / ceiling. Scenarios absent
/// from the JSON are treated as not gated — they always pass the regression
/// check so newly added scenarios don't break CI before an operator captures
/// a reference number.
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
            return Path.Combine(overrideDir, "baseline-reference.json");
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
