using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed class RcObservationCaptureEvidence
{
    public int SchemaVersion { get; set; } = 1;
    public RcObservationCaptureProfile Profile { get; set; } = new();
    public RcObservationCaptureAzure Azure { get; set; } = new();
    public RcObservationCaptureWindow Observation { get; set; } = new();
    public RcObservationCaptureLoadShape LoadShape { get; set; } = new();
    public List<RcObservationCaptureCohort> Cohorts { get; set; } = [];
    public List<RcObservationCaptureMetric> Metrics { get; set; } = [];
    public RcObservationCaptureRestoration Restoration { get; set; } = new();
}

internal sealed class RcObservationCaptureLoadShape
{
    public int CandidateConcurrency { get; set; }
    public int StableConcurrency { get; set; }
    public string OperationMixIdentity { get; set; } = string.Empty;
}

internal sealed class RcObservationCaptureProfile
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
}

internal sealed class RcObservationCaptureAzure
{
    public string BackendKind { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string BackendIdentityDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
    public string AwsBindingDigest { get; set; } = string.Empty;
}

internal sealed class RcObservationCaptureWindow
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset MeasurementEndedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public int RequestedWindowMinutes { get; set; }
}

internal sealed class RcObservationCaptureCohort
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string RuntimeIdentityDigest { get; set; } = string.Empty;
    public string RuntimeDigest { get; set; } = string.Empty;
    public string BackendKind { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string BackendIdentityDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
    public string AwsBindingDigest { get; set; } = string.Empty;
    public DateTimeOffset ObservedFromUtc { get; set; }
    public DateTimeOffset ObservedUntilUtc { get; set; }
    public List<string> MemberDigests { get; set; } = [];
    public List<RcObservationOperationDiagnostic> OperationDiagnostics { get; set; } = [];
}

internal sealed class RcObservationOperationDiagnostic
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public long Completions { get; set; }
    public long Failures { get; set; }
    public long Throttles { get; set; }
    public RcObservationFirstFailure? FirstFailure { get; set; }
}

internal sealed class RcObservationFirstFailure
{
    public string Category { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
}

internal sealed class RcObservationCaptureMetric
{
    public string Id { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double CandidateValue { get; set; }
    public double StableValue { get; set; }
    public long CandidateSamples { get; set; }
    public long StableSamples { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

internal sealed class RcObservationCaptureRestoration
{
    public bool Verified { get; set; }
    public string RuntimeIdentityDigest { get; set; } = string.Empty;
    public string RuntimeDigest { get; set; } = string.Empty;
    public string BackendIdentityDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
    public string AwsBindingDigest { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset VerifiedAtUtc { get; set; }
}

internal sealed class RcCalibrationReport
{
    public int SchemaVersion { get; set; } = 1;
    public string ArtifactKind { get; set; } = "rc_observation_calibration";
    public bool Promotable { get; set; }
    public RcObservationCaptureProfile Profile { get; set; } = new();
    public RcCalibrationReleaseCandidate ReleaseCandidate { get; set; } = new();
    public RcObservationCaptureCohortIdentity Candidate { get; set; } = new();
    public RcObservationCaptureCohortIdentity Prior { get; set; } = new();
    public RcObservationCaptureAzure Azure { get; set; } = new();
    public RcCalibrationWindow Calibration { get; set; } = new();
    public RcCalibrationConcurrency PerCohortConcurrency { get; set; } = new();
    public int TotalConcurrency { get; set; }
    public string OperationMixIdentity { get; set; } = string.Empty;
    public List<RcCalibrationCohort> Cohorts { get; set; } = [];
    public RcObservationCaptureRestoration Restoration { get; set; } = new();
}

internal sealed class RcCalibrationReleaseCandidate
{
    public string Id { get; set; } = string.Empty;
    public string ManifestDigest { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string ArchiveInputsDigest { get; set; } = string.Empty;
    public string GhcrInputsDigest { get; set; } = string.Empty;
}

internal sealed class RcObservationCaptureCohortIdentity
{
    public string RuntimeIdentityDigest { get; set; } = string.Empty;
    public string RuntimeDigest { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
}

internal sealed class RcCalibrationWindow
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset MeasurementEndedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public int RequestedDurationMinutes { get; set; }
}

internal sealed class RcCalibrationConcurrency
{
    public int Candidate { get; set; }
    public int Stable { get; set; }
}

internal sealed class RcCalibrationCohort
{
    public string Role { get; set; } = string.Empty;
    public int Concurrency { get; set; }
    public double GetSecretValueThroughputPerSecond { get; set; }
    public long GetSecretValueSamples { get; set; }
    public List<RcObservationOperationDiagnostic> OperationDiagnostics { get; set; } = [];
}

internal static class RcObservationCaptureWriter
{
    public static int ReadWindowMinutes()
    {
        var value = Environment.GetEnvironmentVariable(
            "AWS2AZURE_RC_OBSERVATION_WINDOW_MINUTES");
        return int.TryParse(value, out var minutes) && minutes is >= 60 and <= 180
            ? minutes
            : throw new InvalidDataException(
                "AWS2AZURE_RC_OBSERVATION_WINDOW_MINUTES must be between 60 and 180.");
    }

    public static int ReadConcurrency(string role)
    {
        var value = Environment.GetEnvironmentVariable(
            $"AWS2AZURE_RC_OBSERVATION_{role.ToUpperInvariant()}_CONCURRENCY");
        return int.TryParse(value, out var concurrency) && concurrency is >= 1 and <= 32
            ? concurrency
            : throw new InvalidDataException(
                $"AWS2AZURE_RC_OBSERVATION_{role.ToUpperInvariant()}_CONCURRENCY " +
                "must be between 1 and 32.");
    }

    public static int ReadCalibrationDurationMinutes()
    {
        var value = Environment.GetEnvironmentVariable(
            "AWS2AZURE_RC_CALIBRATION_DURATION_MINUTES");
        return int.TryParse(value, out var minutes) && minutes is >= 5 and <= 20
            ? minutes
            : throw new InvalidDataException(
                "AWS2AZURE_RC_CALIBRATION_DURATION_MINUTES must be between 5 and 20.");
    }

    public static int ReadCalibrationConcurrency(string cohort)
    {
        var name = cohort switch
        {
            "candidate" => "AWS2AZURE_RC_CALIBRATION_CANDIDATE_CONCURRENCY",
            "stable" => "AWS2AZURE_RC_CALIBRATION_STABLE_CONCURRENCY",
            _ => throw new ArgumentOutOfRangeException(nameof(cohort)),
        };
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var concurrency) && concurrency is >= 1 and <= 32
            ? concurrency
            : throw new InvalidDataException($"{name} must be between 1 and 32.");
    }

    public static string OperationMixIdentity(
        string profile,
        IReadOnlyList<string> operationSchedule) =>
        Digest(profile + "\n" + string.Join("\n", operationSchedule));

    public static string MemberDigest(
        string profile,
        string role,
        int worker,
        string endpoint)
    {
        var runId = RequiredEnvironment("GITHUB_RUN_ID");
        var attempt = RequiredEnvironment("GITHUB_RUN_ATTEMPT");
        return Digest(
            $"{profile}\n{role}\n{runId}\n{attempt}\n{worker}\n{endpoint}");
    }

    public static string CohortId(string role) =>
        $"{role}-{RequiredEnvironment("GITHUB_RUN_ID")}-" +
        RequiredEnvironment("GITHUB_RUN_ATTEMPT");

    public static async Task PublishAsync(RcObservationCaptureEvidence evidence)
    {
        if (evidence.Metrics.Count == 0
            || evidence.LoadShape.CandidateConcurrency <= 0
            || evidence.LoadShape.StableConcurrency <= 0
            || string.IsNullOrWhiteSpace(evidence.LoadShape.OperationMixIdentity)
            || evidence.Metrics.Any(metric =>
                metric.CandidateSamples <= 0 || metric.StableSamples <= 0)
            || evidence.Cohorts.Any(cohort => cohort.OperationDiagnostics.Count == 0)
            || !evidence.Restoration.Verified)
        {
            throw new InvalidDataException(
                "RC observation cannot publish incomplete cohort or restoration evidence.");
        }

        var configured = RequiredEnvironment("AWS2AZURE_RC_OBSERVATION_CAPTURE_PATH");
        var fullPath = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(FindRepoRoot(), configured));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var pending = fullPath + ".pending";
        File.Delete(pending);
        try
        {
            await File.WriteAllTextAsync(
                pending,
                JsonSerializer.Serialize(
                    evidence,
                    RcObservationCaptureJsonContext.Default
                        .RcObservationCaptureEvidence)).ConfigureAwait(false);
            File.Move(pending, fullPath, true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    public static async Task PublishCalibrationAsync(RcCalibrationReport report)
    {
        if (report.ArtifactKind != "rc_observation_calibration"
            || report.Promotable
            || report.Profile.Id != "secretsmanager-basic-lifecycle"
            || report.Calibration.RequestedDurationMinutes is < 5 or > 20
            || report.PerCohortConcurrency.Candidate <= 0
            || report.PerCohortConcurrency.Stable <= 0
            || report.TotalConcurrency != report.PerCohortConcurrency.Candidate
                + report.PerCohortConcurrency.Stable
            || report.OperationMixIdentity.Length == 0
            || report.Cohorts.Count != 2
            || report.Cohorts.Any(cohort => cohort.OperationDiagnostics.Count == 0)
            || !report.Restoration.Verified)
        {
            throw new InvalidDataException(
                "RC calibration report must be complete, sanitized, and non-promotable.");
        }

        var configured = RequiredEnvironment("AWS2AZURE_RC_CALIBRATION_REPORT_PATH");
        var fullPath = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(FindRepoRoot(), configured));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var pending = fullPath + ".pending";
        File.Delete(pending);
        try
        {
            await File.WriteAllTextAsync(
                pending,
                JsonSerializer.Serialize(
                    report,
                    RcObservationCaptureJsonContext.Default.RcCalibrationReport))
                .ConfigureAwait(false);
            File.Move(pending, fullPath, true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    public static double FailureRate(RealAzureWorkloadLoadTracker tracker)
    {
        var snapshot = tracker.Snapshot();
        var completions = snapshot.Sum(item => item.Completions);
        var failures = snapshot.Sum(item => item.Failures);
        var attempts = completions + failures;
        return attempts == 0 ? 1 : (double)failures / attempts;
    }

    public static long TotalAttempts(RealAzureWorkloadLoadTracker tracker) =>
        tracker.Snapshot().Sum(item => item.Completions + item.Failures);

    public static List<RcObservationOperationDiagnostic> OperationDiagnostics(
        RealAzureWorkloadLoadTracker tracker)
    {
        return tracker.Snapshot().Select(item =>
        {
            var firstFailure = tracker.FirstFailureDetail(item.Operation);
            return new RcObservationOperationDiagnostic
            {
                Service = item.Service,
                Operation = item.Operation,
                Completions = item.Completions,
                Failures = item.Failures,
                Throttles = tracker.Throttles(item.Operation),
                FirstFailure = firstFailure is null
                    ? null
                    : new RcObservationFirstFailure
                    {
                        Category = firstFailure.Category,
                        StatusCode = firstFailure.StatusCode,
                        ErrorCode = firstFailure.ErrorCode,
                    },
            };
        }).ToList();
    }

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"{name} is required.")
            : value;
    }

    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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

[JsonSerializable(typeof(RcObservationCaptureEvidence))]
[JsonSerializable(typeof(RcCalibrationReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class RcObservationCaptureJsonContext : JsonSerializerContext;
