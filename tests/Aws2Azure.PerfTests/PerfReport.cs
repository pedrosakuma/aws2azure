using System.Globalization;
using System.Text;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Persists <see cref="PerfResult"/> rows to two files under <c>docs/perf/</c>:
/// <list type="bullet">
///   <item><c>baseline-latest.md</c> — single-row-per-scenario human-readable snapshot.
///   Existing rows for the same scenario are replaced in place; rows for other scenarios
///   are preserved. The file is created with a header on first write to a missing or
///   header-less file. This makes incremental runs (e.g. only one scenario refreshed
///   manually) merge cleanly instead of wiping the prior baseline.</item>
///   <item><c>history.csv</c> — cumulative append-only history. Never overwritten.
///   One row per <see cref="Append"/> call so trend analysis is possible across runs.</item>
/// </list>
/// </summary>
internal static class PerfReport
{
    private static readonly Lock _lock = new();

    public static void Append(PerfResult result, string? notes = null)
    {
        var mdPath = GetReportPath();
        var csvPath = GetHistoryCsvPath();
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);

        var inv = CultureInfo.InvariantCulture;
        var row = string.Format(inv,
            "| {0,-32} | {1,3} | {2,6:0.0} | {3,8} | {4,7} | {5,12:0.0} | {6,9:0.0} | {7,9:0.0} | {8,9:0.0} | {9,9:0.0} | {10} |",
            result.Scenario,
            result.Concurrency,
            result.ElapsedSeconds,
            result.Completed,
            result.Failures,
            result.ThroughputPerSec,
            result.P50Us / 1000.0,
            result.P95Us / 1000.0,
            result.P99Us / 1000.0,
            result.MaxUs / 1000.0,
            notes ?? string.Empty);

        lock (_lock)
        {
            WriteMarkdownMerged(mdPath, result.Scenario, row);
            AppendHistoryCsv(csvPath, result, notes);
            WriteJsonMerged(result, notes);
        }
    }

    private static void WriteJsonMerged(PerfResult result, string? notes)
    {
        var path = GetResultsJsonPath();
        var doc = PerfResultsFile.LoadOrEmpty(path);
        doc.Scenarios[result.Scenario] = new PerfResultRow
        {
            Concurrency = result.Concurrency,
            ElapsedSeconds = result.ElapsedSeconds,
            Completed = result.Completed,
            Failures = result.Failures,
            ThroughputPerSec = result.ThroughputPerSec,
            P50Ms = result.P50Us / 1000.0,
            P95Ms = result.P95Us / 1000.0,
            P99Ms = result.P99Us / 1000.0,
            MaxMs = result.MaxUs / 1000.0,
            CapturedAtUtc = DateTime.UtcNow,
            Notes = notes,
        };
        PerfResultsFile.Save(path, doc);
    }

    private static void WriteMarkdownMerged(string path, string scenario, string newRow)
    {
        var existingLines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var sb = new StringBuilder();

        if (existingLines.Length == 0 || !HasHeader(existingLines))
        {
            sb.Append(BuildHeader());
            sb.AppendLine(newRow);
            File.WriteAllText(path, sb.ToString());
            return;
        }

        // Locate the row separator (`|----` line) and split into header/body.
        var separatorIndex = -1;
        for (var i = 0; i < existingLines.Length; i++)
        {
            if (existingLines[i].StartsWith("|---", StringComparison.Ordinal))
            {
                separatorIndex = i;
                break;
            }
        }
        if (separatorIndex < 0)
        {
            sb.Append(BuildHeader());
            sb.AppendLine(newRow);
            File.WriteAllText(path, sb.ToString());
            return;
        }

        // Refresh the "Generated:" timestamp so the snapshot reflects the latest merge.
        for (var i = 0; i < separatorIndex; i++)
        {
            var line = existingLines[i];
            if (line.StartsWith("Generated:", StringComparison.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.UtcNow:O}");
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        sb.AppendLine(existingLines[separatorIndex]);

        var replaced = false;
        var scenarioCell = $"| {scenario,-32} |";
        for (var i = separatorIndex + 1; i < existingLines.Length; i++)
        {
            var line = existingLines[i];
            if (!line.StartsWith('|'))
            {
                sb.AppendLine(line);
                continue;
            }
            if (!replaced && line.StartsWith(scenarioCell, StringComparison.Ordinal))
            {
                sb.AppendLine(newRow);
                replaced = true;
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        if (!replaced)
        {
            sb.AppendLine(newRow);
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static bool HasHeader(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("|---", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void AppendHistoryCsv(string path, PerfResult result, string? notes)
    {
        var inv = CultureInfo.InvariantCulture;
        var isNew = !File.Exists(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (isNew)
        {
            writer.WriteLine("timestamp_utc,scenario,concurrency,elapsed_seconds,completed,failures,throughput_per_sec,p50_ms,p95_ms,p99_ms,max_ms,notes");
        }
        writer.WriteLine(string.Format(inv,
            "{0},{1},{2},{3:0.0},{4},{5},{6:0.0},{7:0.0},{8:0.0},{9:0.0},{10:0.0},{11}",
            DateTime.UtcNow.ToString("O", inv),
            EscapeCsv(result.Scenario),
            result.Concurrency,
            result.ElapsedSeconds,
            result.Completed,
            result.Failures,
            result.ThroughputPerSec,
            result.P50Us / 1000.0,
            result.P95Us / 1000.0,
            result.P99Us / 1000.0,
            result.MaxUs / 1000.0,
            EscapeCsv(notes ?? string.Empty)));
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string GetReportPath() => Path.Combine(GetPerfDir(), "baseline-latest.md");
    public static string GetHistoryCsvPath() => Path.Combine(GetPerfDir(), "history.csv");
    public static string GetResultsJsonPath() => Path.Combine(GetPerfDir(), "baseline-latest.json");

    private static string GetPerfDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
        {
            Directory.CreateDirectory(overrideDir);
            return overrideDir;
        }
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine(dir, "docs", "perf");
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppContext.BaseDirectory, "perf");
    }

    private static string BuildHeader()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# aws2azure — perf baseline");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Closed-loop concurrent driver — AWS SDK clients pointing at the proxy");
        sb.AppendLine("(`Aws2Azure.Proxy`) which fronts local emulators (Azurite, Service Bus,");
        sb.AppendLine("Cosmos DB, Event Hubs). **Numbers are emulator-bound — they reflect proxy");
        sb.AppendLine("overhead, not real-Azure throughput.**");
        sb.AppendLine();
        sb.AppendLine("Rows are merged in place by scenario name (see `PerfReport.cs`); partial");
        sb.AppendLine("reruns refresh the matching row without wiping the rest. The cumulative");
        sb.AppendLine("append-only history lives at `history.csv` alongside this file.");
        sb.AppendLine();
        sb.AppendLine("| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |");
        sb.AppendLine("|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|");
        return sb.ToString();
    }
}
