using Xunit;

namespace Aws2Azure.FootprintTests;

/// <summary>
/// Footprint budget tests (#271). Publishes the proxy as a Native-AOT binary,
/// measures the sidecar-relevant footprint (binary size, idle RSS, cold start,
/// optional image size), records the numbers to <c>docs/perf/</c>, and fails the
/// run if any metric exceeds its committed ceiling in
/// <c>docs/perf/footprint-reference.json</c>.
///
/// <para>Gated by <c>AWS2AZURE_FOOTPRINT=1</c> (the publish is multi-minute), so
/// a default <c>dotnet test</c> skips it. Numbers are runner-bound — the RID is
/// recorded alongside every row.</para>
/// </summary>
public sealed class FootprintTests
{
    /// <summary>
    /// Canonical list of every <c>scenario</c> string passed to
    /// <see cref="FootprintMeasurement.MeasureAsync"/>. Mirrors
    /// <c>KnownPerfScenariosTests.All</c>: a new footprint scenario MUST be
    /// appended here AND given a reference entry so an absent ceiling surfaces as
    /// a failing drift test rather than silently skipping the gate.
    /// </summary>
    public static readonly IReadOnlyList<string> AllScenarios = new[]
    {
        "aws2azure (all modules)",
    };

    public const string AllModulesScenario = "aws2azure (all modules)";

    [SkippableFact]
    public async Task All_modules_binary_is_within_footprint_budget()
    {
        Skip.IfNot(FootprintGate.Enabled, "AWS2AZURE_FOOTPRINT=1 not set.");

        var binary = ProxyPublisher.Publish(modules: null);
        var result = await FootprintMeasurement.MeasureAsync(
            AllModulesScenario, binary, FootprintConfig.AllModulesJson);

        FootprintReport.Append(result, notes: "all 6 modules");

        // Sanity: the harness actually measured something.
        Assert.True(result.BinarySizeBytes > 0, "binary size not measured");
        Assert.True(result.IdleRssBytes > 0, "idle RSS not measured");
        Assert.True(result.ColdStartMedianMs > 0, "cold start not measured");

        result.AssertWithinBudget();
    }
}
