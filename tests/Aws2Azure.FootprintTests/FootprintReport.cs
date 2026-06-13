using System.Globalization;
using System.Text;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// Persists <see cref="FootprintResult"/> rows to two files under
/// <c>docs/perf/</c>, mirroring <c>Aws2Azure.PerfTests.PerfReport</c>:
/// <list type="bullet">
///   <item><c>footprint-latest.md</c> — single-row-per-scenario human-readable
///   snapshot. Rows for the same scenario are replaced in place; other rows are
///   preserved so incremental refreshes merge cleanly.</item>
///   <item><c>footprint-history.csv</c> — cumulative append-only history.</item>
/// </list>
/// Footprint numbers are runner-bound, so each row records the RID.
/// </summary>
internal static class FootprintReport
{
    private static readonly object _lock = new();

    public static void Append(FootprintResult r, string? notes = null)
    {
        var mdPath = ReportPath("footprint-latest.md");
        var csvPath = ReportPath("footprint-history.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);

        var inv = CultureInfo.InvariantCulture;
        var imageCell = r.ImageMeasured ? string.Format(inv, "{0,7:0.0}", r.ImageSizeMb) : "    ---";
        var row = string.Format(inv,
            "| {0,-26} | {1,-10} | {2,9:0.0} | {3,8:0.0} | {4,10:0} | {5,7} | {6} |",
            r.Scenario, r.Rid, r.BinarySizeMb, r.IdleRssMb, r.ColdStartMedianMs, imageCell,
            notes ?? string.Empty);

        lock (_lock)
        {
            WriteMarkdownMerged(mdPath, r.Scenario, row);
            AppendHistoryCsv(csvPath, r, notes);
        }
    }

    private const string Header =
        "| Scenario                   | RID        | Binary MB | Idle RSS | Cold start | Image | Notes |";
    private const string Divider =
        "|----------------------------|------------|-----------|----------|------------|-------|-------|";

    private static void WriteMarkdownMerged(string path, string scenario, string row)
    {
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : new List<string>();

        if (lines.Count < 2 || !lines[0].StartsWith("| Scenario", StringComparison.Ordinal))
        {
            lines = new List<string> { Header, Divider };
        }

        var prefix = "| " + scenario.PadRight(26) + " |";
        var replaced = false;
        for (var i = 2; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                lines[i] = row;
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add(row);

        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    private static void AppendHistoryCsv(string path, FootprintResult r, string? notes)
    {
        var newFile = !File.Exists(path);
        var sb = new StringBuilder();
        if (newFile)
        {
            sb.AppendLine("timestamp,scenario,modules,rid,binary_mb,idle_rss_mb,cold_start_median_ms,cold_start_min_ms,image_mb,notes");
        }
        var inv = CultureInfo.InvariantCulture;
        sb.Append(DateTime.UtcNow.ToString("o", inv)).Append(',')
          .Append(Csv(r.Scenario)).Append(',')
          .Append(Csv(r.ModulesKey)).Append(',')
          .Append(Csv(r.Rid)).Append(',')
          .Append(r.BinarySizeMb.ToString("0.000", inv)).Append(',')
          .Append(r.IdleRssMb.ToString("0.000", inv)).Append(',')
          .Append(r.ColdStartMedianMs.ToString("0.0", inv)).Append(',')
          .Append(r.ColdStartMinMs.ToString("0.0", inv)).Append(',')
          .Append(r.ImageMeasured ? r.ImageSizeMb.ToString("0.000", inv) : string.Empty).Append(',')
          .Append(Csv(notes ?? string.Empty))
          .Append('\n');
        File.AppendAllText(path, sb.ToString());
    }

    private static string Csv(string v)
        => v.Contains(',') || v.Contains('"')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;

    private static string ReportPath(string fileName)
    {
        var overrideDir = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_DIR");
        if (!string.IsNullOrEmpty(overrideDir)) return Path.Combine(overrideDir, fileName);
        return Path.Combine(RepoRoot.Find(), "docs", "perf", fileName);
    }
}
