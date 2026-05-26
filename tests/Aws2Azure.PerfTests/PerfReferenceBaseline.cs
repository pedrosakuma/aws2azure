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
}

internal sealed class PerfBaselineEntry
{
    [JsonPropertyName("minThroughputPerSec")]
    public double MinThroughputPerSec { get; set; }

    [JsonPropertyName("maxP99Ms")]
    public double MaxP99Ms { get; set; }
}

[JsonSerializable(typeof(PerfBaselineDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal partial class PerfBaselineJsonContext : JsonSerializerContext
{
}
