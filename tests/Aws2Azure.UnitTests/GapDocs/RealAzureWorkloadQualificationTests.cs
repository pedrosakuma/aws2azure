using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RealAzureWorkloadQualificationTests
{
    private static readonly DateTimeOffset EvidenceTime =
        DateTimeOffset.Parse("2026-07-16T18:00:00Z");
    private static readonly DateTimeOffset GeneratedTime =
        DateTimeOffset.Parse("2026-07-16T18:01:00Z");

    [Fact]
    public void Generate_marks_conformance_pass_as_candidate_without_capacity_claim()
    {
        var document = Generate(Evidence("passed", eligible: true));

        Assert.Equal("candidate", document.Verdict);
        Assert.Empty(document.Signals);
        var scenario = Assert.Single(document.Scenarios);
        Assert.Equal("real_azure", scenario.EvidenceSource);
        Assert.Equal(1, scenario.Completions);
        Assert.Equal("load_evidence_missing", Assert.Single(document.Findings).Code);
        Assert.Empty(SloQualificationValidator.Validate(document, GeneratedTime));
    }

    [Fact]
    public void Generate_marks_failed_conformance_as_blocked()
    {
        var document = Generate(
            Evidence("failed", eligible: true, blockingOutcomes: []));

        Assert.Equal("blocked", document.Verdict);
        Assert.Equal(1, Assert.Single(document.Scenarios).Failures);
        Assert.Equal("conformance_operation_failed", Assert.Single(document.Findings).Code);
        Assert.Empty(SloQualificationValidator.Validate(document, GeneratedTime));
    }

    [Fact]
    public void Generate_marks_skipped_or_missing_operations_inconclusive()
    {
        var evidence = Evidence(
            "skipped",
            eligible: false,
            blockingOutcomes: ["object-lifecycle:skipped", "no_positive_real_azure_evidence"]);
        var document = RealAzureWorkloadQualificationGenerator.Generate(
            evidence,
            [
                new RealAzureWorkloadOperation { Service = "s3", Operation = "PutObject" },
                new RealAzureWorkloadOperation { Service = "s3", Operation = "GetObject" }
            ],
            Metadata());

        Assert.Equal("inconclusive", document.Verdict);
        Assert.Equal(1, Assert.Single(document.Scenarios).Skipped);
        Assert.Contains(
            document.Findings,
            finding => finding.Code == "conformance_operation_inconclusive");
        Assert.Contains(
            document.Findings,
            finding => finding.Code == "conformance_operation_missing");
        Assert.Empty(SloQualificationValidator.Validate(document, GeneratedTime));
    }

    [Fact]
    public void Generate_marks_selection_with_no_scenario_evidence_inconclusive()
    {
        var document = RealAzureWorkloadQualificationGenerator.Generate(
            Evidence("passed", eligible: true),
            [new RealAzureWorkloadOperation { Service = "s3", Operation = "GetObject" }],
            Metadata());

        Assert.Equal("inconclusive", document.Verdict);
        Assert.Empty(document.Scenarios);
        Assert.Equal("conformance_operation_missing", Assert.Single(document.Findings).Code);
        Assert.Empty(SloQualificationValidator.Validate(document, GeneratedTime));
    }

    [Fact]
    public void Generate_separates_deterministic_checks_from_real_azure_counts()
    {
        var evidence = Evidence("passed", eligible: true);
        var service = Assert.Single(evidence.Services);
        service.Scenarios.Add(new ScenarioEvidence
        {
            Id = "write-throttled",
            EvidenceSource = "deterministic",
            EstablishesVerification = false,
            Outcome = "passed",
            Operations = ["PutObject"],
            DurationMilliseconds = 100
        });
        service.Operations[0].Scenarios.Add("write-throttled");

        var document = Generate(evidence);

        Assert.Equal("candidate", document.Verdict);
        Assert.Equal(2, document.Scenarios.Count);
        Assert.Equal(
            1,
            Assert.Single(document.Scenarios, scenario => scenario.EvidenceSource == "real_azure")
                .Completions);
        Assert.Equal(
            1,
            Assert.Single(document.Scenarios, scenario => scenario.EvidenceSource == "deterministic")
                .Completions);
    }

    [Fact]
    public void Generate_marks_scenario_filtered_evidence_inconclusive()
    {
        var evidence = Evidence("passed", eligible: true);
        evidence.Selection.Scenario = "object-lifecycle";

        var document = Generate(evidence);

        Assert.Equal("inconclusive", document.Verdict);
        Assert.Contains(
            document.Findings,
            finding => finding.Code == "scenario_filtered_evidence");
    }

    [Fact]
    public void Generate_rejects_null_nested_operation_lists()
    {
        var evidence = Evidence("passed", eligible: true);
        Assert.Single(evidence.Services).Operations[0].Scenarios = null!;

        var exception = Assert.Throws<InvalidDataException>(() => Generate(evidence));

        Assert.Contains("operation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_rejects_operation_summary_that_omits_required_failed_scenario()
    {
        var evidence = Evidence("passed", eligible: true);
        var service = Assert.Single(evidence.Services);
        service.Scenarios.Add(new ScenarioEvidence
        {
            Id = "failed-guard",
            EvidenceSource = "deterministic",
            Outcome = "failed",
            Operations = ["PutObject"]
        });

        var exception = Assert.Throws<InvalidDataException>(() => Generate(evidence));

        Assert.Contains("does not match scenario coverage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderYaml_round_trips_candidate_through_validator()
    {
        var output = Path.Combine(
            AppContext.BaseDirectory,
            $"real-azure-workload-qualification-{Guid.NewGuid():N}.yaml");
        try
        {
            SloQualificationRenderer.RenderYaml(
                Generate(Evidence("passed", eligible: true)),
                output);
            var loaded = SloQualificationLoader.Load(output);

            Assert.Equal("candidate", loaded.Verdict);
            Assert.Empty(SloQualificationValidator.Validate(loaded, GeneratedTime));
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static SloQualificationDocument Generate(ConformanceEvidence evidence) =>
        RealAzureWorkloadQualificationGenerator.Generate(
            evidence,
            [new RealAzureWorkloadOperation { Service = "s3", Operation = "PutObject" }],
            Metadata());

    private static RealAzureWorkloadQualificationMetadata Metadata() => new()
    {
        ProfileId = "s3-basic-write",
        ProfileVersion = 1,
        GitSha = "0123456789abcdef",
        ArtifactDigest = "sha256:artifact",
        ConfigDigest = "sha256:config",
        Region = "eastus2",
        BackendDescription = "Blob Storage Standard_LRS",
        GeneratedAtUtc = GeneratedTime
    };

    private static ConformanceEvidence Evidence(
        string outcome,
        bool eligible,
        List<string>? blockingOutcomes = null) => new()
    {
        RunId = "123",
        RunUrl = "https://github.com/pedrosakuma/aws2azure/actions/runs/123",
        GeneratedAtUtc = EvidenceTime,
        HasPositiveRealAzureEvidence = eligible,
        Services =
        [
            new ServiceEvidence
            {
                Service = "s3",
                Scenarios =
                [
                    new ScenarioEvidence
                    {
                        Id = "object-lifecycle",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = true,
                        Operations = ["PutObject"],
                        Outcome = outcome,
                        DurationMilliseconds = 250,
                        Tests =
                        [
                            new TestEvidence
                            {
                                Identity = "Tests.S3.PutObject",
                                Outcome = outcome,
                                Executions = outcome == "not_run" ? 0 : 1,
                                DurationMilliseconds = 250
                            }
                        ]
                    }
                ],
                Operations =
                [
                    new OperationEvidence
                    {
                        Operation = "PutObject",
                        EligibleForVerifiedRealAzure = eligible,
                        Scenarios = ["object-lifecycle"],
                        BlockingOutcomes = blockingOutcomes ?? []
                    }
                ]
            }
        ]
    };
}
