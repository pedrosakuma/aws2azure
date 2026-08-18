using System.Security.Cryptography;
using System.Text.Json;
using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class WorkloadGaCertificationTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly WorkloadGaEvaluationContract EvaluationContract =
        WorkloadGaEvaluationContractLoader.Load(Path.Combine(
            RepoRoot,
            WorkloadGaEvaluationMetadataBuilder.ContractPath));
    private static readonly WorkloadGaEvaluationMetadata Evaluation =
        WorkloadGaEvaluationMetadataBuilder.Build(EvaluationContract, RepoRoot);
    private static readonly IReadOnlyList<OperationDoc> Operations =
        Loader.LoadAll(Path.Combine(RepoRoot, "docs", "gaps"));
    private static readonly IReadOnlyList<ServiceDesignDoc> Designs =
        Loader.LoadDesignDocs(Path.Combine(RepoRoot, "docs", "gaps"));

    [Theory]
    [InlineData("s3-basic-object-crud.yaml", "ga", 22)]
    [InlineData("secretsmanager-basic-lifecycle.yaml", "ga", 22)]
    [InlineData("sqs-standard-messaging.yaml", "ga", 22)]
    [InlineData("dynamodb-basic-crud.yaml", "conditional", 22)]
    [InlineData("dynamodb-query-scan-indexes.yaml", "conditional", 22)]
    [InlineData("dynamodb-single-partition-transactions.yaml", "ga", 27)]
    [InlineData("sns-standard-publish-service-bus.yaml", "candidate", 22)]
    [InlineData("sns-standard-publish-event-grid.yaml", "candidate", 22)]
    [InlineData("kinesis-basic-record-ingestion.yaml", "candidate", 22)]
    public void Repository_profiles_have_expected_mechanical_verdict(
        string fileName,
        string expectedVerdict,
        int evaluationDay)
    {
        var manifest = LoadManifest(fileName);

        Assert.Empty(WorkloadGaManifestValidator.Validate(manifest, Operations, Designs));
        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, evaluationDay));

        Assert.Equal(expectedVerdict, report.Verdict);
    }

    [Fact]
    public void Transaction_profile_is_qualified_and_approved()
    {
        var manifest = LoadManifest(
            "dynamodb-single-partition-transactions.yaml");

        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, 27));

        Assert.Equal("ga", report.Verdict);
        Assert.DoesNotContain(
            report.Findings,
            finding => finding.Disposition == "blocking");
        Assert.Equal(
            "docs/workloads/evidence/dynamodb-single-partition-transactions.yaml",
            manifest.Evidence.QualificationArtifact);
        Assert.Empty(manifest.Evidence.RollbackStatus);
        Assert.Empty(manifest.Evidence.RollbackBlocker);
    }

    [Fact]
    public void Blocked_rollback_state_requires_canonical_scenario_and_reason()
    {
        var manifest = MinimalManifest();
        manifest.Evidence.RollbackStatus = "blocked";
        manifest.Evidence.RollbackBlocker = string.Empty;

        var errors = WorkloadGaManifestValidator.Validate(
            manifest,
            MinimalOperations(),
            Designs);

        Assert.Contains(
            errors,
            error => error.Contains(
                "canonical 'rollback'",
                StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains(
                "rollback_blocker is required",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_qualification_findings_never_embed_the_absolute_checkout_path()
    {
        // Regression coverage (issue #626): committed generated output
        // (docs/site/workload-ga.json/.md) must be identical no matter which
        // machine's checkout path produced it. Evaluating far enough past the
        // committed evidence's freshness window forces real staleness
        // findings; every message must reference the repo-relative evidence
        // path, never the resolved absolute one.
        var manifest = LoadManifest("s3-basic-object-crud.yaml");
        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 7, 25));

        Assert.Equal("candidate", report.Verdict);
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, finding =>
        {
            Assert.DoesNotContain(RepoRoot, finding.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(RepoRoot, finding.Subject, StringComparison.Ordinal);
        });
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
    public void Seal_after_explicit_evaluation_date_cannot_authorize_a_verdict()
    {
        var operations = MinimalOperations();
        operations[0].VerifiedRealAzure!.Date = "2026-07-17";

        var report = WorkloadGaEvaluator.Evaluate(
            MinimalManifest(),
            operations,
            [],
            RepoRoot,
            new DateOnly(2026, 7, 16));

        Assert.Equal("conditional", report.Verdict);
        Assert.Contains(
            report.Findings,
            finding => finding.Code == "real_azure_seal_after_evaluation");
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
            WorkloadGaEvaluationMetadataBuilder.ParseAsOf(EvaluationContract));

        var first = WorkloadGaRenderer.RenderJson(report, Evaluation);
        var second = WorkloadGaRenderer.RenderJson(report, Evaluation);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("2026-08-18", root.GetProperty("evaluation").GetProperty("evaluated_as_of").GetString());
        Assert.Equal(
            "pedrosakuma/aws2azure",
            root.GetProperty("evaluation").GetProperty("source").GetProperty("repository").GetString());
        Assert.Matches(
            "^sha256:[0-9a-f]{64}$",
            root.GetProperty("evaluation").GetProperty("source")
                .GetProperty("canonical_inputs_revision").GetString());
        Assert.Equal(
            "normalized_yaml_sha256",
            root.GetProperty("evaluation").GetProperty("source")
                .GetProperty("canonical_inputs_revision_type").GetString());
        Assert.Equal(
            WorkloadGaEvaluationMetadataBuilder.CurrentEvaluatorRevision,
            root.GetProperty("evaluation").GetProperty("source")
                .GetProperty("evaluator_revision").GetInt32());
        Assert.Equal(
            "workload_ga_evaluator_schema_version",
            root.GetProperty("evaluation").GetProperty("source")
                .GetProperty("evaluator_revision_type").GetString());
        Assert.Equal(
            "live_workload_certification",
            root.GetProperty("authority").GetProperty("highest_precedence_source").GetString());
        Assert.False(root.GetProperty("authority").GetProperty("historical_claims_may_override").GetBoolean());
        Assert.Equal(
            "s3-basic-object-crud",
            root.GetProperty("profile").GetProperty("profile_id").GetString());
        Assert.Equal("candidate", root.GetProperty("profile").GetProperty("verdict").GetString());
    }

    [Fact]
    public void Evaluation_contract_is_valid_and_uses_an_explicit_point_in_time()
    {
        Assert.Empty(WorkloadGaEvaluationContractValidator.Validate(
            EvaluationContract,
            new DateOnly(2026, 8, 18),
            WorkloadGaEvaluationMetadataBuilder.ComputeCanonicalInputRevision(RepoRoot)));
        Assert.Equal(
            new DateOnly(2026, 8, 18),
            WorkloadGaEvaluationMetadataBuilder.ParseAsOf(EvaluationContract));
    }

    [Fact]
    public void Evaluation_contract_rejects_non_deterministic_or_unidentified_inputs()
    {
        var contract = new WorkloadGaEvaluationContract
        {
            SchemaVersion = 3,
            AsOf = "now",
            SourceRepository = "missing-repository",
        };

        var errors = WorkloadGaEvaluationContractValidator.Validate(
            contract,
            new DateOnly(2026, 8, 18));

        Assert.Contains(errors, error => error.Contains("unsupported schema_version", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("as_of must be", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("owner/repository", StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains("expected_canonical_inputs_revision", StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains("expected_evaluator_revision", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2026, 8, 17, false)]
    [InlineData(2026, 8, 18, false)]
    [InlineData(2026, 8, 19, true)]
    public void Evaluation_contract_rejects_only_dates_after_trusted_utc_today(
        int year,
        int month,
        int day,
        bool rejected)
    {
        var contract = new WorkloadGaEvaluationContract
        {
            SchemaVersion = WorkloadGaEvaluationContractValidator.CurrentSchemaVersion,
            AsOf = new DateOnly(year, month, day).ToString("yyyy-MM-dd"),
            SourceRepository = "pedrosakuma/aws2azure",
            ExpectedCanonicalInputsRevision = Digest('a'),
            ExpectedEvaluatorRevision =
                WorkloadGaEvaluationMetadataBuilder.CurrentEvaluatorRevision,
        };

        var errors = WorkloadGaEvaluationContractValidator.Validate(
            contract,
            new DateOnly(2026, 8, 18));

        Assert.Equal(
            rejected,
            errors.Any(error => error.Contains("trusted UTC date", StringComparison.Ordinal)));
    }

    [Fact]
    public void Evaluation_contract_rejects_revision_mismatch()
    {
        var contract = new WorkloadGaEvaluationContract
        {
            SchemaVersion = WorkloadGaEvaluationContractValidator.CurrentSchemaVersion,
            AsOf = "2026-08-18",
            SourceRepository = "pedrosakuma/aws2azure",
            ExpectedCanonicalInputsRevision = Digest('a'),
            ExpectedEvaluatorRevision = 1,
        };

        var errors = WorkloadGaEvaluationContractValidator.Validate(
            contract,
            new DateOnly(2026, 8, 18),
            Digest('c'));

        Assert.Contains(
            errors,
            error => error.Contains(
                "expected_canonical_inputs_revision",
                StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains("expected_evaluator_revision", StringComparison.Ordinal));
    }

    [Fact]
    public void Temporal_validator_rejects_qualification_just_after_as_of_cutoff()
    {
        var qualification = QualifiedDocument();
        qualification.Provenance.GeneratedAtUtc =
            new DateTimeOffset(2026, 7, 17, 0, 1, 0, TimeSpan.Zero);

        var errors = WorkloadGaTemporalValidator.ValidateQualification(
            qualification,
            new DateOnly(2026, 7, 16));

        Assert.Contains(
            errors,
            error => error.StartsWith(
                "provenance.generated_at_utc postdates evaluated_as_of",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Temporal_validator_rejects_source_artifact_created_after_as_of_cutoff()
    {
        var qualification = QualifiedDocument();
        qualification.Provenance.CorrectnessRun!.EvidenceArtifact!.Artifact.CreatedAt =
            new DateTimeOffset(2026, 7, 17, 0, 1, 0, TimeSpan.Zero);

        var errors = WorkloadGaTemporalValidator.ValidateQualification(
            qualification,
            new DateOnly(2026, 7, 16));

        Assert.Contains(
            errors,
            error => error.StartsWith(
                "provenance.correctness_run.evidence_artifact.artifact.created_at " +
                "postdates evaluated_as_of",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Temporal_validator_rejects_approval_after_as_of_cutoff()
    {
        var record = ApprovedRuntimeLedgerLoader.Load(Path.Combine(
            RepoRoot,
            "docs",
            "workloads",
            "approved-runtimes",
            "s3-basic-object-crud.yaml"));
        record.Approval.ReviewedAt =
            new DateTimeOffset(2026, 7, 17, 0, 1, 0, TimeSpan.Zero);

        var errors = WorkloadGaTemporalValidator.ValidateApprovedRuntime(
            record,
            new DateOnly(2026, 7, 16));

        Assert.Contains(
            errors,
            error => error.StartsWith(
                "approval.reviewed_at postdates evaluated_as_of",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Canonical_input_revision_is_checkout_and_line_ending_independent()
    {
        var first = WorkloadGaEvaluationMetadataBuilder.ComputeCanonicalInputRevision(RepoRoot);
        var second = WorkloadGaEvaluationMetadataBuilder.ComputeCanonicalInputRevision(RepoRoot);

        Assert.Equal(first, second);
        Assert.Matches("^sha256:[0-9a-f]{64}$", first);
        Assert.DoesNotContain(RepoRoot, first, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_input_revision_changes_only_with_canonical_yaml()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-ga-revision-{Guid.NewGuid():N}");
        var gapsRoot = Path.Combine(tempRoot, "docs", "gaps", "s3");
        var workloadsRoot = Path.Combine(tempRoot, "docs", "workloads");
        Directory.CreateDirectory(gapsRoot);
        Directory.CreateDirectory(workloadsRoot);
        var gapPath = Path.Combine(gapsRoot, "PutObject.yaml");
        try
        {
            File.WriteAllText(gapPath, "status: implemented\r\n");
            File.WriteAllText(Path.Combine(workloadsRoot, "profile.yaml"), "version: 1\r\n");
            Directory.CreateDirectory(Path.Combine(workloadsRoot, "certification"));
            File.WriteAllText(
                Path.Combine(workloadsRoot, "certification", "authority.yaml"),
                "expected_canonical_inputs_revision: ignored\r\n");
            var windowsRevision =
                WorkloadGaEvaluationMetadataBuilder.ComputeCanonicalInputRevision(tempRoot);

            File.WriteAllText(gapPath, "status: implemented\n");
            File.WriteAllText(
                Path.Combine(workloadsRoot, "certification", "authority.yaml"),
                "expected_canonical_inputs_revision: changed-but-still-ignored\n");
            File.WriteAllText(Path.Combine(tempRoot, "README.md"), "not a canonical input");
            var normalizedRevision =
                WorkloadGaEvaluationMetadataBuilder.ComputeCanonicalInputRevision(tempRoot);

            File.WriteAllText(gapPath, "status: partial\n");
            var changedRevision =
                WorkloadGaEvaluationMetadataBuilder.ComputeCanonicalInputRevision(tempRoot);

            Assert.Equal(windowsRevision, normalizedRevision);
            Assert.NotEqual(normalizedRevision, changedRevision);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Canonical_snapshot_validation_rejects_changes_after_initial_hash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-ga-snapshot-{Guid.NewGuid():N}");
        var gapsRoot = Path.Combine(tempRoot, "docs", "gaps");
        var workloadsRoot = Path.Combine(tempRoot, "docs", "workloads");
        Directory.CreateDirectory(gapsRoot);
        Directory.CreateDirectory(workloadsRoot);
        try
        {
            var sourcePath = Path.Combine(gapsRoot, "PutObject.yaml");
            File.WriteAllText(sourcePath, "status: implemented\n");
            var initialRevision =
                WorkloadGaEvaluationMetadataBuilder.ComputeCanonicalInputRevision(tempRoot);

            Assert.Empty(
                WorkloadGaEvaluationMetadataBuilder.ValidateCanonicalInputSnapshot(
                    tempRoot,
                    initialRevision));

            File.WriteAllText(sourcePath, "status: partial\n");
            var errors = WorkloadGaEvaluationMetadataBuilder.ValidateCanonicalInputSnapshot(
                tempRoot,
                initialRevision);

            Assert.Contains(
                errors,
                error => error.Contains("changed during evaluation", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Index_renderer_exposes_temporal_authority_in_json_and_markdown()
    {
        var manifest = LoadManifest("s3-basic-object-crud.yaml");
        var report = WorkloadGaEvaluator.Evaluate(
            manifest,
            Operations,
            Designs,
            RepoRoot,
            new DateOnly(2026, 8, 18));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aws2azure-ga-index-{Guid.NewGuid():N}");
        var markdownPath = Path.Combine(tempRoot, "workload-ga.md");
        var jsonPath = Path.Combine(tempRoot, "workload-ga.json");
        try
        {
            WorkloadGaRenderer.RenderIndex([report], Evaluation, markdownPath, jsonPath);

            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = document.RootElement;
            Assert.Equal("point_in_time", root.GetProperty("authority").GetProperty("temporal_scope").GetString());
            Assert.Equal(
                "s3-basic-object-crud",
                root.GetProperty("profiles")[0].GetProperty("profile_id").GetString());
            var markdown = File.ReadAllText(markdownPath);
            Assert.Contains("Current adoption authority (as of `2026-08-18`)", markdown, StringComparison.Ordinal);
            Assert.Contains("| 4 | Release notes | Immutable historical record |", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Historical_release_notice_defers_to_live_certification()
    {
        var releaseNotes = File.ReadAllText(Path.Combine(
            RepoRoot,
            "docs",
            "releases",
            "v1.0.0.md"));
        var notice = releaseNotes.IndexOf("**Historical release record:**", StringComparison.Ordinal);
        var firstGaClaim = releaseNotes.IndexOf("| `s3-basic-object-crud` | 1 | GA", StringComparison.Ordinal);

        Assert.InRange(notice, 0, firstGaClaim - 1);
        Assert.Contains("live workload certification", releaseNotes, StringComparison.Ordinal);
        Assert.Contains(
            "a current `candidate`, `conditional`, or `blocked`",
            releaseNotes,
            StringComparison.Ordinal);
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
    public void Validate_rejects_required_sub_feature_seal_for_unknown_sub_feature()
    {
        var manifest = MinimalManifest();
        manifest.RequiredSubFeatureSeals = [new WorkloadGaSubFeatureSeal
        {
            Operation = "s3:PutObject",
            SubFeature = "Does not exist",
        }];

        var errors = WorkloadGaManifestValidator.Validate(manifest, MinimalOperations(), Designs);

        Assert.Contains(
            errors,
            error => error.Contains(
                "required sub-feature seal 'Does not exist' does not exist under operation 's3:PutObject'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_required_sub_feature_seal_for_operation_outside_profile()
    {
        var manifest = MinimalManifest();
        manifest.RequiredSubFeatureSeals = [new WorkloadGaSubFeatureSeal
        {
            Operation = "s3:GetObject",
            SubFeature = "Any",
        }];

        var errors = WorkloadGaManifestValidator.Validate(manifest, MinimalOperations(), Designs);

        Assert.Contains(
            errors,
            error => error.Contains(
                "required sub-feature seal operation 's3:GetObject' is not required by the profile",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_sub_feature_seal_yields_conditional_even_when_operation_seal_is_fresh()
    {
        var manifest = MinimalManifest();
        manifest.RequiredSubFeatureSeals = [new WorkloadGaSubFeatureSeal
        {
            Operation = "s3:PutObject",
            SubFeature = "Backend variant",
        }];
        var operations = new List<OperationDoc>
        {
            new()
            {
                Service = "s3",
                Operation = "PutObject",
                AzureEquivalent = "PUT blob",
                Status = "implemented",
                VerifiedRealAzure = new RealAzureVerification { Date = "2026-07-16", Evidence = "https://example.com/evidence" },
                SubFeatures =
                [
                    new SubFeature { Name = "Backend variant", Status = "implemented" },
                ],
            },
        };

        var report = WorkloadGaEvaluator.Evaluate(
            manifest, operations, Designs, RepoRoot, new DateOnly(2026, 7, 18));

        Assert.Equal("conditional", report.Verdict);
        Assert.Contains(
            report.Findings,
            finding => finding.Code == "sub_feature_real_azure_seal_missing"
                       && finding.Subject == "s3:PutObject#Backend variant");
    }

    [Fact]
    public void Fresh_sub_feature_seal_alongside_fresh_operation_seal_does_not_block()
    {
        var manifest = MinimalManifest();
        manifest.RequiredSubFeatureSeals = [new WorkloadGaSubFeatureSeal
        {
            Operation = "s3:PutObject",
            SubFeature = "Backend variant",
        }];
        var operations = new List<OperationDoc>
        {
            new()
            {
                Service = "s3",
                Operation = "PutObject",
                AzureEquivalent = "PUT blob",
                Status = "implemented",
                VerifiedRealAzure = new RealAzureVerification { Date = "2026-07-16", Evidence = "https://example.com/evidence" },
                SubFeatures =
                [
                    new SubFeature
                    {
                        Name = "Backend variant",
                        Status = "implemented",
                        VerifiedRealAzure = new RealAzureVerification { Date = "2026-07-16", Evidence = "https://example.com/evidence" },
                    },
                ],
            },
        };

        var report = WorkloadGaEvaluator.Evaluate(
            manifest, operations, Designs, RepoRoot, new DateOnly(2026, 7, 18));

        Assert.DoesNotContain(
            report.Findings,
            finding => finding.Code.StartsWith("sub_feature_real_azure_seal", StringComparison.Ordinal));
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

    [Fact]
    public void Qualification_evidence_invalid_finding_never_leaks_the_resolved_absolute_path()
    {
        // The evaluator resolves the committed repo-relative qualification_artifact
        // path to an absolute filesystem path for its own symlink/traversal checks,
        // but any finding message surfaced to a committed report must reference
        // only the repo-relative path — never a machine-local absolute path
        // (issue #627 review finding).
        var tempRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"aws2azure-ga-relative-path-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            tempRoot,
            "docs",
            "workloads",
            "evidence",
            "qualification.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var qualification = QualifiedDocument();
        qualification.SchemaVersion = 999;
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
            var finding = Assert.Single(
                report.Findings,
                finding => finding.Code == "qualification_evidence_invalid");
            Assert.DoesNotContain(tempRoot, finding.Message, StringComparison.Ordinal);
            Assert.Contains(
                "docs/workloads/evidence/qualification.yaml",
                finding.Message,
                StringComparison.Ordinal);
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
            // Regression guard: a committed report must regenerate identically
            // regardless of the checkout's absolute path. tempRoot here stands
            // in for "a different machine's checkout path" (it is itself an
            // absolute, machine-specific temp directory), so no finding may
            // embed it. This specifically covers the rollback-ledger path,
            // which is a separate leak site from the qualification-artifact
            // path already covered elsewhere.
            Assert.All(report.Findings, finding =>
            {
                Assert.DoesNotContain(tempRoot, finding.Subject, StringComparison.Ordinal);
                Assert.DoesNotContain(tempRoot, finding.Message, StringComparison.Ordinal);
            });
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
