using System.Globalization;
using System.Text;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Appends a single PerfResult to <c>docs/perf/baseline-latest.md</c>.
/// The file is recreated with a header on the first append per process
/// run (controlled by a static flag) so the table stays a single coherent
/// snapshot for the suite invocation that produced it.
/// </summary>
internal static class PerfReport
{
    private static readonly Lock _lock = new();
    private static bool _headerWritten;

    public static void Append(PerfResult result, string? notes = null)
    {
        var path = GetReportPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (_lock)
        {
            if (!_headerWritten)
            {
                File.WriteAllText(path, BuildHeader());
                _headerWritten = true;
            }

            var inv = CultureInfo.InvariantCulture;
            var row = string.Format(inv,
                "| {0,-32} | {1,3} | {2,6:0.0} | {3,8} | {4,7} | {5,12:0.0} | {6,9:0.0} | {7,9:0.0} | {8,9:0.0} | {9,9:0.0} | {10} |\n",
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
            File.AppendAllText(path, row);
        }
    }

    public static string GetReportPath()
    {
        // Walk up from binary output until we find the repo root (where docs/perf lives).
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) &&
                Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine(dir, "docs", "perf", "baseline-latest.md");
            }
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback — alongside the binary if repo root not detected.
        return Path.Combine(AppContext.BaseDirectory, "baseline-latest.md");
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
        sb.AppendLine("| Scenario                         | Cnc | Elap s |  Cmpltd | Failed | Throughput/s |  p50 ms  |  p95 ms  |  p99 ms  |  max ms  | Notes |");
        sb.AppendLine("|----------------------------------|----:|-------:|--------:|-------:|-------------:|---------:|---------:|---------:|---------:|-------|");
        return sb.ToString();
    }
}
