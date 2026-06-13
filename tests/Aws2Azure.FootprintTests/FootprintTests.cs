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
        "aws2azure (s3 only)",
    };

    public const string AllModulesScenario = "aws2azure (all modules)";

    /// <summary>
    /// Representative single-module tier (#273): the leanest realistic sidecar.
    /// Quantifies the build-time-module-selection delta against the all-modules
    /// binary and gates it so a tier regression (e.g. an unselected module
    /// leaking back into the trim graph) surfaces in the budget gate.
    /// </summary>
    public const string S3OnlyScenario = "aws2azure (s3 only)";

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

    [SkippableFact]
    public async Task S3_only_tier_binary_is_within_footprint_budget()
    {
        Skip.IfNot(FootprintGate.Enabled, "AWS2AZURE_FOOTPRINT=1 not set.");
        Skip.IfNot(FootprintGate.TiersEnabled, "AWS2AZURE_FOOTPRINT_TIERS=1 not set.");

        var binary = ProxyPublisher.Publish(modules: "s3");
        var result = await FootprintMeasurement.MeasureAsync(
            S3OnlyScenario, binary, FootprintConfig.AllModulesJson);

        FootprintReport.Append(result, notes: "build-time module selection: s3 only");

        Assert.True(result.BinarySizeBytes > 0, "binary size not measured");
        Assert.True(result.IdleRssBytes > 0, "idle RSS not measured");
        Assert.True(result.ColdStartMedianMs > 0, "cold start not measured");

        result.AssertWithinBudget();
    }
}
