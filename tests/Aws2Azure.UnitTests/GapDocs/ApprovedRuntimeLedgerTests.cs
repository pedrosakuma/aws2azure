using Aws2Azure.GapDocs;
using YamlDotNet.Core;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class ApprovedRuntimeLedgerTests
{
    private static readonly DateTimeOffset ValidationTime =
        new(2026, 7, 18, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Repository_approved_records_are_valid_and_profile_owned()
    {
        var repoRoot = FindRepoRoot();
        var profiles = WorkloadGaManifestLoader.LoadAll(
            Path.Combine(repoRoot, "docs", "workloads"));
        var records = ApprovedRuntimeLedgerLoader.LoadAll(
            Path.Combine(repoRoot, "docs", "workloads", "approved-runtimes"));

        var errors = ApprovedRuntimeLedgerValidator.Validate(
            records,
            profiles,
            ValidationTime);

        Assert.Empty(errors);
        Assert.Equal(4, records.Count);
        var approved = records.Where(record => record.Status == "approved").ToArray();
        var bootstrap = records.Where(record => record.Status == "bootstrap").ToArray();
        // sqs-standard-messaging promoted to approved for issue #626: all four
        // repository profiles (S3, SecretsManager, DynamoDB, SQS) are now
        // approved with no remaining bootstrap-only record.
        Assert.Equal(4, approved.Length);
        Assert.Empty(bootstrap);
        Assert.All(approved, record =>
        {
            Assert.True(record.Eligibility.RollbackBaselineEligible);
            Assert.True(record.Eligibility.PromotionEligible);
            Assert.NotNull(record.Qualification);
        });
        var sqsApproved = Assert.Single(
            approved,
            record => record.Profile.Id == "sqs-standard-messaging");
        Assert.True(sqsApproved.Eligibility.RollbackBaselineEligible);
        Assert.True(sqsApproved.Eligibility.PromotionEligible);
        Assert.NotNull(sqsApproved.Qualification);
        Assert.Equal("qualified", sqsApproved.Qualification!.Verdict);
    }

    [Fact]
    public void Bootstrap_cannot_be_represented_as_promotable_or_qualified()
    {
        var record = ValidRecord();
        record.Eligibility.PromotionEligible = true;
        record.Qualification = ValidQualification(record);

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "bootstrap must not be promotion_eligible",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "bootstrap must not carry qualification evidence",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Approved_transition_requires_distinct_rollback_qualification()
    {
        var record = ValidRecord();
        record.Status = "approved";
        record.Eligibility.PromotionEligible = true;
        record.Qualification = ValidQualification(record);
        record.Qualification.RollbackTargetRuntimeDigest =
            record.Qualification.CandidateRuntimeDigest;

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "rollback target must be a distinct runtime",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Approved_without_qualification_is_rejected()
    {
        var record = ValidRecord();
        record.Status = "approved";
        record.Eligibility.PromotionEligible = true;

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "approved runtime must carry qualification evidence",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Approved_without_trusted_rollback_target_is_rejected()
    {
        var record = ValidRecord();
        record.Status = "approved";
        record.Eligibility.PromotionEligible = true;
        record.Qualification = ValidQualification(record);
        record.Qualification.RollbackTarget = null;

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "qualification.rollback_target missing",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Historical_rollback_target_expiry_does_not_expire_approved_ledger()
    {
        var record = ValidRecord();
        record.Status = "approved";
        record.Eligibility.PromotionEligible = true;
        record.Artifact.ExpiresAt = ValidationTime.AddDays(10);
        record.Qualification = ValidQualification(record);

        var errors = ApprovedRuntimeLedgerValidator.Validate(
            [record],
            [Profile("test-profile", 1)],
            ValidationTime.AddDays(2));

        Assert.Empty(errors);
    }

    [Fact]
    public void Null_nested_trusted_rollback_target_is_reported_as_validation_error()
    {
        var record = ValidRecord();
        record.Status = "approved";
        record.Eligibility.PromotionEligible = true;
        record.Qualification = ValidQualification(record);
        record.Qualification.RollbackTarget!.Profile = null!;

        var exception = Record.Exception(() => Validate(record));

        Assert.Null(exception);
        Assert.Contains(
            Validate(record),
            error => error.Contains(
                "qualification.rollback_target invalid",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Approved_requires_a_qualified_verdict()
    {
        var record = ValidRecord();
        record.Status = "approved";
        record.Eligibility.PromotionEligible = true;
        record.Qualification = ValidQualification(record);
        record.Qualification.Verdict = "candidate";

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "qualification.verdict must be 'qualified'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Revoked_transition_requires_reason_date_and_disables_eligibility()
    {
        var record = ValidRecord();
        record.Status = "revoked";
        record.Revocation = new ApprovedRuntimeRevocation();

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "revoked runtime must not be rollback or promotion eligible",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "revocation.reason missing",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "revocation.revoked_at missing",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 42, 1)]
    [InlineData("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 41, 1)]
    [InlineData("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 42, 2)]
    public void Artifact_name_must_match_runtime_run_and_attempt(
        string runtimeDigest,
        long runId,
        int attempt)
    {
        var record = ValidRecord();
        record.Runtime.AggregateDigest = runtimeDigest;
        record.Producer.RunId = runId;
        record.Producer.RunAttempt = attempt;
        record.Producer.RunUrl =
            $"https://github.com/example/repository/actions/runs/{runId}";

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "artifact.name",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Expired_ephemeral_artifact_is_rejected()
    {
        var record = ValidRecord();
        record.Artifact.ExpiresAt = ValidationTime.AddSeconds(-1);

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "artifact is expired",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Expired_artifact_with_durable_immutable_reference_is_valid()
    {
        var record = ValidRecord();
        record.Artifact.ExpiresAt = ValidationTime.AddSeconds(-1);
        record.Artifact.DurableReference = new ApprovedRuntimeDurableReference
        {
            Uri = "oci://ghcr.io/example/aws2azure@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            Digest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        };

        Assert.Empty(Validate(record));
    }

    [Fact]
    public void Unknown_profile_and_profile_version_drift_are_rejected()
    {
        var record = ValidRecord();
        record.Profile.Id = "unknown";

        var unknownErrors = Validate(record);

        Assert.Contains(unknownErrors, error => error.Contains(
            "unknown profile 'unknown'",
            StringComparison.Ordinal));

        record.Profile.Id = "test-profile";
        record.Profile.Version = 2;
        var driftErrors = Validate(record);
        Assert.Contains(driftErrors, error => error.Contains(
            "profile version drift",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_digest_is_rejected()
    {
        var record = ValidRecord();
        record.Runtime.ExecutableDigest = "sha256:not-a-digest";

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "runtime.executable_digest must use sha256",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Attestation_subject_name_must_match_the_executable_dsse_subject()
    {
        var record = ValidRecord();
        record.Attestation.SubjectName = "runtime/Aws2Azure.Proxy";

        var errors = Validate(record);

        Assert.Contains(errors, error => error.Contains(
            "attestation.subject_name must be 'Aws2Azure.Proxy'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_producer_identity_must_resolve_to_one_runtime()
    {
        var first = ValidRecord();
        var second = ValidRecord();
        second.Profile.Id = "second-profile";
        second.Runtime.AggregateDigest =
            "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

        var errors = ApprovedRuntimeLedgerValidator.Validate(
            [first, second],
            [
                Profile("test-profile", 1),
                Profile("second-profile", 1),
            ],
            ValidationTime);

        Assert.Contains(errors, error => error.Contains(
            "conflicting artifact or runtime identities",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Loader_rejects_unknown_fields()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "tests",
            "Aws2Azure.UnitTests",
            "GapDocs",
            "Fixtures",
            "approved-runtime-unknown-field.yaml");

        Assert.Throws<YamlException>(() => ApprovedRuntimeLedgerLoader.Load(path));
    }

    private static IReadOnlyList<string> Validate(ApprovedRuntimeRecord record) =>
        ApprovedRuntimeLedgerValidator.Validate(
            [record],
            [Profile("test-profile", 1)],
            ValidationTime);

    private static WorkloadGaManifest Profile(string id, int version) => new()
    {
        Id = id,
        Version = version,
    };

    private static ApprovedRuntimeRecord ValidRecord() => new()
    {
        SchemaVersion = 1,
        Profile = new ApprovedRuntimeProfile
        {
            Id = "test-profile",
            Version = 1,
        },
        Status = "bootstrap",
        Eligibility = new ApprovedRuntimeEligibility
        {
            RollbackBaselineEligible = true,
            PromotionEligible = false,
        },
        Runtime = new ApprovedRuntimeIdentity
        {
            Target = new ApprovedRuntimeTarget
            {
                OperatingSystem = "linux",
                Architecture = "x64",
                Rid = "linux-x64",
            },
            SourceRepository = "example/repository",
            SourceSha = "1234567890123456789012345678901234567890",
            AggregateDigest =
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ExecutableDigest =
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        },
        Producer = new ApprovedRuntimeProducer
        {
            Workflow = ".github/workflows/sealed-runtime.yml",
            RunId = 42,
            RunAttempt = 1,
            RunUrl = "https://github.com/example/repository/actions/runs/42",
        },
        Artifact = new ApprovedRuntimeArtifact
        {
            Id = 7,
            Name =
                "aws2azure-sealed-linux-x64-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-run-42-attempt-1",
            UploadDigest =
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            CreatedAt = ValidationTime.AddDays(-1),
            ExpiresAt = ValidationTime.AddDays(1),
        },
        Attestation = new ApprovedRuntimeAttestation
        {
            PredicateType = "https://slsa.dev/provenance/v1",
            Repository = "example/repository",
            SignerWorkflow = "example/repository/.github/workflows/sealed-runtime.yml",
            SourceSha = "1234567890123456789012345678901234567890",
            SourceRef = "refs/heads/main",
            SubjectName = "Aws2Azure.Proxy",
            SubjectDigest =
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ManifestSubjectName = "sealed-runtime-manifest.json",
            ManifestSubjectDigest =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        },
        Approval = new ApprovedRuntimeApproval
        {
            Reason = "First sealed runtime has no predecessor.",
            ReviewUrl = "https://github.com/example/repository/issues/1",
            ReviewedAt = ValidationTime,
            ReviewedBy = "reviewer",
        },
    };

    private static ApprovedRuntimeQualification ValidQualification(
        ApprovedRuntimeRecord record) => new()
    {
        Artifact = "docs/workloads/evidence/qualification.yaml",
        Digest = "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
        Verdict = "qualified",
        CandidateRuntimeDigest = record.Runtime.AggregateDigest,
        RollbackTargetRuntimeDigest =
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
        RollbackTarget = ValidRollbackTarget(record),
        ReviewUrl = "https://github.com/example/repository/issues/1",
        QualifiedAt = ValidationTime,
    };

    private static QualificationSealedRuntimeIdentity ValidRollbackTarget(
        ApprovedRuntimeRecord record) => new()
    {
        SchemaVersion = 1,
        Role = "prior",
        Profile = new QualificationSealedRuntimeProfile
        {
            Id = record.Profile.Id,
            Version = record.Profile.Version,
        },
        Status = "bootstrap",
        Eligibility = new QualificationSealedRuntimeEligibility
        {
            RollbackBaselineEligible = true,
            PromotionEligible = false,
        },
        LedgerRecordDigest =
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
        Source = new QualificationSealedRuntimeSource
        {
            Repository = "example/repository",
            Sha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
            Ref = "refs/heads/main",
        },
        Runtime = new QualificationSealedRuntimeDigests
        {
            AggregateDigest =
                "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            ExecutableDigest =
                "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            ManifestDigest =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        },
        Producer = new QualificationSealedRuntimeProducer
        {
            Workflow = ".github/workflows/sealed-runtime.yml",
            EventName = "workflow_dispatch",
            RunId = 41,
            RunAttempt = 1,
            RunUrl = "https://github.com/example/repository/actions/runs/41",
            AttemptUrl = "https://github.com/example/repository/actions/runs/41/attempts/1",
            RunStartedAt = ValidationTime.AddDays(-2),
        },
        Artifact = new QualificationSealedRuntimeArtifact
        {
            Id = 6,
            Name =
                "aws2azure-sealed-linux-x64-ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff-run-41-attempt-1",
            UploadDigest =
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            CreatedAt = ValidationTime.AddDays(-2),
            ExpiresAt = ValidationTime.AddDays(1),
        },
        Attestation = new QualificationSealedRuntimeAttestation
        {
            PredicateType = "https://slsa.dev/provenance/v1",
            Repository = "example/repository",
            SignerWorkflow = "example/repository/.github/workflows/sealed-runtime.yml",
            SourceSha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
            SourceRef = "refs/heads/main",
            RunInvocationUrl =
                "https://github.com/example/repository/actions/runs/41/attempts/1",
            BundleDigest =
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ExecutableSubjectName = "Aws2Azure.Proxy",
            ExecutableSubjectDigest =
                "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            ManifestSubjectName = "sealed-runtime-manifest.json",
            ManifestSubjectDigest =
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        },
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
