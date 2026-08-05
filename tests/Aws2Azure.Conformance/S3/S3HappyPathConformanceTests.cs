using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.S3;

/// <summary>
/// Tier-1 seed coverage for the S3 happy-path matrix. The fixture-less assertions
/// deliberately stop after request planning because the offline conformance
/// harness has no Blob/Azurite oracle today; issue #708 will wire these cases
/// into a live differential tier.
/// </summary>
public sealed class S3HappyPathConformanceTests
{
    public static IEnumerable<object[]> CaseNames() =>
        S3HappyPathMatrix.Cases.Select(c => new object[] { c.Name });

    [SkippableTheory]
    [MemberData(nameof(CaseNames))]
    public async Task Happy_path_case_is_seeded_for_future_backend_diff(string caseName)
    {
        var testCase = S3HappyPathMatrix.Cases.Single(c => c.Name == caseName);
        await HappyPathConformanceAssertions.AssertSeedCaseIsPlannedAsync(
            testCase,
            new ConformanceCaseContext(
                ConformanceProxyFixture.AccessKeyId,
                ConformanceProxyFixture.Secret));
    }
}
