using Xunit;

namespace Aws2Azure.PerfTests;

/// <summary>
/// Pins the throttle-accounting semantics in <see cref="PerfResult"/> (issue
/// #456): throttling is excluded from the failure budget, so a window whose only
/// non-successes are backend 429s stays healthy, while genuine failures over the
/// budget still fail.
/// </summary>
public class PerfResultThrottleTests
{
    private static PerfResult Make(long completed, long failures, long throttled) =>
        new(
            Scenario: "test.scenario",
            Concurrency: 32,
            ElapsedSeconds: 20,
            Completed: completed,
            Failures: failures,
            ThroughputPerSec: completed / 20.0,
            P50Us: 1000,
            P95Us: 2000,
            P99Us: 3000,
            MaxUs: 4000,
            Throttled: throttled);

    [Fact]
    public void FailureRate_excludes_throttling()
    {
        // The observed #456 case: 232 completed, 5949 throttled, 0 genuine failures.
        var result = Make(completed: 232, failures: 0, throttled: 5949);

        Assert.Equal(0.0, result.FailureRate, 6);
        // Throttle is visible but separate.
        Assert.True(result.ThrottleRate > 0.96);
    }

    [Fact]
    public void Throttle_only_window_with_completions_is_healthy()
    {
        var result = Make(completed: 232, failures: 0, throttled: 5949);

        // No env needed: Completed > 0 and FailureRate is 0.
        result.AssertHealthy();
    }

    [Fact]
    public void Genuine_failures_over_budget_still_fail_despite_throttling()
    {
        // 50 completed, 50 genuine failures (50% > 10% budget), plus throttling.
        var result = Make(completed: 50, failures: 50, throttled: 1000);

        Assert.Throws<Xunit.Sdk.XunitException>(() => result.AssertHealthy());
    }

    [Fact]
    public void ThrottleRate_is_zero_when_nothing_throttled()
    {
        var result = Make(completed: 100, failures: 0, throttled: 0);

        Assert.Equal(0.0, result.ThrottleRate, 6);
        result.AssertHealthy();
    }
}
