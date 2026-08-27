using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class AdopterRealAzureConformanceReportTests
{
    [Fact]
    public void Generate_marks_all_eligible_operations_passed()
    {
        var report = AdopterRealAzureConformanceReportGenerator.Generate(
            Matrix(),
            [new("Tests.S3.Pass", ConformanceOutcome.Passed, TimeSpan.FromSeconds(1), "run.trx")],
            new AdopterRealAzureConformanceReportMetadata
            {
                CandidateId = "team-a",
                GitSha = "abc123",
                RunId = "local-1",
                GeneratedAtUtc = DateTimeOffset.Parse("2026-08-27T18:00:00Z"),
                MatrixPath = "docs/testing/real-azure-conformance.yaml",
                Region = "eastus2",
                ResourceGroup = "rg-adopter"
            });

        Assert.Equal("passed", report.Verdict);
        Assert.True(report.HasPositiveRealAzureEvidence);
        Assert.Equal("team-a", report.Candidate.Id);
        Assert.Equal("abc123", report.Candidate.GitSha);
        Assert.Equal("eastus2", report.Provenance.Region);
        Assert.Equal("rg-adopter", report.Provenance.ResourceGroup);

        var operation = Assert.Single(Assert.Single(report.Services).Operations);
        Assert.Equal("PutObject", operation.Operation);
        Assert.Equal("passed", operation.Verdict);
        Assert.True(operation.EligibleForVerifiedRealAzure);
    }

    [Fact]
    public void Generate_marks_failed_operations_failed_and_missing_evidence_inconclusive()
    {
        var report = AdopterRealAzureConformanceReportGenerator.Generate(
            Matrix(includeDelete: true, includeListGuard: true),
            [
                new("Tests.S3.Pass", ConformanceOutcome.Passed, TimeSpan.FromSeconds(1), "run.trx"),
                new("Tests.S3.Fail", ConformanceOutcome.Failed, TimeSpan.FromSeconds(1), "run.trx")
            ],
            new AdopterRealAzureConformanceReportMetadata
            {
                CandidateId = "team-a",
                RunId = "local-2",
                GeneratedAtUtc = DateTimeOffset.Parse("2026-08-27T18:00:00Z"),
                MatrixPath = "docs/testing/real-azure-conformance.yaml"
            });

        Assert.Equal("failed", report.Verdict);
        var service = Assert.Single(report.Services);
        var passed = Assert.Single(service.Operations, operation => operation.Operation == "PutObject");
        var failed = Assert.Single(service.Operations, operation => operation.Operation == "DeleteObject");
        var inconclusive = Assert.Single(service.Operations, operation => operation.Operation == "ListBuckets");

        Assert.Equal("passed", passed.Verdict);
        Assert.Equal("failed", failed.Verdict);
        Assert.Equal(["delete:failed", "no_positive_real_azure_evidence"], failed.BlockingOutcomes);
        Assert.Equal("inconclusive", inconclusive.Verdict);
        Assert.Equal(["list-guard:not_run", "no_positive_real_azure_evidence"], inconclusive.BlockingOutcomes);
    }

    [Fact]
    public void RenderYaml_omits_optional_null_fields_and_writes_expected_shape()
    {
        var report = AdopterRealAzureConformanceReportGenerator.Generate(
            Matrix(includeListGuard: true),
            [new("Tests.S3.Pass", ConformanceOutcome.Skipped, TimeSpan.Zero, "run.trx")],
            new AdopterRealAzureConformanceReportMetadata
            {
                CandidateId = "team-a",
                RunId = "local-3",
                GeneratedAtUtc = DateTimeOffset.Parse("2026-08-27T18:00:00Z"),
                MatrixPath = "docs/testing/real-azure-conformance.yaml"
            });
        var output = Path.Combine(AppContext.BaseDirectory, $"adopter-report-{Guid.NewGuid():N}.yaml");

        try
        {
            AdopterRealAzureConformanceReportRenderer.RenderYaml(report, output);

            var yaml = File.ReadAllText(output);
            Assert.Contains("artifact_kind: adopter_real_azure_conformance_report", yaml, StringComparison.Ordinal);
            Assert.Contains("id: team-a", yaml, StringComparison.Ordinal);
            Assert.Contains("verdict: inconclusive", yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("run_url:", yaml, StringComparison.Ordinal);
            Assert.Contains("blocking_outcomes:", yaml, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static RealAzureConformanceMatrix Matrix(bool includeDelete = false, bool includeListGuard = false)
    {
        var scenarios = new List<RealAzureScenario>
        {
            new()
            {
                Id = "put",
                Priority = "p0",
                Category = "write",
                EvidenceSource = "real_azure",
                EstablishesVerification = true,
                Description = "Put object.",
                Operations = ["PutObject"],
                Tests = ["Tests.S3.Pass"]
            }
        };

        if (includeListGuard)
        {
            scenarios.Add(new RealAzureScenario
            {
                Id = "list-guard",
                Priority = "p1",
                Category = "throttling",
                EvidenceSource = "deterministic",
                EstablishesVerification = false,
                Description = "Deterministic list guard.",
                Operations = ["ListBuckets"],
                Tests = ["Tests.S3.Deterministic"]
            });
        }

        if (includeDelete)
        {
            scenarios.Add(new RealAzureScenario
            {
                Id = "delete",
                Priority = "p0",
                Category = "core",
                EvidenceSource = "real_azure",
                EstablishesVerification = true,
                Description = "Delete object.",
                Operations = ["DeleteObject"],
                Tests = ["Tests.S3.Fail"]
            });
        }

        return new RealAzureConformanceMatrix
        {
            SchemaVersion = 1,
            Services =
            [
                new RealAzureService
                {
                    Service = "s3",
                    Scenarios = scenarios
                }
            ]
        };
    }
}
