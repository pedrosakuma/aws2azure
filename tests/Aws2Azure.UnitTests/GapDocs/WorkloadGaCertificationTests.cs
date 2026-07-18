using System.Text.Json;
using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class WorkloadGaCertificationTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly IReadOnlyList<OperationDoc> Operations =
        Loader.LoadAll(Path.Combine(RepoRoot, "docs", "gaps"));
    private static readonly IReadOnlyList<ServiceDesignDoc> Designs =
        Loader.LoadDesignDocs(Path.Combine(RepoRoot, "docs", "gaps"));

    [Theory]
    [InlineData("s3-basic-object-crud.yaml", "candidate")]
    [InlineData("secretsmanager-basic-lifecycle.yaml", "candidate")]
    [InlineData("sqs-standard-messaging.yaml", "conditional")]
    [InlineData("dynamodb-basic-crud.yaml", "candidate")]
    public void Repository_profiles_have_expected_mechanical_verdict(
        string fileName,
        string expectedVerdict)
    {
        var manifest = LoadManifest(fileName);

        Assert.Empty(WorkloadGaManifestValidator.Validate(manifest, Operations, Designs));
        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, 16));

        Assert.Equal(expectedVerdict, report.Verdict);
    }

    [Fact]
    public void New_unaccepted_partial_operation_blocks_profile()
    {
        var manifest = LoadManifest("dynamodb-basic-crud.yaml");
        manifest.AcceptedPartialOperations.Remove("dynamodb:PutItem");

        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, 16));

        Assert.Equal("blocked", report.Verdict);
        Assert.Contains(
            report.Findings,
            finding => finding.Code == "partial_operation_not_accepted"
                       && finding.Subject == "dynamodb:PutItem");
    }

    [Fact]
    public void New_unaccepted_design_gap_blocks_profile()
    {
        var manifest = LoadManifest("secretsmanager-basic-lifecycle.yaml");
        manifest.AcceptedDesignGaps.Remove(
            "secretsmanager:Versioning and staging modelled on Key Vault version tags");

        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, 16));

        Assert.Equal("blocked", report.Verdict);
        Assert.Contains(
            report.Findings,
            finding => finding.Code == "design_gap_not_accepted"
                       && finding.Subject.Contains("Versioning and staging", StringComparison.Ordinal));
    }

    [Fact]
    public void Expired_real_azure_seal_yields_conditional()
    {
        var manifest = LoadManifest("dynamodb-basic-crud.yaml");
        manifest.RealAzureSealMaxAgeDays = 1;

        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, 18));

        Assert.Equal("conditional", report.Verdict);
        Assert.Contains(report.Findings, finding => finding.Code == "real_azure_seal_expired");
    }

    [Fact]
    public void Json_renderer_is_deterministic_and_machine_readable()
    {
        var manifest = LoadManifest("s3-basic-object-crud.yaml");
        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, 16));

        var first = WorkloadGaRenderer.RenderJson(report);
        var second = WorkloadGaRenderer.RenderJson(report);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.Equal("s3-basic-object-crud", document.RootElement.GetProperty("profile_id").GetString());
        Assert.Equal("candidate", document.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public void Validate_requires_every_pattern_operation_in_profile()
    {
        var manifest = LoadManifest("dynamodb-basic-crud.yaml");
        manifest.Operations.Remove("dynamodb:DeleteItem");

        var errors = WorkloadGaManifestValidator.Validate(manifest, Operations, Designs);

        Assert.Contains(
            errors,
            error => error.Contains(
                "requirement 'dynamodb_basic_crud' operation 'dynamodb:DeleteItem' is missing",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Required_scenario_must_be_backed_by_real_azure_evidence()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-ga-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            tempRoot,
            "docs",
            "workloads",
            "evidence",
            "qualification.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var manifest = MinimalManifest();
        manifest.Evidence.RequiredRealAzureScenarios = ["required-load"];
        var qualification = QualifiedDocument();
        qualification.Scenarios.Insert(
            0,
            new SloQualificationScenario
            {
                Id = "required-load",
                Service = "s3",
                Operation = "PutObject",
                EvidenceSource = "emulator",
                Completions = 1000,
                DurationSeconds = 300,
                CapturedAtUtc = new DateTimeOffset(2026, 7, 16, 15, 59, 0, TimeSpan.Zero),
            });
        SloQualificationRenderer.RenderYaml(qualification, evidencePath);

        try
        {
            var report = WorkloadGaEvaluator.Evaluate(
                manifest,
                MinimalOperations(),
                [],
                tempRoot,
                new DateOnly(2026, 7, 16));

            Assert.Equal("candidate", report.Verdict);
            Assert.Contains(
                report.Findings,
                finding => finding.Code == "required_scenario_source_mismatch"
                           && finding.Subject == "required-load");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Deterministic_operational_scenario_can_satisfy_manifest_when_not_marked_live()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-ga-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            tempRoot,
            "docs",
            "workloads",
            "evidence",
            "qualification.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var qualification = QualifiedDocument();
        qualification.Scenarios[0].Id = "required-load";
        qualification.Scenarios[0].EvidenceSource = "deterministic";
        qualification.Signals[0].ScenarioId = "required-load";
        qualification.Signals[0].Source = "proxy_overhead";
        qualification.Scenarios.Add(new SloQualificationScenario
        {
            Id = "capacity",
            Service = "s3",
            Operation = "PutObject",
            EvidenceSource = "real_azure",
            Completions = 1000,
            DurationSeconds = 300,
            CapturedAtUtc = qualification.Provenance.WindowEndUtc,
        });
        qualification.Signals.Add(new SloQualificationSignal
        {
            Id = "capacity-p99",
            ScenarioId = "capacity",
            Source = "backend_capacity",
            Disposition = "blocking",
            Metric = "p99_ms",
            MaxValue = 1000,
            MeasuredValue = 500,
            Samples = 1000,
            CapturedAtUtc = qualification.Provenance.WindowEndUtc,
        });
        SloQualificationRenderer.RenderYaml(qualification, evidencePath);

        try
        {
            var report = WorkloadGaEvaluator.Evaluate(
                MinimalManifest(),
                MinimalOperations(),
                [],
                tempRoot,
                new DateOnly(2026, 7, 16));

            Assert.Equal("ga", report.Verdict);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Matching_qualified_real_azure_evidence_yields_ga()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-ga-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            tempRoot,
            "docs",
            "workloads",
            "evidence",
            "qualification.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var qualification = QualifiedDocument();
        qualification.Scenarios[0].Id = "required-load";
        qualification.Signals.ForEach(signal => signal.ScenarioId = "required-load");
        SloQualificationRenderer.RenderYaml(qualification, evidencePath);

        try
        {
            var report = WorkloadGaEvaluator.Evaluate(
                MinimalManifest(),
                MinimalOperations(),
                [],
                tempRoot,
                new DateOnly(2026, 7, 16));

            Assert.Equal("ga", report.Verdict);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    public static TheoryData<string> EvidenceArtifactTrustMutations => new()
    {
        "correctness_missing",
        "source_missing",
        "schema_version",
        "profile_id",
        "repository",
        "workflow_path",
        "event_name",
        "conclusion",
        "run_id",
        "run_attempt",
        "run_url",
        "head_sha",
        "head_ref",
        "artifact_missing",
        "artifact_id",
        "artifact_name",
        "upload_digest",
        "created_at",
        "expires_at",
        "correctness_workflow_path",
        "correctness_artifact_name",
    };

    [Theory]
    [MemberData(nameof(EvidenceArtifactTrustMutations))]
    public void Committed_qualified_artifact_rejects_tampered_run_artifact_trust(
        string mutation)
    {
        var tempRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"aws2azure-ga-trust-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            tempRoot,
            "docs",
            "workloads",
            "evidence",
            "qualification.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var qualification = QualifiedDocument();
        qualification.Scenarios[0].Id = "required-load";
        qualification.Signals.ForEach(signal => signal.ScenarioId = "required-load");
        MutateEvidenceArtifactTrust(qualification, mutation);
        SloQualificationRenderer.RenderYaml(qualification, evidencePath);

        try
        {
            var report = WorkloadGaEvaluator.Evaluate(
                MinimalManifest(),
                MinimalOperations(),
                [],
                tempRoot,
                new DateOnly(2026, 7, 16));

            Assert.Equal("candidate", report.Verdict);
            Assert.Contains(
                report.Findings,
                finding => finding.Code == "qualification_evidence_invalid"
                           && finding.Message.Contains(
                               "evidence_artifact",
                               StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void MutateEvidenceArtifactTrust(
        SloQualificationDocument qualification,
        string mutation)
    {
        var correctness = qualification.Provenance.CorrectnessRun!;
        var source = qualification.Provenance.SourceRuns[0];
        var selection = source.EvidenceArtifact!;
        switch (mutation)
        {
            case "correctness_missing":
                correctness.EvidenceArtifact = null;
                break;
            case "source_missing":
                source.EvidenceArtifact = null;
                break;
            case "schema_version":
                selection.SchemaVersion++;
                break;
            case "profile_id":
                selection.ProfileId = "other-profile";
                break;
            case "repository":
                selection.Repository = "other/repository";
                break;
            case "workflow_path":
                selection.WorkflowPath = ".github/workflows/arbitrary.yml";
                break;
            case "event_name":
                selection.EventName = "pull_request";
                break;
            case "conclusion":
                selection.Conclusion = "failure";
                break;
            case "run_id":
                selection.RunId++;
                break;
            case "run_attempt":
                selection.RunAttempt++;
                break;
            case "run_url":
                selection.RunUrl = "https://github.com/example/repo/actions/runs/999";
                break;
            case "head_sha":
                selection.HeadSha = "1111111111111111111111111111111111111111";
                break;
            case "head_ref":
                selection.HeadRef = "refs/tags/v1.0.0-rc1";
                break;
            case "artifact_missing":
                selection.Artifact = null!;
                break;
            case "artifact_id":
                selection.Artifact.Id = 0;
                break;
            case "artifact_name":
                selection.Artifact.Name = "arbitrary-artifact";
                break;
            case "upload_digest":
                selection.Artifact.UploadDigest = "sha256:invalid";
                break;
            case "created_at":
                selection.Artifact.CreatedAt = default;
                break;
            case "expires_at":
                selection.Artifact.ExpiresAt = new DateTimeOffset(
                    2026,
                    7,
                    16,
                    0,
                    0,
                    0,
                    TimeSpan.Zero);
                break;
            case "correctness_workflow_path":
                correctness.EvidenceArtifact!.WorkflowPath =
                    ".github/workflows/workload-load-real-azure.yml";
                break;
            case "correctness_artifact_name":
                correctness.EvidenceArtifact!.Artifact.Name =
                    "real-azure-workload-load-s3-basic-object-crud";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static WorkloadGaManifest MinimalManifest() => new()
    {
        SchemaVersion = 1,
        Id = "s3-basic-object-crud",
        Version = 1,
        Name = "S3 basic object CRUD",
        MinimumProxyVersion = "0.1.0",
        RealAzureSealMaxAgeDays = 90,
        Operations = ["s3:PutObject"],
        Evidence = new WorkloadGaEvidence
        {
            QualificationArtifact = "docs/workloads/evidence/qualification.yaml",
            RequiredScenarios = ["required-load"],
        },
    };

    private static IReadOnlyList<OperationDoc> MinimalOperations() =>
    [
        new OperationDoc
        {
            Service = "s3",
            Operation = "PutObject",
            AzureEquivalent = "PUT blob",
            Status = "implemented",
            VerifiedRealAzure = new RealAzureVerification
            {
                Date = "2026-07-16",
                Evidence = "https://example.com/evidence",
            },
        },
    ];

    private static SloQualificationDocument QualifiedDocument()
    {
        var capturedAt = new DateTimeOffset(2026, 7, 16, 15, 59, 0, TimeSpan.Zero);
        var document = new SloQualificationDocument
        {
            SchemaVersion = 1,
            ArtifactKind = "real_azure_workload_qualification",
            Verdict = "qualified",
            Profile = new SloQualificationProfile
            {
                Id = "s3-basic-object-crud",
                Version = 1,
                Services =
                [
                    new SloQualificationProfileService
                    {
                        Service = "s3",
                        Operations = ["PutObject"],
                    },
                ],
            },
            Candidate = new SloQualificationCandidate
            {
                GitSha = "0123456789abcdef",
                ArtifactDigest = "sha256:artifact",
                ConfigDigest = "sha256:config",
            },
            Provenance = new SloQualificationProvenance
            {
                RunId = "124",
                RunUrl = "https://github.com/example/repo/actions/runs/124",
                RunAttempt = 1,
                GeneratedAtUtc = capturedAt,
                WindowStartUtc = capturedAt.AddMinutes(-5),
                WindowEndUtc = capturedAt,
                Region = "eastus2",
                BackendDescription = "Blob Storage Standard_LRS",
                CorrectnessRun = new SloQualificationSourceRun
                {
                    RunId = "122",
                    RunUrl = "https://github.com/example/repo/actions/runs/122",
                    RunAttempt = 1,
                    WindowStartUtc = capturedAt.AddMinutes(-10),
                    WindowEndUtc = capturedAt.AddMinutes(-6),
                    GitSha = "0123456789abcdef",
                    ArtifactDigest = "sha256:artifact",
                    ConfigDigest = "sha256:config",
                },
                SourceRuns =
                [
                    new SloQualificationSourceRun
                    {
                        RunId = "123",
                        RunUrl = "https://github.com/example/repo/actions/runs/123",
                        RunAttempt = 1,
                        WindowStartUtc = capturedAt.AddMinutes(-5),
                        WindowEndUtc = capturedAt,
                        GitSha = "0123456789abcdef",
                        ArtifactDigest = "sha256:artifact",
                        ConfigDigest = "sha256:config",
                    }
                ],
            },
            Rules = new SloQualificationRules
            {
                MaxArtifactAgeHours = 72,
                MinSamplesPerScenario = 100,
                MinDurationSeconds = 300,
                MaxFailureRate = 0.001,
                ZeroCompletionsDisqualify = true,
                OnlySkippedRealAzureDisqualifies = true,
                MinDistinctRuns = 1,
            },
            Signals =
            [
                new SloQualificationSignal
                {
                    Id = "p99",
                    ScenarioId = "real-load",
                    Source = "backend_capacity",
                    Disposition = "blocking",
                    Metric = "p99_ms",
                    MaxValue = 1000,
                    MeasuredValue = 500,
                    Samples = 1000,
                    CapturedAtUtc = capturedAt,
                },
            ],
            Scenarios =
            [
                new SloQualificationScenario
                {
                    Id = "real-load",
                    Service = "s3",
                    Operation = "PutObject",
                    EvidenceSource = "real_azure",
                    Completions = 1000,
                    DurationSeconds = 300,
                    CapturedAtUtc = capturedAt,
                },
            ],
        };
        QualificationTrustTestData.AttachSealedTrust(document, capturedAt.AddMinutes(1));
        return document;
    }

    private static WorkloadGaManifest LoadManifest(string fileName) =>
        WorkloadGaManifestLoader.Load(Path.Combine(RepoRoot, "docs", "workloads", fileName));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
