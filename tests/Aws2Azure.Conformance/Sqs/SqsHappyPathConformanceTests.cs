using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.Sqs;

/// <summary>
/// Tier-1 seed coverage for the SQS happy-path matrix. It keeps the suite fully
/// offline today while proving that the new success-path cases already plug into
/// the shared conformance abstraction awaiting issue #708's live differential
/// execution.
/// </summary>
public sealed class SqsHappyPathConformanceTests
{
    public static IEnumerable<object[]> CaseNames() =>
        SqsHappyPathMatrix.Cases.Select(c => new object[] { c.Name });

    [SkippableTheory]
    [MemberData(nameof(CaseNames))]
    public async Task Happy_path_case_is_seeded_for_future_backend_diff(string caseName)
    {
        var testCase = SqsHappyPathMatrix.Cases.Single(c => c.Name == caseName);
        await HappyPathConformanceAssertions.AssertSeedCaseIsPlannedAsync(
            testCase,
            new ConformanceCaseContext(
                SqsConformanceFixture.AccessKeyId,
                SqsConformanceFixture.Secret));
    }
}
