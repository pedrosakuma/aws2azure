using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// Reference ceilings loaded from <c>docs/perf/footprint-reference.json</c>.
/// Used by <see cref="FootprintResult.AssertWithinBudget"/> to fail the run
/// when a footprint metric exceeds its committed ceiling. Scenarios absent from
/// the JSON are treated as not gated — they always pass so newly added
/// scenarios don't break CI before an operator captures a number. Mirrors
/// <c>Aws2Azure.PerfTests.PerfReferenceBaseline</c>.
/// </summary>
internal static class FootprintReferenceBaseline
{
    private static Lazy<FootprintBaselineDocument?> _doc = new(LoadOrNull);

    public static FootprintBaselineEntry? TryGet(string scenario)
    {
        var doc = _doc.Value;
        if (doc?.Scenarios is null) return null;
        return doc.Scenarios.TryGetValue(scenario, out var entry) ? entry : null;
    }

    internal static void ResetForTests() => _doc = new Lazy<FootprintBaselineDocument?>(LoadOrNull);

    private static FootprintBaselineDocument? LoadOrNull()
    {
        var path = ReferencePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FootprintBaselineJsonContext.Default.FootprintBaselineDocument);
    }

    public static string ReferencePath()
    {
        var overrideDir = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
        {
            return Path.Combine(overrideDir, "footprint-reference.json");
        }
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine(dir, "docs", "perf", "footprint-reference.json");
            }
            dir = Path.GetDirectoryName(dir);
        }
        return string.Empty;
    }
}

internal sealed class FootprintBaselineDocument
{
    [JsonPropertyName("scenarios")]
    public Dictionary<string, FootprintBaselineEntry>? Scenarios { get; set; }
}

internal sealed class FootprintBaselineEntry
{
    /// <summary>AOT binary size ceiling in MB. 0 opts out.</summary>
    [JsonPropertyName("maxBinarySizeMb")]
    public double MaxBinarySizeMb { get; set; }

    /// <summary>Idle resident-set ceiling in MB. 0 opts out.</summary>
    [JsonPropertyName("maxIdleRssMb")]
    public double MaxIdleRssMb { get; set; }

    /// <summary>Cold-start (median) ceiling in ms. 0 opts out.</summary>
    [JsonPropertyName("maxColdStartMs")]
    public double MaxColdStartMs { get; set; }

    /// <summary>
    /// Container image size ceiling in MB. 0 opts out; additionally only
    /// enforced when the run actually measured an image
    /// (<c>AWS2AZURE_FOOTPRINT_IMAGE</c> set to a built image).
    /// </summary>
    [JsonPropertyName("maxImageSizeMb")]
    public double MaxImageSizeMb { get; set; }
}

[JsonSerializable(typeof(FootprintBaselineDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal partial class FootprintBaselineJsonContext : JsonSerializerContext
{
}
