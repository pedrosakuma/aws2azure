using Aws2Azure.GapDocs;
using YamlDotNet.Core;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RcObservationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_pass_observation_is_accepted()
    {
        var (evidence, context) = ValidEvidence();

        Assert.Empty(RcObservationValidator.Validate(evidence, context, Now));
    }

    [Fact]
    public void Valid_triggered_rollback_with_exact_prior_restoration_is_accepted()
    {
        var (evidence, context) = ValidEvidence();
        evidence = MakeRollback(evidence, context);
        (evidence, context) = Reseal(evidence, context);

        Assert.Empty(RcObservationValidator.Validate(evidence, context, Now));
    }

    [Fact]
    public void Exact_manifest_candidate_prior_and_profile_identities_are_required()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            ReleaseCandidate = evidence.ReleaseCandidate with
            {
                ManifestDigest = Digest('9'),
                ArchiveInputs = evidence.ReleaseCandidate.ArchiveInputs with
                {
                    Artifact = evidence.ReleaseCandidate.ArchiveInputs.Artifact with
                    {
                        Id = evidence.ReleaseCandidate.ArchiveInputs.Artifact.Id + 1,
                    },
                },
            },
            Candidate = evidence.Candidate with { IdentityDigest = Digest('8') },
            Prior = evidence.Prior with { RuntimeDigest = Digest('7') },
            Profile = evidence.Profile with { Version = evidence.Profile.Version + 1 },
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "trusted RC manifest identity",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "archive inputs",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "trusted RC candidate identity",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "trusted approved-runtime identity",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "profile id/version",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Azure_backend_region_config_and_binding_drift_are_rejected()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Azure = evidence.Azure with { Region = "eastus" },
            Cohorts =
            [
                evidence.Cohorts[0] with { ConfigDigest = Digest('9') },
                evidence.Cohorts[1] with { BackendIdentityDigest = Digest('8') },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "Azure backend, region, configuration, or AWS binding drift",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "backend, region, config, or binding drift",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Mixed_or_runtime_drifted_cohorts_are_rejected()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Cohorts =
            [
                evidence.Cohorts[0] with
                {
                    MemberDigests = [evidence.Cohorts[1].MemberDigests[0]],
                },
                evidence.Cohorts[1] with
                {
                    RuntimeDigest = evidence.Candidate.RuntimeDigest,
                },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "runtime identity drift",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "cohorts are mixed",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Cohorts_must_be_isolated_candidate_and_stable_sets()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Cohorts =
            [
                evidence.Cohorts[0],
                evidence.Cohorts[1] with
                {
                    Role = "candidate",
                    Id = evidence.Cohorts[0].Id,
                },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "distinct candidate and stable roles",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Incomplete_observation_window_is_rejected()
    {
        var (evidence, context) = ValidEvidence();
        var ended = evidence.Observation.StartedAtUtc.AddMinutes(59);
        evidence = evidence with
        {
            Observation = evidence.Observation with
            {
                EndedAtUtc = ended,
                GeneratedAtUtc = ended.AddMinutes(10),
            },
            Decision = evidence.Decision with
            {
                DecidedAtUtc = ended.AddMinutes(5),
            },
            Cohorts = evidence.Cohorts
                .Select(cohort => cohort with { ObservedUntilUtc = ended })
                .ToArray(),
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "window is incomplete",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_observation_window_is_rejected()
    {
        var (evidence, context) = ValidEvidence();
        evidence = ShiftWindow(evidence, TimeSpan.FromDays(-2));
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "window is stale",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Future_observation_window_is_rejected()
    {
        var (evidence, context) = ValidEvidence();
        evidence = ShiftWindow(evidence, TimeSpan.FromDays(1));
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "future timestamps",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_metrics_are_rejected(double value)
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Metrics =
            [
                evidence.Metrics[0] with { CandidateValue = value },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "malformed or non-finite",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_metric_and_unknown_trigger_reference_are_rejected()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Metrics = [evidence.Metrics[0] with { Samples = 0 }],
            RollbackTriggers =
            [
                evidence.RollbackTriggers[0] with { MetricId = "unknown" },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "malformed or non-finite",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "unknown or duplicate metric",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Threshold_breach_cannot_be_marked_pass()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Metrics = [evidence.Metrics[0] with { CandidateValue = 2 }],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "must be 'breach'",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "must be 'fired'",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "threshold breach cannot be marked pass",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Rollback_trigger_override_or_suppression_is_rejected(
        bool overridden,
        bool suppressed)
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            RollbackTriggers =
            [
                evidence.RollbackTriggers[0] with
                {
                    OverrideApplied = overridden,
                    Suppressed = suppressed,
                },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "must explicitly declare",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Omitted_metric_values_are_rejected_instead_of_defaulting_to_zero()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Metrics =
            [
                evidence.Metrics[0] with
                {
                    Threshold = null,
                    CandidateValue = null,
                    StableValue = null,
                },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "malformed or non-finite",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Omitted_trigger_safety_flags_are_rejected()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            RollbackTriggers =
            [
                evidence.RollbackTriggers[0] with
                {
                    OverrideApplied = null,
                    Suppressed = null,
                },
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "must explicitly declare",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Every_metric_requires_one_explicit_rollback_trigger()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            RollbackTriggers = Array.Empty<RcObservationRollbackTrigger>(),
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "exactly one explicit rollback trigger",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Rollback_without_verified_restoration_is_rejected()
    {
        var (evidence, context) = ValidEvidence();
        evidence = MakeRollback(evidence, context);
        evidence = evidence with
        {
            Restoration = evidence.Restoration! with { Verified = false },
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "did not verify restoration",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Rollback_must_restore_exact_prior_config_backend_and_binding()
    {
        var (evidence, context) = ValidEvidence();
        evidence = MakeRollback(evidence, context);
        evidence = evidence with
        {
            Restoration = evidence.Restoration! with
            {
                RuntimeIdentityDigest = evidence.Candidate.IdentityDigest,
                ConfigDigest = Digest('9'),
                BackendIdentityDigest = Digest('8'),
                AwsBindingDigest = Digest('7'),
            },
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "exact trusted prior environment",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Rehashed_tampering_still_fails_the_manifest_bound_digest()
    {
        var (evidence, context) = ValidEvidence();
        var trustedDigest = context.ExpectedEvidenceDigest;
        evidence = evidence with
        {
            Metrics = [evidence.Metrics[0] with { StableValue = 0.25 }],
        };
        evidence = evidence with
        {
            EvidenceDigest = RcObservationIntegrity.ComputePayloadDigest(evidence),
        };

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.NotEqual(trustedDigest, evidence.EvidenceDigest);
        Assert.Contains(errors, error => error.Contains(
            "digest bound by the trusted RC manifest",
            StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains(
            "canonical observation payload",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnostic_tampering_is_digest_bound_and_consistency_validated()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Cohorts =
            [
                evidence.Cohorts[0] with
                {
                    OperationDiagnostics =
                    [
                        OperationDiagnostic("s3", "CreateBucket", 0, 0),
                        OperationDiagnostic("s3", "DeleteBucket", 0, 0),
                        OperationDiagnostic("s3", "DeleteObject", 0, 0),
                        OperationDiagnostic("s3", "GetObject", 198, 2),
                        OperationDiagnostic("s3", "HeadObject", 0, 0),
                        OperationDiagnostic("s3", "ListObjectsV2", 0, 0),
                        OperationDiagnostic("s3", "PutObject", 800, 0),
                    ],
                },
                evidence.Cohorts[1],
            ],
        };
        evidence = evidence with
        {
            EvidenceDigest = RcObservationIntegrity.ComputePayloadDigest(evidence),
        };

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "digest bound by the trusted RC manifest",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "operation-failure-rate samples",
            StringComparison.Ordinal));
    }

    [Fact]
    public void First_failure_diagnostics_must_be_sanitized()
    {
        var (evidence, context) = ValidEvidence();
        evidence = evidence with
        {
            Cohorts =
            [
                evidence.Cohorts[0] with
                {
                    OperationDiagnostics =
                    [
                        OperationDiagnostic("s3", "CreateBucket", 0, 0),
                        OperationDiagnostic("s3", "DeleteBucket", 0, 0),
                        OperationDiagnostic("s3", "DeleteObject", 0, 0),
                        OperationDiagnostic("s3", "GetObject", 199, 1) with
                        {
                            FirstFailure = new RcObservationFirstFailure
                            {
                                Category = "aws service",
                                StatusCode = 503,
                                ErrorCode = "https://vault/secrets/name?token=abc",
                            },
                        },
                        OperationDiagnostic("s3", "HeadObject", 0, 0),
                        OperationDiagnostic("s3", "ListObjectsV2", 0, 0),
                        OperationDiagnostic("s3", "PutObject", 800, 0),
                    ],
                },
                evidence.Cohorts[1],
            ],
        };
        (evidence, context) = Reseal(evidence, context);

        var errors = RcObservationValidator.Validate(evidence, context, Now);

        Assert.Contains(errors, error => error.Contains(
            "first-failure diagnostic is not sanitized",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Payload_digest_does_not_hash_itself()
    {
        var (evidence, _) = ValidEvidence();
        var digest = RcObservationIntegrity.ComputePayloadDigest(evidence);
        evidence = evidence with { EvidenceDigest = Digest('f') };

        Assert.Equal(digest, RcObservationIntegrity.ComputePayloadDigest(evidence));
    }

    [Fact]
    public void Loader_rejects_duplicate_yaml_keys()
    {
        var path = Fixture("rc-observation-duplicate-key.yaml");

        Assert.Throws<YamlException>(() => RcObservationLoader.Load(path));
    }

    [Fact]
    public void Loader_rejects_unknown_yaml_fields()
    {
        var path = Fixture("rc-observation-unknown-field.yaml");

        Assert.Throws<YamlException>(() => RcObservationLoader.Load(path));
    }

    [Fact]
    public void Loader_materializes_read_only_evidence_collections()
    {
        var evidence = RcObservationLoader.Load(
            Fixture("rc-observation-loader-shape.yaml"));

        Assert.Single(evidence.Cohorts);
        Assert.Single(evidence.Metrics);
        Assert.Single(evidence.RollbackTriggers);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RcObservationCohort>)evidence.Cohorts).Add(new RcObservationCohort()));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)evidence.Cohorts[0].MemberDigests).Add(Digest('9')));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RcObservationOperationDiagnostic>)evidence.Cohorts[0]
                .OperationDiagnostics).Add(new RcObservationOperationDiagnostic()));
    }

    internal static (RcObservationEvidence Evidence, RcObservationValidationContext Context)
        ValidEvidenceForCalibrationGuard() => ValidEvidence();

    private static (RcObservationEvidence Evidence, RcObservationValidationContext Context)
        ValidEvidence()
    {
        var started = Now.AddHours(-2);
        var ended = Now.AddHours(-1);
        var candidate = new RcObservationRuntimeIdentity
        {
            IdentityDigest = Digest('a'),
            RuntimeDigest = Digest('b'),
            SourceSha = new string('a', 40),
        };
        var prior = new RcObservationRuntimeIdentity
        {
            IdentityDigest = Digest('c'),
            RuntimeDigest = Digest('d'),
            SourceSha = new string('b', 40),
        };
        var environment = new RcObservationAzureEnvironment
        {
            BackendKind = "blob",
            Region = "westus2",
            BackendIdentityDigest = Digest('e'),
            ConfigDigest = Digest('f'),
            AwsBindingDigest = Digest('1'),
        };
        var evidence = new RcObservationEvidence
        {
            SchemaVersion = RcObservationValidator.CurrentSchemaVersion,
            ArtifactKind = "rc_observation",
            ReleaseCandidate = new RcObservationReleaseCandidate
            {
                Id = "v1.0.0-rc.1",
                ManifestDigest = Digest('2'),
                SourceSha = candidate.SourceSha,
                ArchiveInputs = new RcObservationArchiveInputsIdentity
                {
                    ContentDigest = Digest('9'),
                    Producer = new RcObservationArchiveProducer
                    {
                        Repository = "example/repository",
                        WorkflowPath = ".github/workflows/release-candidate.yml",
                        EventName = "workflow_dispatch",
                        RunId = 12000,
                        RunAttempt = 1,
                        AttemptUrl =
                            "https://github.com/example/repository/actions/runs/" +
                            "12000/attempts/1",
                        SourceSha = new string('e', 40),
                        SourceRef = "refs/heads/main",
                    },
                    Artifact = new RcObservationArtifactIdentity
                    {
                        Id = 67000,
                        Name = "aws2azure-rc-archives-v1.0.0-rc.1-" +
                               new string('9', 64) + "-run-12000-attempt-1",
                        UploadDigest = Digest('0'),
                    },
                },
                GhcrInputs = new RcObservationGhcrInputsIdentity
                {
                    ContentDigest = Digest('0'),
                    Producer = new RcObservationArchiveProducer
                    {
                        Repository = "example/repository",
                        WorkflowPath = ".github/workflows/release-candidate-image.yml",
                        EventName = "workflow_dispatch",
                        RunId = 12100,
                        RunAttempt = 1,
                        AttemptUrl =
                            "https://github.com/example/repository/actions/runs/" +
                            "12100/attempts/1",
                        SourceSha = new string('c', 40),
                        SourceRef = "refs/heads/main",
                    },
                    Artifact = new RcObservationArtifactIdentity
                    {
                        Id = 67100,
                        Name = "aws2azure-rc-ghcr-v1.0.0-rc.1-" +
                               new string('0', 64) + "-run-12100-attempt-1",
                        UploadDigest = Digest('1'),
                    },
                    IndexDigest = Digest('2'),
                },
            },
            Candidate = candidate,
            Prior = prior,
            Profile = new RcObservationProfile
            {
                Id = "s3-basic-object-crud",
                Version = 1,
            },
            Policy = new RcObservationPolicyIdentity
            {
                WorkloadManifestDigest = Digest('5'),
                QualificationPolicyDigest = Digest('6'),
                ObservationPolicyDigest = Digest('7'),
            },
            Azure = environment,
            Producer = new RcObservationProducer
            {
                Repository = "example/repository",
                WorkflowPath = ".github/workflows/rc-observation-real-azure.yml",
                EventName = "workflow_dispatch",
                RunId = 12345,
                RunAttempt = 2,
                RunUrl = "https://github.com/example/repository/actions/runs/12345",
                AttemptUrl =
                    "https://github.com/example/repository/actions/runs/12345/attempts/2",
                SourceSha = new string('d', 40),
                SourceRef = "refs/heads/main",
            },
            CaptureArtifact = new RcObservationArtifactIdentity
            {
                Id = 67890,
                Name = "real-azure-rc-observation-capture-s3-basic-object-crud",
                UploadDigest = Digest('8'),
            },
            Observation = new RcObservationWindow
            {
                StartedAtUtc = started,
                EndedAtUtc = ended,
                GeneratedAtUtc = ended.AddMinutes(10),
                MinimumWindowMinutes = 60,
            },
            Cohorts =
            [
                Cohort(
                    "candidate-a",
                    "candidate",
                    candidate,
                    environment,
                    started,
                    ended,
                    Digest('3')),
                Cohort(
                    "stable-a",
                    "stable",
                    prior,
                    environment,
                    started,
                    ended,
                    Digest('4')),
            ],
            Metrics =
            [
                new RcObservationMetric
                {
                    Id = "operation-failure-rate",
                    Unit = "ratio",
                    Comparison = "less_than_or_equal",
                    Threshold = 0.01,
                    CandidateValue = 0.001,
                    StableValue = 0.001,
                    CandidateSamples = 1000,
                    StableSamples = 1000,
                    Samples = 1000,
                    CapturedAtUtc = started.AddMinutes(30),
                    Result = "pass",
                },
                new RcObservationMetric
                {
                    Id = "representative-load-throughput",
                    Unit = "throughput_per_sec",
                    Comparison = "greater_than_or_equal",
                    Threshold = 40,
                    CandidateValue = 50,
                    StableValue = 45,
                    CandidateSamples = 200,
                    StableSamples = 200,
                    Samples = 200,
                    CapturedAtUtc = started.AddMinutes(30),
                    Result = "pass",
                },
            ],
            RollbackTriggers =
            [
                new RcObservationRollbackTrigger
                {
                    Id = "candidate-error-rate",
                    MetricId = "operation-failure-rate",
                    Status = "armed",
                    OverrideApplied = false,
                    Suppressed = false,
                },
                new RcObservationRollbackTrigger
                {
                    Id = "candidate-throughput",
                    MetricId = "representative-load-throughput",
                    Status = "armed",
                    OverrideApplied = false,
                    Suppressed = false,
                },
            ],
            Decision = new RcObservationDecision
            {
                Verdict = "pass",
                Owner = "release-manager",
                Reason = "All reviewed thresholds passed.",
                DecidedAtUtc = ended.AddMinutes(5),
            },
        };
        var context = new RcObservationValidationContext
        {
            ReleaseCandidateId = evidence.ReleaseCandidate.Id,
            RcManifestDigest = evidence.ReleaseCandidate.ManifestDigest,
            ArchiveInputsDigest = evidence.ReleaseCandidate.ArchiveInputs.ContentDigest,
            ArchiveProducerRepository =
                evidence.ReleaseCandidate.ArchiveInputs.Producer.Repository,
            ArchiveProducerWorkflowPath =
                evidence.ReleaseCandidate.ArchiveInputs.Producer.WorkflowPath,
            ArchiveProducerRunId =
                evidence.ReleaseCandidate.ArchiveInputs.Producer.RunId,
            ArchiveProducerRunAttempt =
                evidence.ReleaseCandidate.ArchiveInputs.Producer.RunAttempt,
            ArchiveProducerSourceSha =
                evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceSha,
            ArchiveProducerSourceRef =
                evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceRef,
            ArchiveArtifactId = evidence.ReleaseCandidate.ArchiveInputs.Artifact.Id,
            ArchiveArtifactName = evidence.ReleaseCandidate.ArchiveInputs.Artifact.Name,
            ArchiveArtifactUploadDigest =
                evidence.ReleaseCandidate.ArchiveInputs.Artifact.UploadDigest,
            GhcrInputsDigest = evidence.ReleaseCandidate.GhcrInputs.ContentDigest,
            GhcrProducerRepository =
                evidence.ReleaseCandidate.GhcrInputs.Producer.Repository,
            GhcrProducerWorkflowPath =
                evidence.ReleaseCandidate.GhcrInputs.Producer.WorkflowPath,
            GhcrProducerRunId = evidence.ReleaseCandidate.GhcrInputs.Producer.RunId,
            GhcrProducerRunAttempt =
                evidence.ReleaseCandidate.GhcrInputs.Producer.RunAttempt,
            GhcrProducerSourceSha =
                evidence.ReleaseCandidate.GhcrInputs.Producer.SourceSha,
            GhcrProducerSourceRef =
                evidence.ReleaseCandidate.GhcrInputs.Producer.SourceRef,
            GhcrArtifactId = evidence.ReleaseCandidate.GhcrInputs.Artifact.Id,
            GhcrArtifactName = evidence.ReleaseCandidate.GhcrInputs.Artifact.Name,
            GhcrArtifactUploadDigest =
                evidence.ReleaseCandidate.GhcrInputs.Artifact.UploadDigest,
            GhcrIndexDigest = evidence.ReleaseCandidate.GhcrInputs.IndexDigest,
            CandidateSourceSha = candidate.SourceSha,
            CandidateIdentityDigest = candidate.IdentityDigest,
            CandidateRuntimeDigest = candidate.RuntimeDigest,
            PriorSourceSha = prior.SourceSha,
            PriorIdentityDigest = prior.IdentityDigest,
            PriorRuntimeDigest = prior.RuntimeDigest,
            ProfileId = evidence.Profile.Id,
            ProfileVersion = evidence.Profile.Version,
            WorkloadManifestDigest = evidence.Policy.WorkloadManifestDigest,
            QualificationPolicyDigest = evidence.Policy.QualificationPolicyDigest,
            ObservationPolicyDigest = evidence.Policy.ObservationPolicyDigest,
            AzureBackendKind = environment.BackendKind,
            AzureRegion = environment.Region,
            AzureBackendIdentityDigest = environment.BackendIdentityDigest,
            ConfigDigest = environment.ConfigDigest,
            AwsBindingDigest = environment.AwsBindingDigest,
            ProducerRepository = evidence.Producer.Repository,
            ProducerWorkflowPath = evidence.Producer.WorkflowPath,
            ProducerRunId = evidence.Producer.RunId,
            ProducerRunAttempt = evidence.Producer.RunAttempt,
            ProducerSourceSha = evidence.Producer.SourceSha,
            ProducerSourceRef = evidence.Producer.SourceRef,
            CaptureArtifactId = evidence.CaptureArtifact.Id,
            CaptureArtifactName = evidence.CaptureArtifact.Name,
            CaptureArtifactUploadDigest = evidence.CaptureArtifact.UploadDigest,
            MinimumWindowMinutes = evidence.Observation.MinimumWindowMinutes,
            MaximumEvidenceAge = TimeSpan.FromHours(4),
        };
        return Reseal(evidence, context);
    }

    private static RcObservationCohort Cohort(
        string id,
        string role,
        RcObservationRuntimeIdentity runtime,
        RcObservationAzureEnvironment environment,
        DateTimeOffset started,
        DateTimeOffset ended,
        string memberDigest) => new()
    {
        Id = id,
        Role = role,
        RuntimeIdentityDigest = runtime.IdentityDigest,
        RuntimeDigest = runtime.RuntimeDigest,
        BackendKind = environment.BackendKind,
        Region = environment.Region,
        BackendIdentityDigest = environment.BackendIdentityDigest,
        ConfigDigest = environment.ConfigDigest,
        AwsBindingDigest = environment.AwsBindingDigest,
        ObservedFromUtc = started,
        ObservedUntilUtc = ended,
        MemberDigests = [memberDigest],
        OperationDiagnostics =
        [
            OperationDiagnostic("s3", "CreateBucket", 0, 0),
            OperationDiagnostic("s3", "DeleteBucket", 0, 0),
            OperationDiagnostic("s3", "DeleteObject", 0, 0),
            OperationDiagnostic("s3", "GetObject", 199, 1),
            OperationDiagnostic("s3", "HeadObject", 0, 0),
            OperationDiagnostic("s3", "ListObjectsV2", 0, 0),
            OperationDiagnostic("s3", "PutObject", 800, 0),
        ],
    };

    private static RcObservationOperationDiagnostic OperationDiagnostic(
        string service,
        string operation,
        long completions,
        long failures) => new()
    {
        Service = service,
        Operation = operation,
        Completions = completions,
        Failures = failures,
        Throttles = 0,
        FirstFailure = failures == 0
            ? null
            : new RcObservationFirstFailure
            {
                Category = "aws_service",
                StatusCode = 503,
                ErrorCode = "ServiceUnavailable",
            },
    };

    private static RcObservationEvidence MakeRollback(
        RcObservationEvidence evidence,
        RcObservationValidationContext context)
    {
        var restorationStarted = evidence.Observation.StartedAtUtc.AddMinutes(60);
        var restorationVerified = evidence.Observation.StartedAtUtc.AddMinutes(70);
        return evidence with
        {
            Observation = evidence.Observation with
            {
                EndedAtUtc = restorationVerified,
                GeneratedAtUtc = restorationVerified.AddMinutes(10),
            },
            Cohorts =
            [
                evidence.Cohorts.Single(cohort => cohort.Role == "candidate") with
                {
                    ObservedUntilUtc = restorationStarted,
                    OperationDiagnostics =
                    [
                        OperationDiagnostic("s3", "CreateBucket", 0, 0),
                        OperationDiagnostic("s3", "DeleteBucket", 0, 0),
                        OperationDiagnostic("s3", "DeleteObject", 0, 0),
                        OperationDiagnostic("s3", "GetObject", 180, 20),
                        OperationDiagnostic("s3", "HeadObject", 0, 0),
                        OperationDiagnostic("s3", "ListObjectsV2", 0, 0),
                        OperationDiagnostic("s3", "PutObject", 800, 0),
                    ],
                },
                evidence.Cohorts.Single(cohort => cohort.Role == "stable") with
                {
                    ObservedUntilUtc = restorationVerified,
                },
            ],
            Metrics =
            [
                evidence.Metrics[0] with
                {
                    CandidateValue = 0.02,
                    Result = "breach",
                },
                evidence.Metrics[1],
            ],
            RollbackTriggers =
            [
                evidence.RollbackTriggers[0] with { Status = "fired" },
                evidence.RollbackTriggers[1],
            ],
            Decision = evidence.Decision with
            {
                Verdict = "rollback",
                Reason = "Candidate error-rate trigger fired.",
                DecidedAtUtc = restorationVerified.AddMinutes(5),
            },
            Restoration = new RcObservationRestoration
            {
                Verified = true,
                RuntimeIdentityDigest = context.PriorIdentityDigest,
                RuntimeDigest = context.PriorRuntimeDigest,
                BackendIdentityDigest = context.AzureBackendIdentityDigest,
                ConfigDigest = context.ConfigDigest,
                AwsBindingDigest = context.AwsBindingDigest,
                StartedAtUtc = restorationStarted,
                VerifiedAtUtc = restorationVerified,
            },
        };
    }

    private static RcObservationEvidence ShiftWindow(
        RcObservationEvidence evidence,
        TimeSpan shift)
    {
        return evidence with
        {
            Observation = evidence.Observation with
            {
                StartedAtUtc = evidence.Observation.StartedAtUtc + shift,
                EndedAtUtc = evidence.Observation.EndedAtUtc + shift,
                GeneratedAtUtc = evidence.Observation.GeneratedAtUtc + shift,
            },
            Decision = evidence.Decision with
            {
                DecidedAtUtc = evidence.Decision.DecidedAtUtc + shift,
            },
            Cohorts = evidence.Cohorts.Select(cohort => cohort with
            {
                ObservedFromUtc = cohort.ObservedFromUtc + shift,
                ObservedUntilUtc = cohort.ObservedUntilUtc + shift,
            }).ToArray(),
            Metrics = evidence.Metrics.Select(metric => metric with
            {
                CapturedAtUtc = metric.CapturedAtUtc + shift,
            }).ToArray(),
        };
    }

    private static (RcObservationEvidence Evidence, RcObservationValidationContext Context)
        Reseal(
        RcObservationEvidence evidence,
        RcObservationValidationContext context)
    {
        evidence = evidence with
        {
            EvidenceDigest = RcObservationIntegrity.ComputePayloadDigest(evidence),
        };
        context = context with { ExpectedEvidenceDigest = evidence.EvidenceDigest };
        return (evidence, context);
    }

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    private static string Fixture(string name) => Path.Combine(
        FindRepoRoot(),
        "tests",
        "Aws2Azure.UnitTests",
        "GapDocs",
        "Fixtures",
        name);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
