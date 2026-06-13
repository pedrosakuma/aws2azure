using System.Diagnostics;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// Drives footprint measurement for a published binary: AOT binary size, cold
/// start (process start → first <c>/_aws2azure/health</c> 200, median of N
/// fresh starts), idle resident set (steady-state high-water after settle), and
/// — when opted in — container image size.
/// </summary>
internal static class FootprintMeasurement
{
    private const int ColdStartIterations = 7;
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleSampleWindow = TimeSpan.FromSeconds(2);

    public static async Task<FootprintResult> MeasureAsync(
        string scenario, PublishedBinary binary, string configJson)
    {
        var coldStartsMs = await MeasureColdStartAsync(binary.BinaryPath, configJson).ConfigureAwait(false);
        var idleRssBytes = await MeasureIdleRssAsync(binary.BinaryPath, configJson).ConfigureAwait(false);
        var imageSizeBytes = TryMeasureImageSize();

        return new FootprintResult(
            Scenario: scenario,
            ModulesKey: binary.ModulesKey,
            Rid: binary.Rid,
            BinarySizeBytes: binary.SizeBytes,
            ColdStartMedianMs: Median(coldStartsMs),
            ColdStartMinMs: coldStartsMs.Count == 0 ? 0 : coldStartsMs.Min(),
            ColdStartSamplesMs: coldStartsMs,
            IdleRssBytes: idleRssBytes,
            ImageSizeBytes: imageSizeBytes);
    }

    private static async Task<List<double>> MeasureColdStartAsync(string binaryPath, string configJson)
    {
        var samples = new List<double>(ColdStartIterations);
        for (var i = 0; i < ColdStartIterations; i++)
        {
            var sw = Stopwatch.StartNew();
            using var proxy = ProxyInstance.Start(binaryPath, configJson);
            await proxy.WaitForHealthyAsync(HealthTimeout).ConfigureAwait(false);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        return samples;
    }

    private static async Task<long> MeasureIdleRssAsync(string binaryPath, string configJson)
    {
        using var proxy = ProxyInstance.Start(binaryPath, configJson);
        await proxy.WaitForHealthyAsync(HealthTimeout).ConfigureAwait(false);

        // Let the runtime settle (background GC, tiered-JIT is irrelevant for AOT
        // but Kestrel/thread-pool warmup still moves RSS for a beat).
        await Task.Delay(IdleSettle).ConfigureAwait(false);

        long peak = 0;
        var deadline = DateTime.UtcNow + IdleSampleWindow;
        while (DateTime.UtcNow < deadline && !proxy.HasExited)
        {
            var rss = proxy.ReadRssBytes();
            if (rss > peak) peak = rss;
            await Task.Delay(200).ConfigureAwait(false);
        }
        return peak;
    }

    /// <summary>
    /// Container image size in bytes via <c>docker image inspect</c>, when
    /// <c>AWS2AZURE_FOOTPRINT_IMAGE</c> names a built image. Returns 0 (treated
    /// as "not measured", never gated) when the var is unset, docker is missing,
    /// or the image is absent.
    /// </summary>
    private static long TryMeasureImageSize()
    {
        var image = Environment.GetEnvironmentVariable("AWS2AZURE_FOOTPRINT_IMAGE");
        if (string.IsNullOrWhiteSpace(image)) return 0;
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("image");
            psi.ArgumentList.Add("inspect");
            psi.ArgumentList.Add(image);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("{{.Size}}");

            using var p = Process.Start(psi);
            if (p is null) return 0;

            // Drain both streams asynchronously so a chatty `docker` can't
            // deadlock on a full stderr pipe, and bound the whole thing by the
            // timeout (killing the tree if it hangs).
            var stdout = new System.Text.StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, _) => { /* drained, ignored */ };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return 0;
            }
            p.WaitForExit(); // flush async readers
            if (p.ExitCode != 0) return 0;
            return long.TryParse(stdout.ToString().Trim(), out var size) ? size : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
