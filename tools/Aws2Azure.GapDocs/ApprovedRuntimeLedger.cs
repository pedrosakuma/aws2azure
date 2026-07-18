using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class ApprovedRuntimeRecord
{
    public int SchemaVersion { get; set; }
    public ApprovedRuntimeProfile Profile { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    public ApprovedRuntimeEligibility Eligibility { get; set; } = new();
    public ApprovedRuntimeIdentity Runtime { get; set; } = new();
    public ApprovedRuntimeProducer Producer { get; set; } = new();
    public ApprovedRuntimeArtifact Artifact { get; set; } = new();
    public ApprovedRuntimeAttestation Attestation { get; set; } = new();
    public ApprovedRuntimeConfigContract? ConfigContract { get; set; }
    public ApprovedRuntimeQualification? Qualification { get; set; }
    public ApprovedRuntimeApproval Approval { get; set; } = new();
    public ApprovedRuntimeRevocation? Revocation { get; set; }

    [YamlIgnore]
    [JsonIgnore]
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeLedgerExport
{
    public int SchemaVersion { get; set; } = 1;
    public string LedgerRecordDigest { get; set; } = string.Empty;
    public ApprovedRuntimeRecord Record { get; set; } = new();

    public static ApprovedRuntimeLedgerExport Create(ApprovedRuntimeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.SourceFile) || !File.Exists(record.SourceFile))
        {
            throw new InvalidDataException(
                "Approved-runtime export requires a committed source record.");
        }

        return new ApprovedRuntimeLedgerExport
        {
            LedgerRecordDigest = "sha256:" + Convert.ToHexStringLower(
                SHA256.HashData(File.ReadAllBytes(record.SourceFile))),
            Record = record,
        };
    }

    public static ApprovedRuntimeLedgerExport Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Approved-runtime export '{path}' must be a regular file.");
        }

        var export = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            ApprovedRuntimeLedgerJsonContext.Default.ApprovedRuntimeLedgerExport)
            ?? throw new InvalidDataException(
                $"Approved-runtime export '{path}' is empty.");
        if (export.SchemaVersion != 1
            || export.LedgerRecordDigest.Length != 71
            || !export.LedgerRecordDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || export.LedgerRecordDigest.AsSpan(7)
                .IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            throw new InvalidDataException(
                $"Approved-runtime export '{path}' has an invalid identity.");
        }
        return export;
    }

    public static ApprovedRuntimeLedgerExport CreateRollbackTarget(
        ApprovedRuntimeRecord approvedRecord)
    {
        ArgumentNullException.ThrowIfNull(approvedRecord);
        var target = approvedRecord.Qualification?.RollbackTarget
            ?? throw new InvalidDataException(
                "Approved-runtime record does not contain a rollback target.");
        if (string.IsNullOrWhiteSpace(target.LedgerRecordDigest))
        {
            throw new InvalidDataException(
                "Approved-runtime rollback target lacks its committed ledger digest.");
        }

        return new ApprovedRuntimeLedgerExport
        {
            LedgerRecordDigest = target.LedgerRecordDigest,
            Record = new ApprovedRuntimeRecord
            {
                SchemaVersion = target.SchemaVersion,
                Profile = new ApprovedRuntimeProfile
                {
                    Id = target.Profile.Id,
                    Version = target.Profile.Version,
                },
                Status = target.Status,
                Eligibility = new ApprovedRuntimeEligibility
                {
                    RollbackBaselineEligible =
                        target.Eligibility.RollbackBaselineEligible,
                    PromotionEligible = target.Eligibility.PromotionEligible,
                },
                Runtime = new ApprovedRuntimeIdentity
                {
                    Target = new ApprovedRuntimeTarget
                    {
                        OperatingSystem = "linux",
                        Architecture = "x64",
                        Rid = "linux-x64",
                    },
                    SourceRepository = target.Source.Repository,
                    SourceSha = target.Source.Sha,
                    AggregateDigest = target.Runtime.AggregateDigest,
                    ExecutableDigest = target.Runtime.ExecutableDigest,
                },
                Producer = new ApprovedRuntimeProducer
                {
                    Workflow = target.Producer.Workflow,
                    RunId = target.Producer.RunId,
                    RunAttempt = target.Producer.RunAttempt,
                    RunUrl = target.Producer.RunUrl,
                },
                Artifact = new ApprovedRuntimeArtifact
                {
                    Id = target.Artifact.Id,
                    Name = target.Artifact.Name,
                    UploadDigest = target.Artifact.UploadDigest,
                    CreatedAt = target.Artifact.CreatedAt,
                    ExpiresAt = target.Artifact.ExpiresAt,
                },
                Attestation = new ApprovedRuntimeAttestation
                {
                    PredicateType = target.Attestation.PredicateType,
                    Repository = target.Attestation.Repository,
                    SignerWorkflow = target.Attestation.SignerWorkflow,
                    SourceSha = target.Attestation.SourceSha,
                    SourceRef = target.Attestation.SourceRef,
                    SubjectName = target.Attestation.ExecutableSubjectName,
                    SubjectDigest = target.Attestation.ExecutableSubjectDigest,
                    ManifestSubjectName = target.Attestation.ManifestSubjectName,
                    ManifestSubjectDigest = target.Attestation.ManifestSubjectDigest,
                },
            },
        };
    }
}

public sealed class ApprovedRuntimeProfile
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
}

public sealed class ApprovedRuntimeEligibility
{
    public bool RollbackBaselineEligible { get; set; }
    public bool PromotionEligible { get; set; }
}

public sealed class ApprovedRuntimeIdentity
{
    public ApprovedRuntimeTarget Target { get; set; } = new();
    public string SourceRepository { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string AggregateDigest { get; set; } = string.Empty;
    public string ExecutableDigest { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeTarget
{
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Rid { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeProducer
{
    public string Workflow { get; set; } = string.Empty;
    public long RunId { get; set; }
    public int RunAttempt { get; set; }
    public string RunUrl { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeArtifact
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UploadDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public ApprovedRuntimeDurableReference? DurableReference { get; set; }
}

public sealed class ApprovedRuntimeDurableReference
{
    public string Uri { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeAttestation
{
    public string PredicateType { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string SignerWorkflow { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string SubjectDigest { get; set; } = string.Empty;
    public string ManifestSubjectName { get; set; } = string.Empty;
    public string ManifestSubjectDigest { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeConfigContract
{
    public string Reference { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeQualification
{
    public string Artifact { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public string CandidateRuntimeDigest { get; set; } = string.Empty;
    public string RollbackTargetRuntimeDigest { get; set; } = string.Empty;
    public QualificationSealedRuntimeIdentity? RollbackTarget { get; set; }
    public string ReviewUrl { get; set; } = string.Empty;
    public DateTimeOffset QualifiedAt { get; set; }
}

public sealed class ApprovedRuntimeApproval
{
    public string Reason { get; set; } = string.Empty;
    public string ReviewUrl { get; set; } = string.Empty;
    public DateTimeOffset ReviewedAt { get; set; }
    public string ReviewedBy { get; set; } = string.Empty;
}

public sealed class ApprovedRuntimeRevocation
{
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset RevokedAt { get; set; }
    public string ReviewUrl { get; set; } = string.Empty;
}

public static class ApprovedRuntimeLedgerLoader
{
    public static IReadOnlyList<ApprovedRuntimeRecord> LoadAll(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Approved-runtime ledger directory not found: {root}");
        }

        return Directory.EnumerateFiles(root, "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToList();
    }

    public static ApprovedRuntimeRecord Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Approved-runtime record not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        using var reader = new StreamReader(path);
        var record = deserializer.Deserialize<ApprovedRuntimeRecord>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        Normalize(record);
        record.SourceFile = path;
        return record;
    }

    private static void Normalize(ApprovedRuntimeRecord record)
    {
        record.Profile ??= new ApprovedRuntimeProfile();
        record.Profile.Id ??= string.Empty;
        record.Status ??= string.Empty;
        record.Eligibility ??= new ApprovedRuntimeEligibility();
        record.Runtime ??= new ApprovedRuntimeIdentity();
        record.Runtime.Target ??= new ApprovedRuntimeTarget();
        record.Runtime.Target.OperatingSystem ??= string.Empty;
        record.Runtime.Target.Architecture ??= string.Empty;
        record.Runtime.Target.Rid ??= string.Empty;
        record.Runtime.SourceRepository ??= string.Empty;
        record.Runtime.SourceSha ??= string.Empty;
        record.Runtime.AggregateDigest ??= string.Empty;
        record.Runtime.ExecutableDigest ??= string.Empty;
        record.Producer ??= new ApprovedRuntimeProducer();
        record.Producer.Workflow ??= string.Empty;
        record.Producer.RunUrl ??= string.Empty;
        record.Artifact ??= new ApprovedRuntimeArtifact();
        record.Artifact.Name ??= string.Empty;
        record.Artifact.UploadDigest ??= string.Empty;
        if (record.Artifact.DurableReference is not null)
        {
            record.Artifact.DurableReference.Uri ??= string.Empty;
            record.Artifact.DurableReference.Digest ??= string.Empty;
        }
        record.Attestation ??= new ApprovedRuntimeAttestation();
        record.Attestation.PredicateType ??= string.Empty;
        record.Attestation.Repository ??= string.Empty;
        record.Attestation.SignerWorkflow ??= string.Empty;
        record.Attestation.SourceSha ??= string.Empty;
        record.Attestation.SourceRef ??= string.Empty;
        record.Attestation.SubjectName ??= string.Empty;
        record.Attestation.SubjectDigest ??= string.Empty;
        record.Attestation.ManifestSubjectName ??= string.Empty;
        record.Attestation.ManifestSubjectDigest ??= string.Empty;
        if (record.ConfigContract is not null)
        {
            record.ConfigContract.Reference ??= string.Empty;
            record.ConfigContract.Digest ??= string.Empty;
        }
        if (record.Qualification is not null)
        {
            record.Qualification.Artifact ??= string.Empty;
            record.Qualification.Digest ??= string.Empty;
            record.Qualification.Verdict ??= string.Empty;
            record.Qualification.CandidateRuntimeDigest ??= string.Empty;
            record.Qualification.RollbackTargetRuntimeDigest ??= string.Empty;
            record.Qualification.ReviewUrl ??= string.Empty;
        }
        record.Approval ??= new ApprovedRuntimeApproval();
        record.Approval.Reason ??= string.Empty;
        record.Approval.ReviewUrl ??= string.Empty;
        record.Approval.ReviewedBy ??= string.Empty;
        if (record.Revocation is not null)
        {
            record.Revocation.Reason ??= string.Empty;
            record.Revocation.ReviewUrl ??= string.Empty;
        }
    }
}

public static partial class ApprovedRuntimeLedgerValidator
{
    public const int CurrentSchemaVersion = 1;

    private static readonly HashSet<string> Statuses =
        new(["bootstrap", "approved", "revoked"], StringComparer.Ordinal);

    public static IReadOnlyList<string> Validate(
        IReadOnlyList<ApprovedRuntimeRecord> records,
        IReadOnlyList<WorkloadGaManifest> profiles,
        DateTimeOffset now)
    {
        var errors = new List<string>();
        var profilesById = profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var seenProfiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            errors.AddRange(ValidateRecord(record, profilesById, now));
            if (!seenProfiles.Add(record.Profile.Id))
            {
                errors.Add(
                    $"{Source(record)}: duplicate approved-runtime record for profile " +
                    $"'{record.Profile.Id}'");
            }
        }

        ValidateUnambiguousProducerIdentities(records, errors);
        return errors;
    }

    private static IReadOnlyList<string> ValidateRecord(
        ApprovedRuntimeRecord record,
        IReadOnlyDictionary<string, WorkloadGaManifest> profilesById,
        DateTimeOffset now)
    {
        var errors = new List<string>();
        void Err(string message) => errors.Add($"{Source(record)}: {message}");

        if (record.SchemaVersion != CurrentSchemaVersion)
        {
            Err(
                $"unsupported schema_version '{record.SchemaVersion}'; " +
                $"expected {CurrentSchemaVersion}");
        }

        if (!profilesById.TryGetValue(record.Profile.Id, out var profile))
        {
            Err($"unknown profile '{record.Profile.Id}'");
        }
        else if (record.Profile.Version != profile.Version)
        {
            Err(
                $"profile version drift for '{record.Profile.Id}': ledger has " +
                $"{record.Profile.Version}, manifest has {profile.Version}");
        }

        var expectedFileName = record.Profile.Id + ".yaml";
        if (!string.IsNullOrWhiteSpace(record.SourceFile)
            && !Path.GetFileName(record.SourceFile).Equals(expectedFileName, StringComparison.Ordinal))
        {
            Err($"record file name must be '{expectedFileName}'");
        }

        if (!Statuses.Contains(record.Status))
        {
            Err($"invalid status '{record.Status}'; allowed: bootstrap, approved, revoked");
        }

        ValidateRuntime(record, Err);
        ValidateProducer(record, Err);
        ValidateArtifact(record, now, Err);
        ValidateAttestation(record, Err);
        ValidateOptionalConfigContract(record, Err);
        ValidateDecision(record, now, Err);
        return errors;
    }

    private static void ValidateRuntime(ApprovedRuntimeRecord record, Action<string> err)
    {
        var runtime = record.Runtime;
        if (runtime.Target.OperatingSystem != "linux"
            || runtime.Target.Architecture != "x64"
            || runtime.Target.Rid != "linux-x64")
        {
            err("runtime.target must be linux/x64 with rid linux-x64");
        }
        if (!RepositoryRegex().IsMatch(runtime.SourceRepository))
        {
            err("runtime.source_repository must be an owner/repository identity");
        }
        if (!GitShaRegex().IsMatch(runtime.SourceSha))
        {
            err("runtime.source_sha must be 40 lowercase hexadecimal characters");
        }
        RequireDigest(runtime.AggregateDigest, "runtime.aggregate_digest", err);
        RequireDigest(runtime.ExecutableDigest, "runtime.executable_digest", err);
        if (runtime.AggregateDigest.Equals(runtime.ExecutableDigest, StringComparison.Ordinal))
        {
            err("runtime aggregate and executable digests must identify distinct subjects");
        }
    }

    private static void ValidateProducer(ApprovedRuntimeRecord record, Action<string> err)
    {
        var producer = record.Producer;
        if (!producer.Workflow.Equals(
                ".github/workflows/sealed-runtime.yml",
                StringComparison.Ordinal))
        {
            err("producer.workflow must be '.github/workflows/sealed-runtime.yml'");
        }
        if (producer.RunId <= 0)
        {
            err("producer.run_id must be greater than zero");
        }
        if (producer.RunAttempt <= 0)
        {
            err("producer.run_attempt must be greater than zero");
        }

        var expectedUrl =
            $"https://github.com/{record.Runtime.SourceRepository}/actions/runs/{producer.RunId}";
        if (!producer.RunUrl.Equals(expectedUrl, StringComparison.Ordinal))
        {
            err($"producer.run_url must be '{expectedUrl}'");
        }
    }

    private static void ValidateArtifact(
        ApprovedRuntimeRecord record,
        DateTimeOffset now,
        Action<string> err)
    {
        var artifact = record.Artifact;
        if (artifact.Id <= 0)
        {
            err("artifact.id must be greater than zero");
        }
        RequireDigest(artifact.UploadDigest, "artifact.upload_digest", err);
        if (artifact.UploadDigest.Equals(record.Runtime.AggregateDigest, StringComparison.Ordinal)
            || artifact.UploadDigest.Equals(record.Runtime.ExecutableDigest, StringComparison.Ordinal))
        {
            err("artifact upload, runtime aggregate, and executable digests must be distinct");
        }

        var match = ArtifactNameRegex().Match(artifact.Name);
        if (!match.Success)
        {
            err(
                "artifact.name must embed the linux-x64 runtime digest, run id, and attempt");
        }
        else
        {
            var embeddedDigest = "sha256:" + match.Groups["digest"].Value;
            if (!embeddedDigest.Equals(record.Runtime.AggregateDigest, StringComparison.Ordinal))
            {
                err("artifact.name runtime digest does not match runtime.aggregate_digest");
            }
            if (!long.TryParse(match.Groups["run"].Value, out var runId)
                || runId != record.Producer.RunId)
            {
                err("artifact.name run id does not match producer.run_id");
            }
            if (!int.TryParse(match.Groups["attempt"].Value, out var attempt)
                || attempt != record.Producer.RunAttempt)
            {
                err("artifact.name attempt does not match producer.run_attempt");
            }
        }

        if (artifact.CreatedAt == default)
        {
            err("artifact.created_at missing");
        }
        if (artifact.ExpiresAt == default)
        {
            err("artifact.expires_at missing");
        }
        else if (artifact.ExpiresAt <= artifact.CreatedAt)
        {
            err("artifact.expires_at must be later than artifact.created_at");
        }

        if (artifact.DurableReference is not null)
        {
            if (!Uri.TryCreate(artifact.DurableReference.Uri, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "oci"))
            {
                err("artifact.durable_reference.uri must be an absolute https or oci URI");
            }
            RequireDigest(
                artifact.DurableReference.Digest,
                "artifact.durable_reference.digest",
                err);
        }
        if (artifact.ExpiresAt <= now && artifact.DurableReference is null)
        {
            err(
                "artifact is expired and has no durable immutable release/package reference");
        }
    }

    private static void ValidateAttestation(
        ApprovedRuntimeRecord record,
        Action<string> err)
    {
        var attestation = record.Attestation;
        if (!attestation.PredicateType.Equals(
                "https://slsa.dev/provenance/v1",
                StringComparison.Ordinal))
        {
            err("attestation.predicate_type must be the SLSA provenance v1 predicate");
        }
        if (!attestation.Repository.Equals(record.Runtime.SourceRepository, StringComparison.Ordinal))
        {
            err("attestation.repository must match runtime.source_repository");
        }
        if (!attestation.SignerWorkflow.Equals(
                record.Runtime.SourceRepository + "/" + record.Producer.Workflow,
                StringComparison.Ordinal))
        {
            err("attestation.signer_workflow must identify the exact producer workflow");
        }
        if (!attestation.SourceSha.Equals(record.Runtime.SourceSha, StringComparison.Ordinal))
        {
            err("attestation.source_sha must match runtime.source_sha");
        }
        if (!attestation.SourceRef.Equals("refs/heads/main", StringComparison.Ordinal)
            && !ReleaseCandidateRefRegex().IsMatch(attestation.SourceRef))
        {
            err("attestation.source_ref must be protected main or an allowed release-candidate tag");
        }
        if (!attestation.SubjectName.Equals("Aws2Azure.Proxy", StringComparison.Ordinal))
        {
            err("attestation.subject_name must be 'Aws2Azure.Proxy'");
        }
        RequireDigest(attestation.SubjectDigest, "attestation.subject_digest", err);
        if (!attestation.SubjectDigest.Equals(
                record.Runtime.ExecutableDigest,
                StringComparison.Ordinal))
        {
            err("attestation.subject_digest must match runtime.executable_digest");
        }
        if (!attestation.ManifestSubjectName.Equals(
                "sealed-runtime-manifest.json",
                StringComparison.Ordinal))
        {
            err("attestation.manifest_subject_name must be 'sealed-runtime-manifest.json'");
        }
        RequireDigest(
            attestation.ManifestSubjectDigest,
            "attestation.manifest_subject_digest",
            err);
        if (attestation.ManifestSubjectDigest.Equals(
                attestation.SubjectDigest,
                StringComparison.Ordinal))
        {
            err("attestation executable and manifest subjects must have distinct digests");
        }
    }

    private static void ValidateOptionalConfigContract(
        ApprovedRuntimeRecord record,
        Action<string> err)
    {
        if (record.ConfigContract is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(record.ConfigContract.Reference))
        {
            err("config_contract.reference missing");
        }
        RequireDigest(record.ConfigContract.Digest, "config_contract.digest", err);
    }

    private static void ValidateDecision(
        ApprovedRuntimeRecord record,
        DateTimeOffset now,
        Action<string> err)
    {
        if (string.IsNullOrWhiteSpace(record.Approval.Reason))
        {
            err("approval.reason missing");
        }
        if (!IsAbsoluteHttpsUrl(record.Approval.ReviewUrl))
        {
            err("approval.review_url must be an absolute https URL");
        }
        if (record.Approval.ReviewedAt == default)
        {
            err("approval.reviewed_at missing");
        }
        if (string.IsNullOrWhiteSpace(record.Approval.ReviewedBy))
        {
            err("approval.reviewed_by missing");
        }

        switch (record.Status)
        {
            case "bootstrap":
                if (!record.Eligibility.RollbackBaselineEligible)
                {
                    err("bootstrap must be rollback_baseline_eligible");
                }
                if (record.Eligibility.PromotionEligible)
                {
                    err("bootstrap must not be promotion_eligible");
                }
                if (record.Qualification is not null)
                {
                    err("bootstrap must not carry qualification evidence");
                }
                if (record.Revocation is not null)
                {
                    err("bootstrap must not carry revocation metadata");
                }
                break;

            case "approved":
                if (!record.Eligibility.RollbackBaselineEligible
                    || !record.Eligibility.PromotionEligible)
                {
                    err("approved runtime must be rollback_baseline_eligible and promotion_eligible");
                }
                if (record.Revocation is not null)
                {
                    err("approved runtime must not carry revocation metadata");
                }
                ValidateQualification(record, now, err);
                break;

            case "revoked":
                if (record.Eligibility.RollbackBaselineEligible
                    || record.Eligibility.PromotionEligible)
                {
                    err("revoked runtime must not be rollback or promotion eligible");
                }
                if (record.Revocation is null)
                {
                    err("revoked runtime must carry revocation reason and date");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(record.Revocation.Reason))
                    {
                        err("revocation.reason missing");
                    }
                    if (record.Revocation.RevokedAt == default)
                    {
                        err("revocation.revoked_at missing");
                    }
                    if (!IsAbsoluteHttpsUrl(record.Revocation.ReviewUrl))
                    {
                        err("revocation.review_url must be an absolute https URL");
                    }
                }
                break;
        }
    }

    private static void ValidateQualification(
        ApprovedRuntimeRecord record,
        DateTimeOffset now,
        Action<string> err)
    {
        var qualification = record.Qualification;
        if (qualification is null)
        {
            err("approved runtime must carry qualification evidence");
            return;
        }
        if (string.IsNullOrWhiteSpace(qualification.Artifact))
        {
            err("qualification.artifact missing");
        }
        RequireDigest(qualification.Digest, "qualification.digest", err);
        if (!qualification.Verdict.Equals("qualified", StringComparison.Ordinal))
        {
            err("qualification.verdict must be 'qualified'");
        }
        RequireDigest(
            qualification.CandidateRuntimeDigest,
            "qualification.candidate_runtime_digest",
            err);
        RequireDigest(
            qualification.RollbackTargetRuntimeDigest,
            "qualification.rollback_target_runtime_digest",
            err);
        if (!qualification.CandidateRuntimeDigest.Equals(
                record.Runtime.AggregateDigest,
                StringComparison.Ordinal))
        {
            err("qualification candidate runtime does not match the ledger runtime");
        }
        if (qualification.RollbackTargetRuntimeDigest.Equals(
                qualification.CandidateRuntimeDigest,
                StringComparison.Ordinal))
        {
            err("qualification rollback target must be a distinct runtime");
        }
        if (qualification.RollbackTarget is null)
        {
            err("qualification.rollback_target missing");
        }
        else
        {
            try
            {
                SealedRuntimeEvidenceValidator.ValidateTrustedRollbackTarget(
                    qualification.RollbackTarget,
                    record.Profile.Id,
                    record.Profile.Version,
                    qualification.RollbackTargetRuntimeDigest,
                    now);
            }
            catch (InvalidDataException exception)
            {
                err($"qualification.rollback_target invalid: {exception.Message}");
            }
        }
        if (!IsAbsoluteHttpsUrl(qualification.ReviewUrl))
        {
            err("qualification.review_url must be an absolute https URL");
        }
        if (qualification.QualifiedAt == default)
        {
            err("qualification.qualified_at missing");
        }
    }

    private static void ValidateUnambiguousProducerIdentities(
        IReadOnlyList<ApprovedRuntimeRecord> records,
        List<string> errors)
    {
        foreach (var group in records.GroupBy(
                     record => (record.Runtime.SourceRepository,
                                record.Producer.RunId,
                                record.Producer.RunAttempt,
                                record.Artifact.Id)))
        {
            var identities = group
                .Select(record => (
                    record.Artifact.Name,
                    record.Artifact.UploadDigest,
                    record.Runtime.AggregateDigest,
                    record.Runtime.ExecutableDigest))
                .Distinct()
                .ToList();
            if (identities.Count > 1)
            {
                errors.Add(
                    "approved-runtime ledger: one producer run/attempt/artifact id " +
                    "resolves to conflicting artifact or runtime identities");
            }
        }
    }

    private static void RequireDigest(string value, string field, Action<string> err)
    {
        if (!Sha256Regex().IsMatch(value))
        {
            err($"{field} must use sha256:<64 lowercase hexadecimal characters>");
        }
    }

    private static bool IsAbsoluteHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    private static string Source(ApprovedRuntimeRecord record) =>
        string.IsNullOrWhiteSpace(record.SourceFile)
            ? "approved-runtime record"
            : record.SourceFile;

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitShaRegex();

    [GeneratedRegex(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex(
        "^aws2azure-sealed-linux-x64-(?<digest>[0-9a-f]{64})-run-(?<run>[1-9][0-9]*)-attempt-(?<attempt>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactNameRegex();

    [GeneratedRegex(
        "^refs/tags/v[0-9]+\\.[0-9]+\\.[0-9]+-rc([.-]?[0-9A-Za-z]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseCandidateRefRegex();
}

[JsonSerializable(typeof(ApprovedRuntimeLedgerExport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
internal sealed partial class ApprovedRuntimeLedgerJsonContext : JsonSerializerContext;
