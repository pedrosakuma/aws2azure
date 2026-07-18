using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aws2Azure.GapDocs;

public sealed class QualificationSealedRuntimeProfile
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
}

public sealed class QualificationSealedRuntimeEligibility
{
    public bool RollbackBaselineEligible { get; set; }
    public bool PromotionEligible { get; set; }
}

public sealed class QualificationSealedRuntimeSource
{
    public string Repository { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public string Ref { get; set; } = string.Empty;
}

public sealed class QualificationSealedRuntimeDigests
{
    public string AggregateDigest { get; set; } = string.Empty;
    public string ExecutableDigest { get; set; } = string.Empty;
    public string ManifestDigest { get; set; } = string.Empty;
}

public sealed class QualificationSealedRuntimeProducer
{
    public string Workflow { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public long RunId { get; set; }
    public int RunAttempt { get; set; }
    public string RunUrl { get; set; } = string.Empty;
    public string AttemptUrl { get; set; } = string.Empty;
    public DateTimeOffset RunStartedAt { get; set; }
}

public sealed class QualificationSealedRuntimeArtifact
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UploadDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class QualificationSealedRuntimeAttestation
{
    public string PredicateType { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string SignerWorkflow { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string RunInvocationUrl { get; set; } = string.Empty;
    public string BundleDigest { get; set; } = string.Empty;
    public string ExecutableSubjectName { get; set; } = string.Empty;
    public string ExecutableSubjectDigest { get; set; } = string.Empty;
    public string ManifestSubjectName { get; set; } = string.Empty;
    public string ManifestSubjectDigest { get; set; } = string.Empty;
}

public sealed class QualificationSealedRuntimeIdentity
{
    public int SchemaVersion { get; set; }
    public string Role { get; set; } = string.Empty;
    public QualificationSealedRuntimeProfile Profile { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    public QualificationSealedRuntimeEligibility Eligibility { get; set; } = new();
    public string? LedgerRecordDigest { get; set; }
    public QualificationSealedRuntimeSource Source { get; set; } = new();
    public QualificationSealedRuntimeDigests Runtime { get; set; } = new();
    public QualificationSealedRuntimeProducer Producer { get; set; } = new();
    public QualificationSealedRuntimeArtifact Artifact { get; set; } = new();
    public QualificationSealedRuntimeAttestation Attestation { get; set; } = new();
}

public sealed class QualificationRunArtifactIdentity
{
    public int SchemaVersion { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string WorkflowPath { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string Conclusion { get; set; } = string.Empty;
    public long RunId { get; set; }
    public int RunAttempt { get; set; }
    public string RunUrl { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string HeadRef { get; set; } = string.Empty;
    public QualificationRunArtifact Artifact { get; set; } = new();
}

public sealed class QualificationRunArtifact
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UploadDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public static class SealedRuntimeEvidenceLoader
{
    public static QualificationSealedRuntimeIdentity LoadRuntime(string path) =>
        JsonSerializer.Deserialize(
            File.ReadAllText(path),
            SealedRuntimeEvidenceJsonContext.Default.QualificationSealedRuntimeIdentity)
        ?? throw new InvalidDataException($"{path}: empty sealed runtime identity");

    public static QualificationRunArtifactIdentity LoadRunArtifact(string path) =>
        JsonSerializer.Deserialize(
            File.ReadAllText(path),
            SealedRuntimeEvidenceJsonContext.Default.QualificationRunArtifactIdentity)
        ?? throw new InvalidDataException($"{path}: empty qualification run identity");
}

public static partial class SealedRuntimeEvidenceValidator
{
    public static void ValidateCandidate(
        QualificationSealedRuntimeIdentity identity,
        string profileId,
        int profileVersion,
        string sourceSha,
        string aggregateDigest,
        DateTimeOffset now)
    {
        ValidateCommon(identity, profileId, profileVersion, now);
        if (identity.Role != "candidate"
            || identity.Status != "candidate"
            || identity.Eligibility.RollbackBaselineEligible
            || identity.Eligibility.PromotionEligible
            || identity.LedgerRecordDigest is not null
            || identity.Source.Sha != sourceSha
            || identity.Runtime.AggregateDigest != aggregateDigest)
        {
            throw new InvalidDataException(
                "Sealed candidate identity does not match the qualification candidate.");
        }
    }

    public static void ValidateApprovedCandidate(
        QualificationSealedRuntimeIdentity identity,
        ApprovedRuntimeRecord record,
        DateTimeOffset now)
    {
        ValidateCommon(identity, record.Profile.Id, record.Profile.Version, now);
        if (record.Status != "approved"
            || identity.Role != "candidate"
            || identity.Status != "candidate"
            || identity.Eligibility.RollbackBaselineEligible
            || identity.Eligibility.PromotionEligible
            || identity.LedgerRecordDigest is not null
            || identity.Source.Repository != record.Runtime.SourceRepository
            || identity.Source.Sha != record.Runtime.SourceSha
            || identity.Source.Ref != record.Attestation.SourceRef
            || identity.Runtime.AggregateDigest != record.Runtime.AggregateDigest
            || identity.Runtime.ExecutableDigest != record.Runtime.ExecutableDigest
            || identity.Runtime.ManifestDigest != record.Attestation.ManifestSubjectDigest
            || identity.Producer.Workflow != record.Producer.Workflow
            || identity.Producer.RunId != record.Producer.RunId
            || identity.Producer.RunAttempt != record.Producer.RunAttempt
            || identity.Artifact.Id != record.Artifact.Id
            || identity.Artifact.Name != record.Artifact.Name
            || identity.Artifact.UploadDigest != record.Artifact.UploadDigest
            || identity.Artifact.CreatedAt.ToUniversalTime()
                != record.Artifact.CreatedAt.ToUniversalTime()
            || identity.Artifact.ExpiresAt.ToUniversalTime()
                != record.Artifact.ExpiresAt.ToUniversalTime()
            || identity.Attestation.PredicateType != record.Attestation.PredicateType
            || identity.Attestation.Repository != record.Attestation.Repository
            || identity.Attestation.SignerWorkflow != record.Attestation.SignerWorkflow
            || identity.Attestation.SourceSha != record.Attestation.SourceSha
            || identity.Attestation.SourceRef != record.Attestation.SourceRef
            || identity.Attestation.ExecutableSubjectName != record.Attestation.SubjectName
            || identity.Attestation.ExecutableSubjectDigest != record.Attestation.SubjectDigest
            || identity.Attestation.ManifestSubjectName
                != record.Attestation.ManifestSubjectName
            || identity.Attestation.ManifestSubjectDigest
                != record.Attestation.ManifestSubjectDigest)
        {
            throw new InvalidDataException(
                "Sealed qualification candidate does not exactly match the approved profile ledger.");
        }
    }

    public static void ValidateRollbackTarget(
        QualificationSealedRuntimeIdentity identity,
        string profileId,
        int profileVersion,
        string expectedAggregateDigest,
        string expectedLedgerRecordDigest,
        DateTimeOffset now)
    {
        ValidateCommon(identity, profileId, profileVersion, now);
        if (identity.Role != "prior"
            || identity.Status is not ("bootstrap" or "approved")
            || !identity.Eligibility.RollbackBaselineEligible
            || identity.Status == "bootstrap" && identity.Eligibility.PromotionEligible
            || identity.Status == "approved" && !identity.Eligibility.PromotionEligible
            || !IsDigest(identity.LedgerRecordDigest ?? string.Empty)
            || identity.LedgerRecordDigest != expectedLedgerRecordDigest
            || identity.Runtime.AggregateDigest != expectedAggregateDigest)
        {
            throw new InvalidDataException(
                "Sealed prior identity does not match the approved rollback target.");
        }
    }

    public static void ValidatePrior(
        QualificationSealedRuntimeIdentity identity,
        ApprovedRuntimeRecord record,
        DateTimeOffset now)
    {
        ValidateCommon(identity, record.Profile.Id, record.Profile.Version, now);
        var recordDigest = ApprovedRuntimeLedgerExport.Create(record).LedgerRecordDigest;
        if (identity.Role != "prior"
            || identity.Status != record.Status
            || identity.Status is not ("bootstrap" or "approved")
            || !identity.Eligibility.RollbackBaselineEligible
            || identity.Eligibility.RollbackBaselineEligible
                != record.Eligibility.RollbackBaselineEligible
            || identity.Eligibility.PromotionEligible != record.Eligibility.PromotionEligible
            || identity.LedgerRecordDigest != recordDigest
            || identity.Source.Repository != record.Runtime.SourceRepository
            || identity.Source.Sha != record.Runtime.SourceSha
            || identity.Source.Ref != record.Attestation.SourceRef
            || identity.Runtime.AggregateDigest != record.Runtime.AggregateDigest
            || identity.Runtime.ExecutableDigest != record.Runtime.ExecutableDigest
            || identity.Runtime.ManifestDigest != record.Attestation.ManifestSubjectDigest
            || identity.Producer.Workflow != record.Producer.Workflow
            || identity.Producer.RunId != record.Producer.RunId
            || identity.Producer.RunAttempt != record.Producer.RunAttempt
            || identity.Artifact.Id != record.Artifact.Id
            || identity.Artifact.Name != record.Artifact.Name
            || identity.Artifact.UploadDigest != record.Artifact.UploadDigest
            || identity.Artifact.CreatedAt.ToUniversalTime()
                != record.Artifact.CreatedAt.ToUniversalTime()
            || identity.Artifact.ExpiresAt.ToUniversalTime()
                != record.Artifact.ExpiresAt.ToUniversalTime()
            || identity.Attestation.PredicateType != record.Attestation.PredicateType
            || identity.Attestation.Repository != record.Attestation.Repository
            || identity.Attestation.SignerWorkflow != record.Attestation.SignerWorkflow
            || identity.Attestation.SourceSha != record.Attestation.SourceSha
            || identity.Attestation.SourceRef != record.Attestation.SourceRef
            || identity.Attestation.ExecutableSubjectName != record.Attestation.SubjectName
            || identity.Attestation.ExecutableSubjectDigest != record.Attestation.SubjectDigest
            || identity.Attestation.ManifestSubjectName
                != record.Attestation.ManifestSubjectName
            || identity.Attestation.ManifestSubjectDigest
                != record.Attestation.ManifestSubjectDigest)
        {
            throw new InvalidDataException(
                "Sealed prior identity does not exactly match the committed profile ledger.");
        }
    }

    public static string IdentityKey(QualificationSealedRuntimeIdentity identity)
    {
        return string.Join(
            '\n',
            identity.Role,
            identity.Profile.Id,
            identity.Profile.Version,
            identity.Status,
            identity.Eligibility.RollbackBaselineEligible,
            identity.Eligibility.PromotionEligible,
            identity.LedgerRecordDigest,
            identity.Source.Repository,
            identity.Source.Sha,
            identity.Source.Ref,
            identity.Runtime.AggregateDigest,
            identity.Runtime.ExecutableDigest,
            identity.Runtime.ManifestDigest,
            identity.Producer.Workflow,
            identity.Producer.EventName,
            identity.Producer.RunId,
            identity.Producer.RunAttempt,
            identity.Producer.RunUrl,
            identity.Producer.AttemptUrl,
            identity.Artifact.Id,
            identity.Artifact.Name,
            identity.Artifact.UploadDigest,
            identity.Artifact.CreatedAt.ToUniversalTime().ToString("O"),
            identity.Artifact.ExpiresAt.ToUniversalTime().ToString("O"),
            identity.Attestation.PredicateType,
            identity.Attestation.Repository,
            identity.Attestation.SignerWorkflow,
            identity.Attestation.SourceSha,
            identity.Attestation.SourceRef,
            identity.Attestation.RunInvocationUrl,
            identity.Attestation.BundleDigest,
            identity.Attestation.ExecutableSubjectName,
            identity.Attestation.ExecutableSubjectDigest,
            identity.Attestation.ManifestSubjectName,
            identity.Attestation.ManifestSubjectDigest);
    }

    public static bool IsDigest(string value) => Sha256Regex().IsMatch(value);

    public static bool IsTrustedRef(string value) => TrustedRefRegex().IsMatch(value);

    public static void ValidateRunArtifact(
        QualificationRunArtifactIdentity selection,
        string profileId,
        string expectedRepository,
        string expectedWorkflow,
        string expectedArtifactName,
        string expectedRunId,
        int expectedRunAttempt,
        string expectedHeadSha,
        string expectedHeadRef,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Artifact is null)
        {
            throw new InvalidDataException(
                $"Run {expectedRunId}/{expectedRunAttempt} evidence artifact metadata is missing.");
        }
        if (selection.SchemaVersion != 1
            || selection.ProfileId != profileId
            || selection.Repository != expectedRepository
            || !RepositoryRegex().IsMatch(selection.Repository)
            || selection.WorkflowPath != expectedWorkflow
            || selection.EventName != "workflow_dispatch"
            || selection.Conclusion != "success"
            || selection.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                != expectedRunId
            || selection.RunAttempt != expectedRunAttempt
            || selection.RunUrl
                != $"https://github.com/{selection.Repository}/actions/runs/{selection.RunId}"
            || selection.HeadSha != expectedHeadSha
            || selection.HeadRef != expectedHeadRef
            || !IsTrustedRef(selection.HeadRef)
            || selection.Artifact.Id <= 0
            || selection.Artifact.Name != expectedArtifactName
            || !IsDigest(selection.Artifact.UploadDigest)
            || selection.Artifact.CreatedAt == default
            || selection.Artifact.ExpiresAt <= selection.Artifact.CreatedAt
            || selection.Artifact.ExpiresAt <= now)
        {
            throw new InvalidDataException(
                $"Run {expectedRunId}/{expectedRunAttempt} does not match its trusted " +
                $"{expectedWorkflow} artifact identity for profile '{profileId}'.");
        }
    }

    private static void ValidateCommon(
        QualificationSealedRuntimeIdentity identity,
        string profileId,
        int profileVersion,
        DateTimeOffset now)
    {
        if (identity.SchemaVersion != 1
            || identity.Profile.Id != profileId
            || identity.Profile.Version != profileVersion
            || !RepositoryRegex().IsMatch(identity.Source.Repository)
            || !GitShaRegex().IsMatch(identity.Source.Sha)
            || !TrustedRefRegex().IsMatch(identity.Source.Ref)
            || !IsDigest(identity.Runtime.AggregateDigest)
            || !IsDigest(identity.Runtime.ExecutableDigest)
            || !IsDigest(identity.Runtime.ManifestDigest)
            || identity.Runtime.AggregateDigest == identity.Runtime.ExecutableDigest
            || identity.Runtime.ExecutableDigest == identity.Runtime.ManifestDigest
            || identity.Producer.Workflow != ".github/workflows/sealed-runtime.yml"
            || identity.Producer.EventName != "workflow_dispatch"
            || identity.Producer.RunId <= 0
            || identity.Producer.RunAttempt <= 0
            || identity.Producer.RunStartedAt == default
            || identity.Producer.RunUrl
                != $"https://github.com/{identity.Source.Repository}/actions/runs/" +
                   identity.Producer.RunId
            || identity.Producer.AttemptUrl
                != identity.Producer.RunUrl + "/attempts/" + identity.Producer.RunAttempt
            || identity.Artifact.Id <= 0
            || !IsDigest(identity.Artifact.UploadDigest)
            || identity.Artifact.CreatedAt == default
            || identity.Artifact.ExpiresAt <= identity.Artifact.CreatedAt
            || identity.Artifact.ExpiresAt <= now
            || identity.Attestation.PredicateType != "https://slsa.dev/provenance/v1"
            || identity.Attestation.Repository != identity.Source.Repository
            || identity.Attestation.SignerWorkflow
                != identity.Source.Repository + "/.github/workflows/sealed-runtime.yml"
            || identity.Attestation.SourceSha != identity.Source.Sha
            || identity.Attestation.SourceRef != identity.Source.Ref
            || identity.Attestation.RunInvocationUrl != identity.Producer.AttemptUrl
            || !IsDigest(identity.Attestation.BundleDigest)
            || identity.Attestation.ExecutableSubjectName != "Aws2Azure.Proxy"
            || identity.Attestation.ExecutableSubjectDigest
                != identity.Runtime.ExecutableDigest
            || identity.Attestation.ManifestSubjectName != "sealed-runtime-manifest.json"
            || identity.Attestation.ManifestSubjectDigest != identity.Runtime.ManifestDigest)
        {
            throw new InvalidDataException(
                "Sealed runtime identity is incomplete, stale, or internally inconsistent.");
        }

        var expectedArtifactName =
            $"aws2azure-sealed-linux-x64-{identity.Runtime.AggregateDigest["sha256:".Length..]}" +
            $"-run-{identity.Producer.RunId}-attempt-{identity.Producer.RunAttempt}";
        if (identity.Artifact.Name != expectedArtifactName)
        {
            throw new InvalidDataException(
                "Sealed runtime artifact name does not bind its digest and producer attempt.");
        }
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitShaRegex();

    [GeneratedRegex(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex(
        "^refs/heads/main$|^refs/tags/v[0-9]+\\.[0-9]+\\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TrustedRefRegex();
}

[JsonSerializable(typeof(QualificationSealedRuntimeIdentity))]
[JsonSerializable(typeof(QualificationRunArtifactIdentity))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal sealed partial class SealedRuntimeEvidenceJsonContext : JsonSerializerContext;
