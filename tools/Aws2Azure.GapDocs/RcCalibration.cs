using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aws2Azure.GapDocs;

public sealed class RcCalibrationReport
{
    public int SchemaVersion { get; set; }
    public string ArtifactKind { get; set; } = string.Empty;
    public bool? Promotable { get; set; }
    public RcObservationCaptureProfile Profile { get; set; } = new();
    public RcCalibrationReleaseCandidate ReleaseCandidate { get; set; } = new();
    public RcCalibrationRuntimeIdentity Candidate { get; set; } = new();
    public RcCalibrationRuntimeIdentity Prior { get; set; } = new();
    public RcObservationAzureEnvironment Azure { get; set; } = new();
    public RcCalibrationWindow Calibration { get; set; } = new();
    public RcCalibrationConcurrency PerCohortConcurrency { get; set; } = new();
    public int TotalConcurrency { get; set; }
    public string OperationMixIdentity { get; set; } = string.Empty;
    public List<RcCalibrationCohort> Cohorts { get; set; } = [];
    public RcObservationRestoration Restoration { get; set; } = new();
}

public sealed class RcCalibrationReleaseCandidate
{
    public string Id { get; set; } = string.Empty;
    public string ManifestDigest { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string ArchiveInputsDigest { get; set; } = string.Empty;
    public string GhcrInputsDigest { get; set; } = string.Empty;
}

public sealed class RcCalibrationRuntimeIdentity
{
    public string RuntimeIdentityDigest { get; set; } = string.Empty;
    public string RuntimeDigest { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
}

public sealed class RcCalibrationWindow
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset MeasurementEndedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public int RequestedDurationMinutes { get; set; }
}

public sealed class RcCalibrationConcurrency
{
    public int Candidate { get; set; }
    public int Stable { get; set; }
}

public sealed class RcCalibrationCohort
{
    public string Role { get; set; } = string.Empty;
    public int Concurrency { get; set; }
    public double GetSecretValueThroughputPerSecond { get; set; }
    public long GetSecretValueSamples { get; set; }
    public List<RcObservationOperationDiagnostic> OperationDiagnostics { get; set; } = [];
}

public static partial class RcCalibrationReportValidator
{
    private const string SecretsManagerLifecycleProfile = "secretsmanager-basic-lifecycle";
    private const double ThroughputTolerance = 0.000001d;
    private static readonly string[] SecretsManagerLifecycleOperations =
    [
        "CreateSecret",
        "DeleteSecret",
        "DescribeSecret",
        "GetSecretValue",
        "ListSecrets",
        "PutSecretValue",
        "UpdateSecret",
    ];
    private static readonly string[] SecretsManagerLifecycleSchedule =
    [
        "CreateSecret",
        "DescribeSecret",
        "GetSecretValue",
        "PutSecretValue",
        "GetSecretValue",
        "UpdateSecret",
        "GetSecretValue",
        "ListSecrets",
        "DeleteSecret",
    ];

    public static string ExpectedOperationMixIdentity =>
        OperationMixIdentity(SecretsManagerLifecycleProfile, SecretsManagerLifecycleSchedule);

    public static RcCalibrationReport Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("RC calibration report not found", path);
        }

        var bytes = File.ReadAllBytes(path);
        RejectDuplicateProperties(bytes, path);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        var context = new RcCalibrationJsonContext(options);
        return JsonSerializer.Deserialize(bytes, context.RcCalibrationReport)
            ?? throw new InvalidDataException($"{path}: empty RC calibration report");
    }

    public static IReadOnlyList<string> Validate(RcCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var errors = new List<string>();
        void Err(string message) => errors.Add(message);

        if (report.SchemaVersion != 1)
        {
            Err("schema_version must be 1");
        }
        if (report.ArtifactKind != "rc_observation_calibration")
        {
            Err("artifact_kind must be 'rc_observation_calibration'");
        }
        if (report.Promotable is not false)
        {
            Err("calibration reports must declare promotable: false");
        }
        if (!ReleaseCandidateIdRegex().IsMatch(report.ReleaseCandidate.Id)
            || !IsDigest(report.ReleaseCandidate.ManifestDigest)
            || !IsGitSha(report.ReleaseCandidate.SourceSha)
            || !IsDigest(report.ReleaseCandidate.ArchiveInputsDigest)
            || !IsDigest(report.ReleaseCandidate.GhcrInputsDigest))
        {
            Err("release candidate identity linkage is incomplete or malformed");
        }
        if (report.Profile.Id != SecretsManagerLifecycleProfile
            || report.Profile.Version != 1)
        {
            Err("calibration is only valid for Secrets Manager lifecycle profile v1");
        }
        if (!RuntimeShape(report.Candidate) || !RuntimeShape(report.Prior))
        {
            Err("candidate and prior runtime identity linkage is incomplete");
        }
        if (report.ReleaseCandidate.SourceSha != report.Candidate.SourceSha)
        {
            Err("candidate runtime source must match the release candidate source");
        }
        if (report.Candidate.RuntimeIdentityDigest == report.Prior.RuntimeIdentityDigest
            || report.Candidate.RuntimeDigest == report.Prior.RuntimeDigest)
        {
            Err("candidate and prior identities must be distinct");
        }
        ValidateAzure(report.Azure, Err);
        ValidateWindow(report.Calibration, Err);
        ValidateConcurrency(report, Err);
        ValidateCohorts(report, Err);
        ValidateRestoration(report, Err);
        return errors;
    }

    public static void ValidateFile(string path)
    {
        var report = Load(path);
        var errors = Validate(report);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidateAzure(
        RcObservationAzureEnvironment azure,
        Action<string> err)
    {
        if (azure.BackendKind != "keyVault"
            || !RegionRegex().IsMatch(azure.Region)
            || !IsDigest(azure.BackendIdentityDigest)
            || !IsDigest(azure.ConfigDigest)
            || !IsDigest(azure.AwsBindingDigest))
        {
            err("Azure backend, region, configuration, or AWS binding is malformed");
        }
    }

    private static void ValidateWindow(RcCalibrationWindow window, Action<string> err)
    {
        var now = DateTimeOffset.UtcNow;
        var measurementInterval = window.MeasurementEndedAtUtc - window.StartedAtUtc;
        if (window.RequestedDurationMinutes is < 5 or > 20)
        {
            err("requested calibration duration must be between 5 and 20 minutes");
        }
        if (window.StartedAtUtc == default
            || window.MeasurementEndedAtUtc == default
            || window.EndedAtUtc == default
            || window.StartedAtUtc > window.MeasurementEndedAtUtc
            || window.MeasurementEndedAtUtc > window.EndedAtUtc)
        {
            err("calibration timestamps must be ordered and non-default");
        }
        if (measurementInterval < TimeSpan.FromMinutes(window.RequestedDurationMinutes))
        {
            err("measurement interval must cover the requested calibration duration");
        }
        if (window.StartedAtUtc > now.AddMinutes(5)
            || window.MeasurementEndedAtUtc > now.AddMinutes(5)
            || window.EndedAtUtc > now.AddMinutes(5))
        {
            err("calibration timestamps must not be in the future");
        }
    }

    private static void ValidateConcurrency(
        RcCalibrationReport report,
        Action<string> err)
    {
        if (report.PerCohortConcurrency.Candidate <= 0
            || report.PerCohortConcurrency.Stable <= 0
            || report.PerCohortConcurrency.Candidate > 32
            || report.PerCohortConcurrency.Stable > 32
            || report.TotalConcurrency != report.PerCohortConcurrency.Candidate
                + report.PerCohortConcurrency.Stable)
        {
            err("per-cohort and total concurrency are inconsistent");
        }
        if (report.OperationMixIdentity != ExpectedOperationMixIdentity)
        {
            err("operation_mix_identity does not match the canonical Secrets lifecycle mix");
        }
    }

    private static void ValidateCohorts(RcCalibrationReport report, Action<string> err)
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["candidate"] = report.PerCohortConcurrency.Candidate,
            ["stable"] = report.PerCohortConcurrency.Stable,
        };
        if (report.Cohorts.Count != 2
            || report.Cohorts.Select(cohort => cohort.Role).Distinct(StringComparer.Ordinal).Count() != 2)
        {
            err("calibration must contain distinct candidate and stable cohorts");
            return;
        }
        foreach (var cohort in report.Cohorts)
        {
            if (!expected.TryGetValue(cohort.Role, out var concurrency)
                || cohort.Concurrency != concurrency
                || cohort.GetSecretValueSamples < 0
                || !double.IsFinite(cohort.GetSecretValueThroughputPerSecond)
                || cohort.GetSecretValueThroughputPerSecond < 0
                || cohort.OperationDiagnostics.Count == 0)
            {
                err($"cohort '{cohort.Role}' has malformed throughput, concurrency, or diagnostics");
                continue;
            }
            ValidateDiagnostics(cohort, report.Calibration, err);
        }
    }

    private static void ValidateDiagnostics(
        RcCalibrationCohort cohort,
        RcCalibrationWindow window,
        Action<string> err)
    {
        if (cohort.OperationDiagnostics.Count != SecretsManagerLifecycleOperations.Length)
        {
            err($"cohort '{cohort.Role}' must report the exact Secrets lifecycle operations");
            return;
        }

        RcObservationOperationDiagnostic? getSecretValue = null;
        for (var index = 0; index < SecretsManagerLifecycleOperations.Length; index++)
        {
            var diagnostic = cohort.OperationDiagnostics[index];
            var expectedOperation = SecretsManagerLifecycleOperations[index];
            if (diagnostic.Service != "secretsmanager"
                || diagnostic.Operation != expectedOperation
                || diagnostic.Completions < 0
                || diagnostic.Failures < 0
                || diagnostic.Throttles < 0
                || diagnostic.Throttles > diagnostic.Failures)
            {
                err($"cohort '{cohort.Role}' has malformed operation diagnostics");
            }
            if (diagnostic.Operation == "GetSecretValue")
            {
                getSecretValue = diagnostic;
            }
            if ((diagnostic.Failures == 0) != (diagnostic.FirstFailure is null))
            {
                err($"cohort '{cohort.Role}' first failure must match failure count");
            }
            if (diagnostic.FirstFailure is not null
                && (string.IsNullOrWhiteSpace(diagnostic.FirstFailure.Category)
                    || string.IsNullOrWhiteSpace(diagnostic.FirstFailure.ErrorCode)
                    || diagnostic.FirstFailure.StatusCode is < 100 or > 599
                    || !SafeTokenRegex().IsMatch(diagnostic.FirstFailure.Category)
                    || !SafeTokenRegex().IsMatch(diagnostic.FirstFailure.ErrorCode)))
            {
                err($"cohort '{cohort.Role}' has unsanitized first failure details");
            }
        }
        if (getSecretValue is null)
        {
            err($"cohort '{cohort.Role}' is missing GetSecretValue diagnostics");
            return;
        }

        var getSecretValueSamples = getSecretValue.Completions + getSecretValue.Failures;
        if (cohort.GetSecretValueSamples != getSecretValueSamples)
        {
            err($"cohort '{cohort.Role}' GetSecretValue samples do not match diagnostics");
        }
        var measurementSeconds =
            (window.MeasurementEndedAtUtc - window.StartedAtUtc).TotalSeconds;
        var expectedThroughput = measurementSeconds <= 0
            ? double.NaN
            : getSecretValue.Completions / measurementSeconds;
        if (!double.IsFinite(expectedThroughput)
            || Math.Abs(cohort.GetSecretValueThroughputPerSecond - expectedThroughput)
            > ThroughputTolerance)
        {
            err($"cohort '{cohort.Role}' GetSecretValue throughput does not match diagnostics");
        }
    }

    private static void ValidateRestoration(
        RcCalibrationReport report,
        Action<string> err)
    {
        var restoration = report.Restoration;
        if (!restoration.Verified
            || restoration.RuntimeIdentityDigest != report.Prior.RuntimeIdentityDigest
            || restoration.RuntimeDigest != report.Prior.RuntimeDigest
            || restoration.BackendIdentityDigest != report.Azure.BackendIdentityDigest
            || restoration.ConfigDigest != report.Azure.ConfigDigest
            || restoration.AwsBindingDigest != report.Azure.AwsBindingDigest
            || restoration.StartedAtUtc < report.Calibration.MeasurementEndedAtUtc
            || restoration.VerifiedAtUtc < restoration.StartedAtUtc
            || restoration.StartedAtUtc > report.Calibration.EndedAtUtc
            || restoration.VerifiedAtUtc != report.Calibration.EndedAtUtc)
        {
            err("calibration must verify exact-prior restoration on the same backend");
        }
    }

    private static bool RuntimeShape(RcCalibrationRuntimeIdentity identity) =>
        IsDigest(identity.RuntimeIdentityDigest)
        && IsDigest(identity.RuntimeDigest)
        && IsGitSha(identity.SourceSha);

    private static bool IsDigest(string value) =>
        DigestRegex().IsMatch(value);

    private static bool IsGitSha(string value) =>
        GitShaRegex().IsMatch(value);

    private static string OperationMixIdentity(
        string profile,
        IReadOnlyList<string> operations) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(profile + "\n" + string.Join("\n", operations))));

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes, string path)
    {
        var reader = new Utf8JsonReader(bytes);
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var property = reader.GetString()!;
                    if (!objects.Peek().Add(property))
                    {
                        throw new InvalidDataException(
                            $"{path}: duplicate JSON field '{property}'");
                    }
                    break;
            }
        }
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitShaRegex();

    [GeneratedRegex(
        "^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)-rc\\.[1-9][0-9]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseCandidateIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeTokenRegex();
}

[JsonSerializable(typeof(RcCalibrationReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class RcCalibrationJsonContext : JsonSerializerContext;
