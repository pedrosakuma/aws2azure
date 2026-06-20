using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Deterministic tests for <see cref="PerfSweep.DetectKnee"/> over synthetic
/// throughput curves — no proxy, no backend. Pins the saturation semantics that
/// the Tier 2 real-Azure A/B (issue #420) relies on.
/// </summary>
public class PerfSweepKneeTests
{
    [Fact]
    public void Saturating_curve_finds_knee_at_plateau_start()
    {
        // Throughput climbs then flattens: the proxy saturates around c=8.
        var points = new[]
        {
            (1, 100.0, 5.0),
            (2, 190.0, 6.0),
            (4, 360.0, 9.0),
            (8, 500.0, 18.0),
            (16, 520.0, 40.0),
            (32, 525.0, 95.0),
        };

        var knee = PerfSweep.DetectKnee(points);

        // max throughput = 525 @ c=32; 95% threshold = 498.75, first met at c=8.
        Assert.Equal(8, knee.KneeConcurrency);
        Assert.Equal(500.0, knee.ThroughputAtKnee, 3);
        Assert.Equal(18.0, knee.P99AtKneeMs, 3);
        Assert.Equal(525.0, knee.MaxThroughput, 3);
        Assert.Equal(32, knee.MaxThroughputConcurrency);
        // The ladder extended well past the knee, so saturation was observed.
        Assert.True(knee.ReachedSaturation);
    }

    [Fact]
    public void Still_climbing_curve_reports_no_saturation()
    {
        // Linear scaling to the last rung: the proxy never saturated, the ladder
        // was too short. The knee is the last level and saturation is NOT claimed.
        var points = new[]
        {
            (1, 100.0, 5.0),
            (2, 200.0, 5.0),
            (4, 400.0, 5.0),
            (8, 800.0, 5.0),
        };

        var knee = PerfSweep.DetectKnee(points);

        Assert.Equal(8, knee.KneeConcurrency);
        Assert.Equal(800.0, knee.MaxThroughput, 3);
        Assert.Equal(8, knee.MaxThroughputConcurrency);
        Assert.False(knee.ReachedSaturation);
    }

    [Fact]
    public void Knee_before_peak_still_counts_as_saturated()
    {
        // Throughput hits ~95% early then only creeps up 5% to the highest rung.
        // The knee is the early level; because a higher rung was tested, the
        // plateau was observed → saturated, even though the peak is at the end.
        var points = new[]
        {
            (4, 480.0, 10.0),
            (8, 500.0, 20.0),
            (16, 505.0, 45.0),
        };

        var knee = PerfSweep.DetectKnee(points);

        // max = 505 @ 16; 95% threshold = 479.75, first met at c=4.
        Assert.Equal(4, knee.KneeConcurrency);
        Assert.True(knee.ReachedSaturation);
    }

    [Fact]
    public void Single_point_is_not_saturated()
    {
        var knee = PerfSweep.DetectKnee(new[] { (16, 420.0, 12.0) });

        Assert.Equal(16, knee.KneeConcurrency);
        Assert.Equal(420.0, knee.ThroughputAtKnee, 3);
        Assert.Equal(420.0, knee.MaxThroughput, 3);
        Assert.False(knee.ReachedSaturation);
    }

    [Fact]
    public void Empty_points_yield_empty_knee()
    {
        var knee = PerfSweep.DetectKnee(Array.Empty<(int, double, double)>());

        Assert.Equal(0, knee.KneeConcurrency);
        Assert.Equal(0.0, knee.MaxThroughput, 3);
        Assert.False(knee.ReachedSaturation);
    }

    [Fact]
    public void All_zero_throughput_does_not_throw()
    {
        // A fully throttled/broken run: every level returned zero throughput.
        // Must degrade gracefully (no divide-by-zero, no false saturation).
        var points = new[]
        {
            (1, 0.0, 0.0),
            (2, 0.0, 0.0),
        };

        var knee = PerfSweep.DetectKnee(points);

        Assert.Equal(0.0, knee.MaxThroughput, 3);
        Assert.False(knee.ReachedSaturation);
    }

    [Fact]
    public void Unsorted_input_is_sorted_by_concurrency()
    {
        var points = new[]
        {
            (32, 525.0, 95.0),
            (1, 100.0, 5.0),
            (8, 500.0, 18.0),
            (4, 360.0, 9.0),
            (16, 520.0, 40.0),
            (2, 190.0, 6.0),
        };

        var knee = PerfSweep.DetectKnee(points);

        Assert.Equal(8, knee.KneeConcurrency);
        Assert.Equal(32, knee.MaxThroughputConcurrency);
        Assert.True(knee.ReachedSaturation);
    }
}
