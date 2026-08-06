using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.Kinesis;

/// <summary>
/// Tier-1 seed coverage for the Kinesis happy-path matrix. Kinesis success paths
/// need a real stream/backend to be meaningful, so the test currently validates
/// only the shared planning shape and defers wire execution to issue #708.
/// </summary>
public sealed class KinesisHappyPathConformanceTests
{
    public static IEnumerable<object[]> CaseNames() =>
        KinesisHappyPathMatrix.Cases.Select(c => new object[] { c.Name });

    [SkippableTheory]
    [MemberData(nameof(CaseNames))]
    public async Task Happy_path_case_is_seeded_for_future_backend_diff(string caseName)
    {
        var testCase = KinesisHappyPathMatrix.Cases.Single(c => c.Name == caseName);
        await HappyPathConformanceAssertions.AssertSeedCaseIsPlannedAsync(
            testCase,
            new ConformanceCaseContext(
                KinesisConformanceFixture.AccessKeyId,
                KinesisConformanceFixture.Secret));
    }
}
