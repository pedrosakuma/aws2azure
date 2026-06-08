using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Machine-readable sibling of <c>baseline-latest.md</c>: one row per scenario,
/// merged in place by scenario name exactly like the markdown snapshot. Written
/// by <see cref="PerfReport"/> during the scenario run and consumed by
/// <see cref="RelativeRegressionGate"/> in a second <c>dotnet test</c> step.
///
/// Not committed (see <c>.gitignore</c>) — it is a fresh per-run CI hand-off so
/// the relative gate never compares a fresh proxy row against a stale committed
/// baseline row. Each row is stamped with <see cref="PerfResultRow.CapturedAtUtc"/>
/// so the gate can additionally reject cross-run pairs via a freshness window.
/// </summary>
internal static class PerfResultsFile
{
    public static PerfResultsDocument LoadOrEmpty(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new PerfResultsDocument();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PerfResultsJsonContext.Default.PerfResultsDocument)
                   ?? new PerfResultsDocument();
        }
        catch (JsonException)
        {
            // Corrupt / partially-written file — start fresh rather than wedging the run.
            return new PerfResultsDocument();
        }
    }

    public static void Save(string path, PerfResultsDocument doc)
    {
        var json = JsonSerializer.Serialize(doc, PerfResultsJsonContext.Default.PerfResultsDocument);
        File.WriteAllText(path, json);
    }
}

internal sealed class PerfResultsDocument
{
    [JsonPropertyName("scenarios")]
    public Dictionary<string, PerfResultRow> Scenarios { get; set; } = new();
}

internal sealed class PerfResultRow
{
    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; }

    [JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds { get; set; }

    [JsonPropertyName("completed")]
    public long Completed { get; set; }

    [JsonPropertyName("failures")]
    public long Failures { get; set; }

    [JsonPropertyName("throughputPerSec")]
    public double ThroughputPerSec { get; set; }

    [JsonPropertyName("p50Ms")]
    public double P50Ms { get; set; }

    [JsonPropertyName("p95Ms")]
    public double P95Ms { get; set; }

    [JsonPropertyName("p99Ms")]
    public double P99Ms { get; set; }

    [JsonPropertyName("maxMs")]
    public double MaxMs { get; set; }

    [JsonPropertyName("capturedAtUtc")]
    public DateTime CapturedAtUtc { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

[JsonSerializable(typeof(PerfResultsDocument))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal partial class PerfResultsJsonContext : JsonSerializerContext
{
}
