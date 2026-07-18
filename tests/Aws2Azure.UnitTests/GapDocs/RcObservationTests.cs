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
    }

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
            SchemaVersion = 1,
            ArtifactKind = "rc_observation",
            ReleaseCandidate = new RcObservationReleaseCandidate
            {
                Id = "v1.0.0-rc.1",
                ManifestDigest = Digest('2'),
                SourceSha = candidate.SourceSha,
            },
            Candidate = candidate,
            Prior = prior,
            Profile = new RcObservationProfile
            {
                Id = "s3-basic-object-crud",
                Version = 1,
            },
            Azure = environment,
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
                    Id = "error_rate",
                    Unit = "ratio",
                    Comparison = "less_than_or_equal",
                    Threshold = 0.01,
                    CandidateValue = 0.001,
                    StableValue = 0.001,
                    Samples = 1000,
                    CapturedAtUtc = started.AddMinutes(30),
                    Result = "pass",
                },
            ],
            RollbackTriggers =
            [
                new RcObservationRollbackTrigger
                {
                    Id = "candidate-error-rate",
                    MetricId = "error_rate",
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
            CandidateSourceSha = candidate.SourceSha,
            CandidateIdentityDigest = candidate.IdentityDigest,
            CandidateRuntimeDigest = candidate.RuntimeDigest,
            PriorSourceSha = prior.SourceSha,
            PriorIdentityDigest = prior.IdentityDigest,
            PriorRuntimeDigest = prior.RuntimeDigest,
            ProfileId = evidence.Profile.Id,
            ProfileVersion = evidence.Profile.Version,
            AzureBackendKind = environment.BackendKind,
            AzureRegion = environment.Region,
            AzureBackendIdentityDigest = environment.BackendIdentityDigest,
            ConfigDigest = environment.ConfigDigest,
            AwsBindingDigest = environment.AwsBindingDigest,
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
    };

    private static RcObservationEvidence MakeRollback(
        RcObservationEvidence evidence,
        RcObservationValidationContext context)
    {
        return evidence with
        {
            Metrics =
            [
                evidence.Metrics[0] with
                {
                    CandidateValue = 0.02,
                    Result = "breach",
                },
            ],
            RollbackTriggers =
            [
                evidence.RollbackTriggers[0] with { Status = "fired" },
            ],
            Decision = evidence.Decision with
            {
                Verdict = "rollback",
                Reason = "Candidate error-rate trigger fired.",
            },
            Restoration = new RcObservationRestoration
            {
                Verified = true,
                RuntimeIdentityDigest = context.PriorIdentityDigest,
                RuntimeDigest = context.PriorRuntimeDigest,
                BackendIdentityDigest = context.AzureBackendIdentityDigest,
                ConfigDigest = context.ConfigDigest,
                AwsBindingDigest = context.AwsBindingDigest,
                StartedAtUtc = evidence.Observation.StartedAtUtc.AddMinutes(40),
                VerifiedAtUtc = evidence.Observation.StartedAtUtc.AddMinutes(50),
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
