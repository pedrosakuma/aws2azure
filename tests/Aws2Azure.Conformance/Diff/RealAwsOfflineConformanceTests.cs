using Aws2Azure.Conformance.AllowList;
using Aws2Azure.Conformance.Cases;
using Aws2Azure.Conformance.Goldens;

namespace Aws2Azure.Conformance.Diff;

/// <summary>
/// Tier-3 credential-free conformance differential. It reuses the shared case
/// catalog and only compares already-recorded files on disk, so it can run with
/// zero cloud credentials and zero live network traffic on every PR once the
/// capture/export workflows start producing artifacts. Until then, each case
/// skips with a path-specific message instead of failing.
/// </summary>
public sealed class RealAwsOfflineConformanceTests
{
    public static IEnumerable<object[]> CaseNames() =>
        ConformanceCaseCatalog.All.Select(entry => new object[] { entry.Service, entry.Case.Name });

    [SkippableTheory]
    [MemberData(nameof(CaseNames))]
    public async Task Real_azure_evidence_matches_real_aws_golden(
        string service,
        string caseName)
    {
        var entry = ConformanceCaseCatalog.Get(service, caseName);
        var result = await OfflineConformanceDiffRunner.CompareAsync(
            service,
            entry.Case,
            GoldenStore.ForService(service),
            EvidenceStore.ForService(service),
            ConformanceAllowList.FromGapDocs(service));

        Skip.If(result.Status == OfflineConformanceDiffStatus.Skipped, result.Message);
        Assert.True(result.Status == OfflineConformanceDiffStatus.Passed, result.Message);
    }
}
