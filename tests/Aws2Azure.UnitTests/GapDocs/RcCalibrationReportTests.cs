using System.Text.Json;
using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RcCalibrationReportTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 7, 19, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Valid_non_promotable_secrets_calibration_report_is_accepted()
    {
        var report = ValidReport();

        Assert.Empty(RcCalibrationReportValidator.Validate(report));
    }

    [Fact]
    public void Calibration_cannot_be_marked_promotable_or_observation_kind()
    {
        var report = ValidReport();
        report.Promotable = true;
        report.ArtifactKind = "rc_observation";

        var errors = RcCalibrationReportValidator.Validate(report);

        Assert.Contains(errors, error => error.Contains(
            "artifact_kind must be 'rc_observation_calibration'",
            StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(
            "promotable: false",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Calibration_json_rejects_observation_evidence_fields()
    {
        var directory = Path.Combine(
            FindRepoRoot(),
            "artifacts",
            "unit-rc-calibration-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "calibration-report.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                {
                  "schema_version": 1,
                  "artifact_kind": "rc_observation_calibration",
                  "promotable": false,
                  "evidence_digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                }
                """);

            var exception = Assert.Throws<JsonException>(() =>
                RcCalibrationReportValidator.Load(path));

            Assert.Contains("evidence_digest", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Calibration_must_explicitly_declare_non_promotable()
    {
        var report = ValidReport();
        report.Promotable = null;

        var errors = RcCalibrationReportValidator.Validate(report);

        Assert.Contains(errors, error => error.Contains(
            "promotable: false",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Observation_validator_rejects_calibration_artifact_kind()
    {
        var (evidence, context) = RcObservationTests.ValidEvidenceForCalibrationGuard();
        evidence = evidence with { ArtifactKind = "rc_observation_calibration" };
        evidence = evidence with
        {
            EvidenceDigest = RcObservationIntegrity.ComputePayloadDigest(evidence),
        };

        var errors = RcObservationValidator.Validate(
            evidence,
            context,
            Started.AddHours(1));

        Assert.Contains(errors, error => error.Contains(
            "artifact_kind must be 'rc_observation'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_requires_exact_ordered_secrets_lifecycle_operations()
    {
        var omitted = ValidReport();
        omitted.Cohorts[0].OperationDiagnostics.RemoveAt(0);
        Assert.Contains(RcCalibrationReportValidator.Validate(omitted), error =>
            error.Contains("exact Secrets lifecycle operations", StringComparison.Ordinal));

        var extra = ValidReport();
        extra.Cohorts[0].OperationDiagnostics.Add(Diagnostic("TagResource", 1));
        Assert.Contains(RcCalibrationReportValidator.Validate(extra), error =>
            error.Contains("exact Secrets lifecycle operations", StringComparison.Ordinal));

        var drift = ValidReport();
        drift.Cohorts[0].OperationDiagnostics[0] = Diagnostic("CreateSecret", 1) with
        {
            Service = "s3",
        };
        Assert.Contains(RcCalibrationReportValidator.Validate(drift), error =>
            error.Contains("malformed operation diagnostics", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_recomputes_operation_mix_identity()
    {
        var report = ValidReport();
        report.OperationMixIdentity = Digest('f');

        var errors = RcCalibrationReportValidator.Validate(report);

        Assert.Contains(errors, error => error.Contains(
            "operation_mix_identity does not match",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Candidate_runtime_must_match_release_candidate_source()
    {
        var report = ValidReport();
        report.Candidate.SourceSha = new string('9', 40);

        Assert.Contains(RcCalibrationReportValidator.Validate(report), error =>
            error.Contains(
                "candidate runtime source must match",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Operation_mix_identity_binds_lifecycle_order_and_multiplicity()
    {
        var setOnlyIdentity = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    "secretsmanager-basic-lifecycle\n" +
                    "CreateSecret\nDeleteSecret\nDescribeSecret\nGetSecretValue\n" +
                    "ListSecrets\nPutSecretValue\nUpdateSecret")));
        var report = ValidReport();
        report.OperationMixIdentity = setOnlyIdentity;

        Assert.Contains(RcCalibrationReportValidator.Validate(report), error =>
            error.Contains("operation_mix_identity does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_reconciles_get_samples_and_throughput()
    {
        var samples = ValidReport();
        samples.Cohorts[0].GetSecretValueSamples++;
        Assert.Contains(RcCalibrationReportValidator.Validate(samples), error =>
            error.Contains("GetSecretValue samples", StringComparison.Ordinal));

        var throughput = ValidReport();
        throughput.Cohorts[0].GetSecretValueThroughputPerSecond += 0.01;
        Assert.Contains(RcCalibrationReportValidator.Validate(throughput), error =>
            error.Contains("GetSecretValue throughput", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_requires_complete_non_future_measurement_window()
    {
        var shortWindow = ValidReport();
        shortWindow.Calibration.MeasurementEndedAtUtc =
            shortWindow.Calibration.StartedAtUtc.AddMinutes(9).AddSeconds(59);
        Assert.Contains(RcCalibrationReportValidator.Validate(shortWindow), error =>
            error.Contains("requested calibration duration", StringComparison.Ordinal));

        var defaultTimestamp = ValidReport();
        defaultTimestamp.Calibration.StartedAtUtc = default;
        Assert.Contains(RcCalibrationReportValidator.Validate(defaultTimestamp), error =>
            error.Contains("ordered and non-default", StringComparison.Ordinal));

        var future = ValidReport();
        future.Calibration.StartedAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        future.Calibration.MeasurementEndedAtUtc = future.Calibration.StartedAtUtc
            .AddMinutes(10);
        future.Calibration.EndedAtUtc = future.Calibration.MeasurementEndedAtUtc
            .AddMinutes(2);
        Assert.Contains(RcCalibrationReportValidator.Validate(future), error =>
            error.Contains("must not be in the future", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_enforces_first_failure_and_throttle_sanitization()
    {
        var missingFailure = ValidReport();
        var create = missingFailure.Cohorts[0].OperationDiagnostics[0];
        missingFailure.Cohorts[0].OperationDiagnostics[0] = create with
        {
            Failures = 1,
            FirstFailure = null,
        };
        Assert.Contains(RcCalibrationReportValidator.Validate(missingFailure), error =>
            error.Contains("first failure must match", StringComparison.Ordinal));

        var unexpectedFailure = ValidReport();
        var describe = unexpectedFailure.Cohorts[0].OperationDiagnostics[1];
        unexpectedFailure.Cohorts[0].OperationDiagnostics[1] = describe with
        {
            FirstFailure = new RcObservationFirstFailure
            {
                Category = "exception",
                ErrorCode = "InvalidDataException",
            },
        };
        Assert.Contains(RcCalibrationReportValidator.Validate(unexpectedFailure), error =>
            error.Contains("first failure must match", StringComparison.Ordinal));

        var badStatus = ValidReport();
        var put = badStatus.Cohorts[0].OperationDiagnostics[5];
        badStatus.Cohorts[0].OperationDiagnostics[5] = put with
        {
            FirstFailure = put.FirstFailure! with { StatusCode = 99 },
        };
        Assert.Contains(RcCalibrationReportValidator.Validate(badStatus), error =>
            error.Contains("unsanitized first failure", StringComparison.Ordinal));

        var badToken = ValidReport();
        put = badToken.Cohorts[0].OperationDiagnostics[5];
        badToken.Cohorts[0].OperationDiagnostics[5] = put with
        {
            FirstFailure = put.FirstFailure! with { ErrorCode = new string('x', 129) },
        };
        Assert.Contains(RcCalibrationReportValidator.Validate(badToken), error =>
            error.Contains("unsanitized first failure", StringComparison.Ordinal));

        var throttleDrift = ValidReport();
        put = throttleDrift.Cohorts[0].OperationDiagnostics[5];
        throttleDrift.Cohorts[0].OperationDiagnostics[5] = put with
        {
            Throttles = put.Failures + 1,
        };
        Assert.Contains(RcCalibrationReportValidator.Validate(throttleDrift), error =>
            error.Contains("malformed operation diagnostics", StringComparison.Ordinal));
    }

    [Fact]
    public void Restoration_must_finish_within_the_calibration_timeline()
    {
        var report = ValidReport();
        report.Restoration = report.Restoration with
        {
            VerifiedAtUtc = report.Calibration.EndedAtUtc.AddSeconds(1),
        };

        Assert.Contains(RcCalibrationReportValidator.Validate(report), error =>
            error.Contains(
                "verify exact-prior restoration",
                StringComparison.Ordinal));
    }

    private static RcCalibrationReport ValidReport()
    {
        var runtimeA = Digest('a');
        var runtimeB = Digest('b');
        var measurementSeconds = 600d;
        return new RcCalibrationReport
        {
            SchemaVersion = 1,
            ArtifactKind = "rc_observation_calibration",
            Promotable = false,
            Profile = new RcObservationCaptureProfile
            {
                Id = "secretsmanager-basic-lifecycle",
                Version = 1,
            },
            ReleaseCandidate = new RcCalibrationReleaseCandidate
            {
                Id = "v1.0.0-rc.1",
                ManifestDigest = Digest('1'),
                SourceSha = new string('1', 40),
                ArchiveInputsDigest = Digest('2'),
                GhcrInputsDigest = Digest('3'),
            },
            Candidate = new RcCalibrationRuntimeIdentity
            {
                RuntimeIdentityDigest = Digest('4'),
                RuntimeDigest = runtimeA,
                SourceSha = new string('1', 40),
            },
            Prior = new RcCalibrationRuntimeIdentity
            {
                RuntimeIdentityDigest = Digest('5'),
                RuntimeDigest = runtimeB,
                SourceSha = new string('5', 40),
            },
            Azure = new RcObservationAzureEnvironment
            {
                BackendKind = "keyVault",
                Region = "eastus2",
                BackendIdentityDigest = Digest('6'),
                ConfigDigest = Digest('7'),
                AwsBindingDigest = Digest('8'),
            },
            Calibration = new RcCalibrationWindow
            {
                StartedAtUtc = Started,
                MeasurementEndedAtUtc = Started.AddMinutes(10),
                EndedAtUtc = Started.AddMinutes(12),
                RequestedDurationMinutes = 10,
            },
            PerCohortConcurrency = new RcCalibrationConcurrency
            {
                Candidate = 6,
                Stable = 5,
            },
            TotalConcurrency = 11,
            OperationMixIdentity = RcCalibrationReportValidator.ExpectedOperationMixIdentity,
            Cohorts =
            [
                Cohort("candidate", 6, measurementSeconds),
                Cohort("stable", 5, measurementSeconds),
            ],
            Restoration = new RcObservationRestoration
            {
                Verified = true,
                RuntimeIdentityDigest = Digest('5'),
                RuntimeDigest = runtimeB,
                BackendIdentityDigest = Digest('6'),
                ConfigDigest = Digest('7'),
                AwsBindingDigest = Digest('8'),
                StartedAtUtc = Started.AddMinutes(10),
                VerifiedAtUtc = Started.AddMinutes(12),
            },
        };
    }

    private static RcCalibrationCohort Cohort(
        string role,
        int concurrency,
        double measurementSeconds) => new()
    {
        Role = role,
        Concurrency = concurrency,
        GetSecretValueThroughputPerSecond = 100 / measurementSeconds,
        GetSecretValueSamples = 100,
        OperationDiagnostics =
        [
            Diagnostic("CreateSecret", completions: 10),
            Diagnostic("DeleteSecret", completions: 10),
            Diagnostic("DescribeSecret", completions: 10),
            Diagnostic("GetSecretValue", completions: 100),
            Diagnostic("ListSecrets", completions: 10),
            Diagnostic("PutSecretValue", completions: 10, failures: 1, throttles: 1),
            Diagnostic("UpdateSecret", completions: 10),
        ],
    };

    private static RcObservationOperationDiagnostic Diagnostic(
        string operation,
        long completions,
        long failures = 0,
        long throttles = 0) => new()
    {
        Service = "secretsmanager",
        Operation = operation,
        Completions = completions,
        Failures = failures,
        Throttles = throttles,
        FirstFailure = failures == 0
            ? null
            : new RcObservationFirstFailure
            {
                Category = "throttle",
                StatusCode = 429,
                ErrorCode = "ThrottlingException",
            },
    };

    private static string Digest(char c) => "sha256:" + new string(c, 64);

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
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
