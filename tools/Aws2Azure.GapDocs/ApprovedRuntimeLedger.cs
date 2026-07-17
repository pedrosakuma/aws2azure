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
    public string SourceFile { get; set; } = string.Empty;
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
    public string SubjectName { get; set; } = string.Empty;
    public string SubjectDigest { get; set; } = string.Empty;
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
        record.Attestation.SubjectName ??= string.Empty;
        record.Attestation.SubjectDigest ??= string.Empty;
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
        ValidateDecision(record, Err);
        return errors;
    }

    private static void ValidateRuntime(ApprovedRuntimeRecord record, Action<string> err)
    {
        var runtime = record.Runtime;
        if (string.IsNullOrWhiteSpace(runtime.Target.OperatingSystem)
            || string.IsNullOrWhiteSpace(runtime.Target.Architecture)
            || string.IsNullOrWhiteSpace(runtime.Target.Rid))
        {
            err("runtime.target operating_system, architecture, and rid are required");
        }
        if (string.IsNullOrWhiteSpace(runtime.SourceRepository))
        {
            err("runtime.source_repository missing");
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
        if (!producer.Workflow.StartsWith(".github/workflows/", StringComparison.Ordinal)
            || !producer.Workflow.EndsWith(".yml", StringComparison.Ordinal))
        {
            err("producer.workflow must be a repository workflow path ending in .yml");
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
        if (string.IsNullOrWhiteSpace(attestation.PredicateType))
        {
            err("attestation.predicate_type missing");
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

    private static void ValidateDecision(ApprovedRuntimeRecord record, Action<string> err)
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
                ValidateQualification(record, err);
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
        "^aws2azure-sealed-linux-x64-(?<digest>[0-9a-f]{64})-run-(?<run>[1-9][0-9]*)-attempt-(?<attempt>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactNameRegex();
}
