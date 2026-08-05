using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Diff;

public sealed class OfflineConformanceDiffRunnerTests
{
    [Fact]
    public async Task Matching_multistep_fixtures_pass()
    {
        var result = await OfflineConformanceDiffRunner.CompareAsync(
            "kinesis",
            ConformanceCaseCatalog.Get("kinesis", "list-shards-pagination").Case,
            FixtureGoldenStore("matching", "kinesis"),
            FixtureEvidenceStore("matching", "kinesis"),
            new ConformanceAllowList(Array.Empty<string>()));

        Assert.Equal(OfflineConformanceDiffStatus.Passed, result.Status);
        Assert.Equal(2, result.ComparedSteps.Count);
        Assert.Empty(result.UnexpectedDifferences);
    }

    [Fact]
    public async Task Unexpected_divergence_is_reported()
    {
        var result = await OfflineConformanceDiffRunner.CompareAsync(
            "s3",
            ConformanceCaseCatalog.Get("s3", "signature-does-not-match").Case,
            FixtureGoldenStore("divergent", "s3"),
            FixtureEvidenceStore("divergent", "s3"),
            new ConformanceAllowList(Array.Empty<string>()));

        Assert.Equal(OfflineConformanceDiffStatus.Failed, result.Status);
        var diff = Assert.Single(result.UnexpectedDifferences);
        Assert.Equal("signature-does-not-match", diff.StepName);
        Assert.Equal("field-value:Code", diff.Divergence.Tag);
        Assert.Contains("SignatureDoesNotMatch", result.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidAccessKeyId", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allow_listed_divergence_is_accepted()
    {
        var result = await OfflineConformanceDiffRunner.CompareAsync(
            "s3",
            ConformanceCaseCatalog.Get("s3", "signature-does-not-match").Case,
            FixtureGoldenStore("divergent", "s3"),
            FixtureEvidenceStore("divergent", "s3"),
            new ConformanceAllowList(["signature-does-not-match::field-value:Code"]));

        Assert.Equal(OfflineConformanceDiffStatus.Passed, result.Status);
        Assert.Empty(result.UnexpectedDifferences);
    }

    [Fact]
    public async Task Missing_evidence_skips_instead_of_failing()
    {
        var result = await OfflineConformanceDiffRunner.CompareAsync(
            "s3",
            ConformanceCaseCatalog.Get("s3", "request-time-too-skewed").Case,
            FixtureGoldenStore("missing-evidence", "s3"),
            FixtureEvidenceStore("missing-evidence", "s3"),
            new ConformanceAllowList(Array.Empty<string>()));

        Assert.Equal(OfflineConformanceDiffStatus.Skipped, result.Status);
        Assert.Contains("request-time-too-skewed.evidence", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partially_captured_multistep_case_skips_whole_case()
    {
        var result = await OfflineConformanceDiffRunner.CompareAsync(
            "kinesis",
            ConformanceCaseCatalog.Get("kinesis", "list-shards-pagination").Case,
            FixtureGoldenStore("partial-missing", "kinesis"),
            FixtureEvidenceStore("partial-missing", "kinesis"),
            new ConformanceAllowList(Array.Empty<string>()));

        Assert.Equal(OfflineConformanceDiffStatus.Skipped, result.Status);
        Assert.Contains("list-shards-page-2.evidence", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_golden_skips_instead_of_failing()
    {
        var result = await OfflineConformanceDiffRunner.CompareAsync(
            "s3",
            ConformanceCaseCatalog.Get("s3", "invalid-access-key-id").Case,
            FixtureGoldenStore("missing-golden", "s3"),
            FixtureEvidenceStore("missing-golden", "s3"),
            new ConformanceAllowList(Array.Empty<string>()));

        Assert.Equal(OfflineConformanceDiffStatus.Skipped, result.Status);
        Assert.Contains("invalid-access-key-id.aws.golden", result.Message, StringComparison.Ordinal);
    }

    private static GoldenStore FixtureGoldenStore(string scenario, string service) =>
        new(Path.Combine(FixtureRoot(scenario), "goldens", service));

    private static EvidenceStore FixtureEvidenceStore(string scenario, string service) =>
        new(Path.Combine(FixtureRoot(scenario), "evidence", service));

    private static string FixtureRoot(string scenario) =>
        Path.Combine(
            ConformanceProjectPaths.ProjectRoot(),
            "Diff",
            "TestFixtures",
            scenario);
}
