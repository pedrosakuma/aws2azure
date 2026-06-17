namespace Aws2Azure.PerfTests;

/// <summary>
/// Scales each scenario's base concurrency by
/// <c>AWS2AZURE_PERF_CONCURRENCY_SCALE</c> (positive integer, default 1).
///
/// <para>The closed-loop driver obeys Little's law — throughput =
/// concurrency / latency — so at a fixed worker count the <em>harness</em>, not
/// the backend, caps throughput. The committed emulator baseline runs unscaled
/// (multiplier 1) so its thresholds stay comparable run-to-run; real-Azure
/// Tier 2 runs (#420) crank the multiplier to push the proxy toward a CPU-bound
/// regime where a CPU/alloc optimization can actually translate to throughput.</para>
/// </summary>
internal static class PerfConcurrency
{
    private static readonly int Multiplier = ReadMultiplier();

    /// <summary>Scaled worker count, never below 1.</summary>
    public static int Scale(int baseConcurrency) =>
        Math.Max(1, baseConcurrency * Multiplier);

    private static int ReadMultiplier()
    {
        var raw = Environment.GetEnvironmentVariable("AWS2AZURE_PERF_CONCURRENCY_SCALE");
        return int.TryParse(raw, out var value) && value > 0 ? value : 1;
    }
}
