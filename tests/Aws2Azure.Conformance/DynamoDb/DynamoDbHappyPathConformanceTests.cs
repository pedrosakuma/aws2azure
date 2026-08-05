using Aws2Azure.Conformance.Cases;

namespace Aws2Azure.Conformance.DynamoDb;

/// <summary>
/// Tier-1 seed coverage for the DynamoDB happy-path matrix. It verifies that the
/// cases are wired into the shared abstraction and can plan their first signed
/// request, then skips the live execution until issue #708 adds a backend-backed
/// differential tier.
/// </summary>
public sealed class DynamoDbHappyPathConformanceTests
{
    public static IEnumerable<object[]> CaseNames() =>
        DynamoDbHappyPathMatrix.Cases.Select(c => new object[] { c.Name });

    [SkippableTheory]
    [MemberData(nameof(CaseNames))]
    public async Task Happy_path_case_is_seeded_for_future_backend_diff(string caseName)
    {
        var testCase = DynamoDbHappyPathMatrix.Cases.Single(c => c.Name == caseName);
        await HappyPathConformanceAssertions.AssertSeedCaseIsPlannedAsync(
            testCase,
            new ConformanceCaseContext(
                DynamoDbConformanceFixture.AccessKeyId,
                DynamoDbConformanceFixture.Secret));
    }
}
