using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.SecretsManager;

/// <summary>
/// Tier-1 seed coverage for the Secrets Manager happy-path matrix. It validates
/// the common case abstraction and signed-request planning, then skips live
/// execution until the backend-backed issue #708 harness is in place.
/// </summary>
public sealed class SecretsManagerHappyPathConformanceTests
{
    public static IEnumerable<object[]> CaseNames() =>
        SecretsManagerHappyPathMatrix.Cases.Select(c => new object[] { c.Name });

    [SkippableTheory]
    [MemberData(nameof(CaseNames))]
    public async Task Happy_path_case_is_seeded_for_future_backend_diff(string caseName)
    {
        var testCase = SecretsManagerHappyPathMatrix.Cases.Single(c => c.Name == caseName);
        await HappyPathConformanceAssertions.AssertSeedCaseIsPlannedAsync(
            testCase,
            new ConformanceCaseContext(
                SecretsManagerConformanceFixture.AccessKeyId,
                SecretsManagerConformanceFixture.Secret));
    }
}
