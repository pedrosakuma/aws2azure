using System.Security.Cryptography;
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
    [InlineData("s3-basic-object-crud.yaml", "ga")]
    [InlineData("secretsmanager-basic-lifecycle.yaml", "ga")]
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
            new DateOnly(2026, 7, 18));

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

    public static TheoryData<string> PriorIdentityTrustMutations => new()
    {
        "schema_version",
        "role",
        "profile_id",
        "profile_version",
        "status",
        "rollback_baseline_eligible",
        "promotion_eligible",
        "ledger_record_digest",
        "source_null",
        "source_repository",
        "source_sha",
        "source_ref",
        "runtime_aggregate_digest",
        "runtime_executable_digest",
        "runtime_manifest_digest",
        "producer_workflow",
        "producer_event_name",
        "producer_run_id",
        "producer_run_attempt",
        "producer_run_url",
        "producer_attempt_url",
        "producer_run_started_at",
        "artifact_id",
        "artifact_name",
        "artifact_upload_digest",
        "artifact_created_at",
        "artifact_expires_at",
        "attestation_predicate_type",
        "attestation_repository",
        "attestation_signer_workflow",
        "attestation_source_sha",
        "attestation_source_ref",
        "attestation_run_invocation_url",
        "attestation_bundle_digest",
        "attestation_executable_subject_name",
        "attestation_executable_subject_digest",
        "attestation_manifest_subject_name",
        "attestation_manifest_subject_digest",
    };

    [Theory]
    [MemberData(nameof(PriorIdentityTrustMutations))]
    public void Approved_ledger_rejects_rehashed_qualification_with_tampered_prior(
        string mutation)
    {
        var tempRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"aws2azure-ga-prior-trust-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            tempRoot,
            "docs",
            "workloads",
            "evidence",
            "s3-basic-object-crud.yaml");
        var ledgerPath = Path.Combine(
            tempRoot,
            "docs",
            "workloads",
            "approved-runtimes",
            "s3-basic-object-crud.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);

        var sourceEvidencePath = Path.Combine(
            RepoRoot,
            "docs",
            "workloads",
            "evidence",
            "s3-basic-object-crud.yaml");
        var sourceLedgerPath = Path.Combine(
            RepoRoot,
            "docs",
            "workloads",
            "approved-runtimes",
            "s3-basic-object-crud.yaml");
        var qualification = SloQualificationLoader.Load(sourceEvidencePath);
        foreach (var proof in qualification.RollbackProofs)
        {
            MutatePriorIdentity(proof.Prior, mutation);
        }
        SloQualificationRenderer.RenderYaml(qualification, evidencePath);

        var sourceLedger = ApprovedRuntimeLedgerLoader.Load(sourceLedgerPath);
        var oldDigest = sourceLedger.Qualification!.Digest;
        var newDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(evidencePath)));
        var ledgerYaml = File.ReadAllText(sourceLedgerPath).Replace(
            oldDigest,
            newDigest,
            StringComparison.Ordinal);
        File.WriteAllText(ledgerPath, ledgerYaml);

        try
        {
            var report = WorkloadGaEvaluator.Evaluate(
                LoadManifest("s3-basic-object-crud.yaml"),
                Operations,
                Designs,
                tempRoot,
                new DateOnly(2026, 7, 18));

            Assert.Equal("candidate", report.Verdict);
            Assert.Contains(
                report.Findings,
                finding => finding.Code is "rollback_ledger_mismatch"
                    or "qualification_evidence_invalid");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void MutatePriorIdentity(
        QualificationSealedRuntimeIdentity identity,
        string mutation)
    {
        switch (mutation)
        {
            case "schema_version":
                identity.SchemaVersion++;
                break;
            case "role":
                identity.Role = "candidate";
                break;
            case "profile_id":
                identity.Profile.Id = "other-profile";
                break;
            case "profile_version":
                identity.Profile.Version++;
                break;
            case "status":
                identity.Status = "approved";
                identity.Eligibility.PromotionEligible = true;
                break;
            case "rollback_baseline_eligible":
                identity.Eligibility.RollbackBaselineEligible = false;
                break;
            case "promotion_eligible":
                identity.Eligibility.PromotionEligible = true;
                break;
            case "ledger_record_digest":
                identity.LedgerRecordDigest = Digest('1');
                break;
            case "source_null":
                identity.Source = null!;
                break;
            case "source_repository":
                identity.Source.Repository = "other/repository";
                identity.Producer.RunUrl =
                    $"https://github.com/{identity.Source.Repository}/actions/runs/" +
                    identity.Producer.RunId;
                identity.Producer.AttemptUrl =
                    identity.Producer.RunUrl + "/attempts/" + identity.Producer.RunAttempt;
                identity.Attestation.Repository = identity.Source.Repository;
                identity.Attestation.SignerWorkflow =
                    identity.Source.Repository + "/.github/workflows/sealed-runtime.yml";
                identity.Attestation.RunInvocationUrl = identity.Producer.AttemptUrl;
                break;
            case "source_sha":
                identity.Source.Sha = new string('1', 40);
                identity.Attestation.SourceSha = identity.Source.Sha;
                break;
            case "source_ref":
                identity.Source.Ref = "refs/tags/v1.0.0-rc1";
                identity.Attestation.SourceRef = identity.Source.Ref;
                break;
            case "runtime_aggregate_digest":
                identity.Runtime.AggregateDigest = Digest('1');
                RebindArtifactName(identity);
                break;
            case "runtime_executable_digest":
                identity.Runtime.ExecutableDigest = Digest('2');
                identity.Attestation.ExecutableSubjectDigest =
                    identity.Runtime.ExecutableDigest;
                break;
            case "runtime_manifest_digest":
                identity.Runtime.ManifestDigest = Digest('3');
                identity.Attestation.ManifestSubjectDigest = identity.Runtime.ManifestDigest;
                break;
            case "producer_workflow":
                identity.Producer.Workflow = ".github/workflows/other.yml";
                break;
            case "producer_event_name":
                identity.Producer.EventName = "pull_request";
                break;
            case "producer_run_id":
                identity.Producer.RunId++;
                identity.Producer.RunUrl =
                    $"https://github.com/{identity.Source.Repository}/actions/runs/" +
                    identity.Producer.RunId;
                identity.Producer.AttemptUrl =
                    identity.Producer.RunUrl + "/attempts/" + identity.Producer.RunAttempt;
                identity.Attestation.RunInvocationUrl = identity.Producer.AttemptUrl;
                RebindArtifactName(identity);
                break;
            case "producer_run_attempt":
                identity.Producer.RunAttempt++;
                identity.Producer.AttemptUrl =
                    identity.Producer.RunUrl + "/attempts/" + identity.Producer.RunAttempt;
                identity.Attestation.RunInvocationUrl = identity.Producer.AttemptUrl;
                RebindArtifactName(identity);
                break;
            case "producer_run_url":
                identity.Producer.RunUrl = "https://github.com/example/repo/actions/runs/1";
                break;
            case "producer_attempt_url":
                identity.Producer.AttemptUrl =
                    "https://github.com/example/repo/actions/runs/1/attempts/1";
                break;
            case "producer_run_started_at":
                identity.Producer.RunStartedAt = identity.Producer.RunStartedAt.AddSeconds(1);
                break;
            case "artifact_id":
                identity.Artifact.Id++;
                break;
            case "artifact_name":
                identity.Artifact.Name += "-tampered";
                break;
            case "artifact_upload_digest":
                identity.Artifact.UploadDigest = Digest('4');
                break;
            case "artifact_created_at":
                identity.Artifact.CreatedAt = identity.Artifact.CreatedAt.AddSeconds(1);
                break;
            case "artifact_expires_at":
                identity.Artifact.ExpiresAt = identity.Artifact.ExpiresAt.AddSeconds(1);
                break;
            case "attestation_predicate_type":
                identity.Attestation.PredicateType = "https://example.invalid/predicate";
                break;
            case "attestation_repository":
                identity.Attestation.Repository = "other/repository";
                break;
            case "attestation_signer_workflow":
                identity.Attestation.SignerWorkflow =
                    "other/repository/.github/workflows/sealed-runtime.yml";
                break;
            case "attestation_source_sha":
                identity.Attestation.SourceSha = new string('2', 40);
                break;
            case "attestation_source_ref":
                identity.Attestation.SourceRef = "refs/tags/v1.0.0-rc1";
                break;
            case "attestation_run_invocation_url":
                identity.Attestation.RunInvocationUrl =
                    "https://github.com/example/repo/actions/runs/1/attempts/1";
                break;
            case "attestation_bundle_digest":
                identity.Attestation.BundleDigest = Digest('5');
                break;
            case "attestation_executable_subject_name":
                identity.Attestation.ExecutableSubjectName = "Other";
                break;
            case "attestation_executable_subject_digest":
                identity.Attestation.ExecutableSubjectDigest = Digest('6');
                break;
            case "attestation_manifest_subject_name":
                identity.Attestation.ManifestSubjectName = "other.json";
                break;
            case "attestation_manifest_subject_digest":
                identity.Attestation.ManifestSubjectDigest = Digest('7');
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static void RebindArtifactName(QualificationSealedRuntimeIdentity identity)
    {
        identity.Artifact.Name =
            $"aws2azure-sealed-linux-x64-{identity.Runtime.AggregateDigest["sha256:".Length..]}" +
            $"-run-{identity.Producer.RunId}-attempt-{identity.Producer.RunAttempt}";
    }

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    [Fact]
    public void Null_nested_candidate_and_prior_identities_return_validation_errors()
    {
        var candidateMalformed = SloQualificationLoader.Load(Path.Combine(
            RepoRoot,
            "docs",
            "workloads",
            "evidence",
            "s3-basic-object-crud.yaml"));
        candidateMalformed.Candidate.Runtime!.Source = null!;

        var candidateErrors = SloQualificationValidator.Validate(
            candidateMalformed,
            new DateTimeOffset(2026, 7, 18, 5, 0, 0, TimeSpan.Zero));

        Assert.NotEmpty(candidateErrors);

        var priorMalformed = SloQualificationLoader.Load(Path.Combine(
            RepoRoot,
            "docs",
            "workloads",
            "evidence",
            "s3-basic-object-crud.yaml"));
        priorMalformed.RollbackProofs[0].Prior = null!;

        var priorErrors = SloQualificationValidator.Validate(
            priorMalformed,
            new DateTimeOffset(2026, 7, 18, 5, 0, 0, TimeSpan.Zero));

        Assert.NotEmpty(priorErrors);

        var digestMalformed = SloQualificationLoader.Load(Path.Combine(
            RepoRoot,
            "docs",
            "workloads",
            "evidence",
            "s3-basic-object-crud.yaml"));
        digestMalformed.Provenance.CorrectnessRun!.EvidenceArtifact!.Artifact.UploadDigest =
            null!;
        digestMalformed.RollbackProofs[0].CandidateConfigDigest = null!;

        var digestErrors = SloQualificationValidator.Validate(
            digestMalformed,
            new DateTimeOffset(2026, 7, 18, 5, 0, 0, TimeSpan.Zero));

        Assert.NotEmpty(digestErrors);
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
