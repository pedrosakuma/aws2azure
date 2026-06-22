using System.Globalization;
using System.Text;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Persists <see cref="PerfResult"/> rows to report files. Local runs default to
/// <c>TestResults/perf/</c>; CI opts into the tracked <c>docs/perf/</c> baseline.
/// <list type="bullet">
///   <item><c>baseline-latest.md</c> — single-row-per-scenario human-readable snapshot.
///   Existing rows for the same scenario are replaced in place; rows for other scenarios
///   are preserved. The file is created with a header on first write to a missing or
///   header-less file. This makes incremental runs (e.g. only one scenario refreshed
///   manually) merge cleanly instead of wiping the prior baseline.</item>
///   <item><c>history.csv</c> — cumulative append-only history. Never overwritten.
///   One row per <see cref="Append"/> call so trend analysis is possible across runs.</item>
///   <item><c>baseline-latest.json</c> — machine-readable merged rows consumed by
///   the relative regression gate.</item>
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
        // Surface backend throttling (#456) inline in the notes — it is excluded
        // from the failure count/budget (expected backpressure, not a defect), so
        // without this it would be invisible in the diffed A/B artifacts. The
        // report schema (columns the relative gate parses) is intentionally left
        // unchanged.
        if (result.Throttled > 0)
        {
            var throttleTag = string.Format(inv,
                "[throttled {0:P1}: {1} of {2} attempts]",
                result.ThrottleRate, result.Throttled,
                result.Completed + result.Throttled + result.Failures);
            notes = string.IsNullOrEmpty(notes) ? throttleTag : $"{throttleTag} {notes}";
        }

        var memCells = result.MemoryMeasured
            ? string.Format(inv, " {0,8:0.0} | {1,9:0.0} | {2,11:0} | {3,5} |",
                result.PeakWorkingSetMb, result.PeakGcHeapBytes / (1024.0 * 1024.0),
                result.AllocBytesPerOp, result.Gen2Collections)
            : "      --- |       --- |         --- |   --- |";
        var row = string.Format(inv,
            "| {0,-32} | {1,3} | {2,6:0.0} | {3,8} | {4,7} | {5,12:0.0} | {6,9:0.0} | {7,9:0.0} | {8,9:0.0} | {9,9:0.0} |{10} {11} |",
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
            memCells,
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
            MemoryMeasured = result.MemoryMeasured,
            PeakWorkingSetMb = result.MemoryMeasured ? result.PeakWorkingSetMb : 0,
            PeakGcHeapMb = result.MemoryMeasured ? result.PeakGcHeapBytes / (1024.0 * 1024.0) : 0,
            AllocBytesPerOp = result.MemoryMeasured ? result.AllocBytesPerOp : 0,
            Gen2Collections = result.MemoryMeasured ? result.Gen2Collections : 0,
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
            writer.WriteLine("timestamp_utc,scenario,concurrency,elapsed_seconds,completed,failures,throughput_per_sec,p50_ms,p95_ms,p99_ms,max_ms,peak_working_set_mb,peak_gc_heap_mb,alloc_bytes_per_op,gen2_collections,notes");
        }
        writer.WriteLine(string.Format(inv,
            "{0},{1},{2},{3:0.0},{4},{5},{6:0.0},{7:0.0},{8:0.0},{9:0.0},{10:0.0},{11},{12},{13},{14},{15}",
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
            result.MemoryMeasured ? result.PeakWorkingSetMb.ToString("0.0", inv) : string.Empty,
            result.MemoryMeasured ? (result.PeakGcHeapBytes / (1024.0 * 1024.0)).ToString("0.0", inv) : string.Empty,
            result.MemoryMeasured ? result.AllocBytesPerOp.ToString("0", inv) : string.Empty,
            result.MemoryMeasured ? result.Gen2Collections.ToString(inv) : string.Empty,
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
        if (TryFindRepoRoot(out var repoRoot))
        {
            if (ShouldUpdateDocsPerf())
            {
                return Path.Combine(repoRoot, "docs", "perf");
            }

            return Path.Combine(repoRoot, "TestResults", "perf");
        }

        return Path.Combine(AppContext.BaseDirectory, "perf");
    }

    private static bool TryFindRepoRoot(out string repoRoot)
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                repoRoot = dir;
                return true;
            }

            dir = Path.GetDirectoryName(dir);
        }

        repoRoot = string.Empty;
        return false;
    }

    private static bool ShouldUpdateDocsPerf()
    {
        var value = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_UPDATE_DOCS");
        return string.Equals(value, "1", StringComparison.Ordinal) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHeader()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# aws2azure — perf baseline");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Closed-loop concurrent driver — AWS SDK clients pointing at the proxy");
        sb.AppendLine("(`Aws2Azure.Proxy`). The backend is indicated per row in the **Notes**");
        sb.AppendLine("column (`[emulator]`, `[real-Azure (text)]`, `[real-Azure (binary)]`); SDK");
        sb.AppendLine("baseline rows run directly against the same backend with no proxy. Emulator");
        sb.AppendLine("numbers reflect proxy overhead, not real-Azure throughput; real-Azure runs");
        sb.AppendLine("(issue #420 Tier 2) are the end-to-end falsification arbiter.");
        sb.AppendLine();
        sb.AppendLine("Rows are merged in place by scenario name (see `PerfReport.cs`); partial");
        sb.AppendLine("reruns refresh the matching row without wiping the rest. The cumulative");
        sb.AppendLine("append-only history lives at `history.csv` alongside this file.");
        sb.AppendLine();
        sb.AppendLine("| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  |  RSS MB  | GCheap MB |   B/op    |  g2  | Notes |");
        sb.AppendLine("|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|---------:|----------:|----------:|-----:|-------|");
        return sb.ToString();
    }
}
