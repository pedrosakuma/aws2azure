using System.Globalization;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// One footprint measurement of a published proxy binary. All sizes are bytes;
/// the MB / ms accessors are what the budget gate and report consume.
/// </summary>
internal sealed record FootprintResult(
    string Scenario,
    string ModulesKey,
    string Rid,
    long BinarySizeBytes,
    double ColdStartMedianMs,
    double ColdStartMinMs,
    IReadOnlyList<double> ColdStartSamplesMs,
    long IdleRssBytes,
    long ImageSizeBytes)
{
    public double BinarySizeMb => BinarySizeBytes / (1024.0 * 1024.0);
    public double IdleRssMb => IdleRssBytes / (1024.0 * 1024.0);
    public double ImageSizeMb => ImageSizeBytes / (1024.0 * 1024.0);

    /// <summary>True when a container image size was actually captured.</summary>
    public bool ImageMeasured => ImageSizeBytes > 0;

    /// <summary>
    /// Compares each metric against the committed ceiling in
    /// <c>docs/perf/footprint-reference.json</c>. No-op when the scenario is not
    /// listed there (newly added scenarios stay non-gated until an operator
    /// records a ceiling — mirrors the perf baseline convention). Each ceiling is
    /// independently opt-out: a value of 0 disables that dimension, and the image
    /// ceiling additionally no-ops when no image size was measured.
    /// </summary>
    public void AssertWithinBudget()
    {
        var entry = FootprintReferenceBaseline.TryGet(Scenario);
        if (entry is null) return;

        var inv = CultureInfo.InvariantCulture;
        var problems = new List<string>();

        if (entry.MaxBinarySizeMb > 0 && BinarySizeMb > entry.MaxBinarySizeMb)
        {
            problems.Add(string.Format(inv,
                "binary {0:0.0} MB > ceiling {1:0.0} MB", BinarySizeMb, entry.MaxBinarySizeMb));
        }
        if (entry.MaxIdleRssMb > 0 && IdleRssMb > entry.MaxIdleRssMb)
        {
            problems.Add(string.Format(inv,
                "idle RSS {0:0.0} MB > ceiling {1:0.0} MB", IdleRssMb, entry.MaxIdleRssMb));
        }
        if (entry.MaxColdStartMs > 0 && ColdStartMedianMs > entry.MaxColdStartMs)
        {
            problems.Add(string.Format(inv,
                "cold start {0:0} ms > ceiling {1:0} ms", ColdStartMedianMs, entry.MaxColdStartMs));
        }
        if (ImageMeasured && entry.MaxImageSizeMb > 0 && ImageSizeMb > entry.MaxImageSizeMb)
        {
            problems.Add(string.Format(inv,
                "image {0:0.0} MB > ceiling {1:0.0} MB", ImageSizeMb, entry.MaxImageSizeMb));
        }

        if (problems.Count > 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"{Scenario}: footprint regression — " + string.Join("; ", problems));
        }
    }
}
