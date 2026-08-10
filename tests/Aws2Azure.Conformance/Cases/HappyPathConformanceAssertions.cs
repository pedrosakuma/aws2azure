namespace Aws2Azure.Conformance.Cases;

/// <summary>
/// Shared seed assertions for the new happy-path matrices. Tier 1 cannot execute
/// these scenarios meaningfully yet — the current fixtures are intentionally
/// offline and stop before any real backend interaction — but we still validate
/// that each case is wired into the common abstraction, exposes successful
/// expectations, and can at least build its first signed request without touching
/// the network. Individual steps may assert an intentional non-2xx outcome (e.g.
/// a post-abort lookup real AWS correctly rejects) within an otherwise-successful
/// scenario. The live execution itself stays deferred to issue #708.
/// </summary>
internal static class HappyPathConformanceAssertions
{
    public static async Task AssertSeedCaseIsPlannedAsync(
        IConformanceCase testCase,
        ConformanceCaseContext context)
    {
        Assert.Equal(ConformanceOutcomeKind.Success, testCase.Expected.OutcomeKind);
        Assert.False(string.IsNullOrWhiteSpace(testCase.Name));
        Assert.False(string.IsNullOrWhiteSpace(testCase.Operation));

        var plan = await testCase.CreatePlanAsync(context);
        Assert.NotEmpty(plan.Steps);
        Assert.Equal(testCase.Expected.Steps.Count, plan.Steps.Count);
        // Most seeded happy-path steps are 2xx, but a case may document trailing
        // negative-verification steps within its overall success scenario (e.g. a
        // post-abort ListParts/GetObject that real AWS correctly rejects with 404)
        // — allow those explicitly documented exceptions while still catching
        // authoring typos (e.g. a stray 5xx or a transposed 2xx digit).
        Assert.All(testCase.Expected.Steps, step => Assert.True(
            step.ExpectedStatus is >= 200 and <= 299 or 404,
            $"Unexpected seeded step status {step.ExpectedStatus}; only 2xx or documented 404 negative-verification steps are allowed."));

        using var request = await plan.Steps[0].BuildRequestAsync(new ConformanceExecutionState(context));
        Assert.NotNull(request.RequestUri);
        Assert.True(
            request.Headers.Authorization is not null
            || request.Headers.Contains("Authorization"),
            "Seed conformance request must be SigV4-signed.");

        Skip.If(plan.ShouldSkip, plan.SkipReason!);
    }
}
