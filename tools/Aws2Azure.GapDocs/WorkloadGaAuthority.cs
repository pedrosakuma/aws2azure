using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class WorkloadGaEvaluationContract
{
    public int SchemaVersion { get; set; }
    public string AsOf { get; set; } = string.Empty;
    public string SourceRepository { get; set; } = string.Empty;
    public string ExpectedCanonicalInputsRevision { get; set; } = string.Empty;
    public int ExpectedEvaluatorRevision { get; set; }

    [YamlIgnore]
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class WorkloadGaEvaluationMetadata
{
    public string EvaluatedAsOf { get; set; } = string.Empty;
    public string Contract { get; set; } = string.Empty;
    public WorkloadGaSourceIdentity Source { get; set; } = new();
}

public sealed class WorkloadGaSourceIdentity
{
    public string Repository { get; set; } = string.Empty;
    public string CanonicalInputsRevisionType { get; set; } = "normalized_yaml_sha256";
    public string CanonicalInputsRevision { get; set; } = string.Empty;
    public string EvaluatorRevisionType { get; set; } = "workload_ga_evaluator_schema_version";
    public int EvaluatorRevision { get; set; }
    public List<string> CanonicalInputRoots { get; set; } =
    [
        "docs/gaps/**/*.yaml",
        "docs/workloads/**/*.yaml",
    ];
    public List<string> ExcludedCanonicalInputs { get; set; } =
    [
        WorkloadGaEvaluationMetadataBuilder.ContractPath,
    ];
}

public sealed class WorkloadGaAuthorityMetadata
{
    public string Scope { get; set; } = "current_workload_adoption";
    public string TemporalScope { get; set; } = "point_in_time";
    public string HighestPrecedenceSource { get; set; } = "live_workload_certification";
    public bool HistoricalClaimsMayOverride { get; set; }
    public List<WorkloadGaPrecedenceEntry> Precedence { get; set; } =
    [
        new()
        {
            Rank = 1,
            Source = "live_workload_certification",
            Role = "authoritative_current_verdict",
        },
        new()
        {
            Rank = 2,
            Source = "workload_profile_manifests",
            Role = "normative_certification_input",
        },
        new()
        {
            Rank = 3,
            Source = "gap_docs",
            Role = "normative_capability_input",
        },
        new()
        {
            Rank = 4,
            Source = "release_notes",
            Role = "immutable_historical_record",
        },
        new()
        {
            Rank = 5,
            Source = "explanatory_guides",
            Role = "non_authoritative_explanation",
        },
    ];
}

public sealed class WorkloadGaPrecedenceEntry
{
    public int Rank { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public static class WorkloadGaEvaluationContractLoader
{
    public static WorkloadGaEvaluationContract Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Workload GA evaluation contract not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        using var reader = new StreamReader(path);
        var contract = deserializer.Deserialize<WorkloadGaEvaluationContract>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        contract.SourceFile = path;
        return contract;
    }
}

public static class WorkloadGaEvaluationContractValidator
{
    public const int CurrentSchemaVersion = 2;

    public static IReadOnlyList<string> Validate(
        WorkloadGaEvaluationContract contract,
        DateOnly trustedCurrentDate,
        string? canonicalInputsRevision = null)
    {
        var errors = new List<string>();
        var source = string.IsNullOrWhiteSpace(contract.SourceFile)
            ? "workload GA evaluation contract"
            : contract.SourceFile;
        void Err(string message) => errors.Add($"{source}: {message}");

        if (contract.SchemaVersion != CurrentSchemaVersion)
        {
            Err(
                $"unsupported schema_version '{contract.SchemaVersion}'; " +
                $"expected {CurrentSchemaVersion}");
        }
        if (!DateOnly.TryParseExact(
                contract.AsOf,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var asOf))
        {
            Err("as_of must be a calendar date in yyyy-MM-dd format");
        }
        else if (asOf > trustedCurrentDate)
        {
            Err(
                $"as_of '{contract.AsOf}' must not be later than trusted UTC date " +
                $"'{trustedCurrentDate:yyyy-MM-dd}'");
        }
        if (string.IsNullOrWhiteSpace(contract.SourceRepository)
            || contract.SourceRepository.Count(character => character == '/') != 1
            || contract.SourceRepository.StartsWith("/", StringComparison.Ordinal)
            || contract.SourceRepository.EndsWith("/", StringComparison.Ordinal))
        {
            Err("source_repository must use the owner/repository form");
        }
        ValidateSha256Revision(
            contract.ExpectedCanonicalInputsRevision,
            canonicalInputsRevision,
            "expected_canonical_inputs_revision",
            Err);
        if (contract.ExpectedEvaluatorRevision
            != WorkloadGaEvaluationMetadataBuilder.CurrentEvaluatorRevision)
        {
            Err(
                $"expected_evaluator_revision '{contract.ExpectedEvaluatorRevision}' " +
                $"does not match evaluator schema revision " +
                $"'{WorkloadGaEvaluationMetadataBuilder.CurrentEvaluatorRevision}'");
        }

        return errors;
    }

    private static void ValidateSha256Revision(
        string expected,
        string? actual,
        string field,
        Action<string> err)
    {
        if (!IsSha256(expected))
        {
            err($"{field} must use 'sha256:<64 lowercase hex>'");
            return;
        }
        if (actual is not null && !expected.Equals(actual, StringComparison.Ordinal))
        {
            err($"{field} '{expected}' does not match computed revision '{actual}'");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
}

public static class WorkloadGaEvaluationMetadataBuilder
{
    public const string ContractPath = "docs/workloads/certification/authority.yaml";
    public const int CurrentEvaluatorRevision = 2;

    public static WorkloadGaEvaluationMetadata Build(
        WorkloadGaEvaluationContract contract,
        string repoRoot)
    {
        return BuildFromRevision(
            contract,
            ComputeCanonicalInputRevision(repoRoot));
    }

    public static WorkloadGaEvaluationMetadata BuildFromRevision(
        WorkloadGaEvaluationContract contract,
        string canonicalInputsRevision)
    {
        return new WorkloadGaEvaluationMetadata
        {
            EvaluatedAsOf = contract.AsOf,
            Contract = ContractPath,
            Source = new WorkloadGaSourceIdentity
            {
                Repository = contract.SourceRepository,
                CanonicalInputsRevision = canonicalInputsRevision,
                EvaluatorRevision = CurrentEvaluatorRevision,
            },
        };
    }

    public static DateOnly ParseAsOf(WorkloadGaEvaluationContract contract) =>
        DateOnly.ParseExact(
            contract.AsOf,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    public static string ComputeCanonicalInputRevision(string repoRoot)
    {
        var canonicalFiles = EnumerateCanonicalFiles(repoRoot);
        return ComputeNormalizedTextRevision(repoRoot, canonicalFiles);
    }

    public static IReadOnlyList<string> ValidateCanonicalInputSnapshot(
        string repoRoot,
        string initialRevision)
    {
        var currentRevision = ComputeCanonicalInputRevision(repoRoot);
        if (currentRevision.Equals(initialRevision, StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            $"canonical inputs changed during evaluation: started at '{initialRevision}', " +
            $"now '{currentRevision}'",
        ];
    }

    private static string ComputeNormalizedTextRevision(
        string repoRoot,
        IReadOnlyList<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files)
        {
            var relativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            Append(hash, relativePath);
            Append(hash, "\n");
            var content = File.ReadAllText(path)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
            Append(hash, content);
            Append(hash, "\n\0");
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<string> EnumerateCanonicalFiles(string repoRoot)
    {
        var roots = new[]
        {
            Path.Combine(repoRoot, "docs", "gaps"),
            Path.Combine(repoRoot, "docs", "workloads"),
        };
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(
                    $"Workload GA canonical input directory not found: {root}");
            }
            files.AddRange(
                Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
                    .Where(path => !Path.GetRelativePath(repoRoot, path)
                        .Replace('\\', '/')
                        .Equals(ContractPath, StringComparison.Ordinal)));
        }

        return files
            .OrderBy(
                path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'),
                StringComparer.Ordinal)
            .ToList();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}

public static class WorkloadGaTemporalValidator
{
    public static IReadOnlyList<string> ValidateQualification(
        SloQualificationDocument qualification,
        DateOnly evaluatedAsOf)
    {
        var errors = new List<string>();
        var cutoff = EndOfDay(evaluatedAsOf);
        void Check(DateTimeOffset value, string field)
        {
            if (value != default && value.ToUniversalTime() > cutoff)
            {
                errors.Add($"{field} postdates evaluated_as_of '{evaluatedAsOf:yyyy-MM-dd}'");
            }
        }

        Check(qualification.Provenance.GeneratedAtUtc, "provenance.generated_at_utc");
        Check(qualification.Provenance.WindowStartUtc, "provenance.window_start_utc");
        Check(qualification.Provenance.WindowEndUtc, "provenance.window_end_utc");
        if (qualification.Provenance.CorrectnessRun is not null)
        {
            Check(
                qualification.Provenance.CorrectnessRun.WindowStartUtc,
                "provenance.correctness_run.window_start_utc");
            Check(
                qualification.Provenance.CorrectnessRun.WindowEndUtc,
                "provenance.correctness_run.window_end_utc");
            if (qualification.Provenance.CorrectnessRun.EvidenceArtifact is not null)
            {
                Check(
                    qualification.Provenance.CorrectnessRun.EvidenceArtifact.Artifact.CreatedAt,
                    "provenance.correctness_run.evidence_artifact.artifact.created_at");
            }
        }
        for (var index = 0; index < qualification.Provenance.SourceRuns.Count; index++)
        {
            var sourceRun = qualification.Provenance.SourceRuns[index];
            Check(
                sourceRun.WindowStartUtc,
                $"provenance.source_runs[{index}].window_start_utc");
            Check(
                sourceRun.WindowEndUtc,
                $"provenance.source_runs[{index}].window_end_utc");
            if (sourceRun.EvidenceArtifact is not null)
            {
                Check(
                    sourceRun.EvidenceArtifact.Artifact.CreatedAt,
                    $"provenance.source_runs[{index}].evidence_artifact.artifact.created_at");
            }
        }
        for (var index = 0; index < qualification.Signals.Count; index++)
        {
            Check(qualification.Signals[index].CapturedAtUtc, $"signals[{index}].captured_at_utc");
        }
        for (var index = 0; index < qualification.Scenarios.Count; index++)
        {
            Check(qualification.Scenarios[index].CapturedAtUtc, $"scenarios[{index}].captured_at_utc");
        }
        CheckRuntime(qualification.Candidate.Runtime, "candidate.runtime", Check);
        for (var index = 0; index < qualification.RollbackProofs.Count; index++)
        {
            var proof = qualification.RollbackProofs[index];
            var prefix = $"rollback_proofs[{index}]";
            CheckRuntime(proof.Candidate, prefix + ".candidate", Check);
            CheckRuntime(proof.Prior, prefix + ".prior", Check);
            Check(proof.StartedAtUtc, prefix + ".started_at_utc");
            Check(proof.CandidateCreateCompletedAtUtc, prefix + ".candidate_create_completed_at_utc");
            Check(proof.CandidateReadCompletedAtUtc, prefix + ".candidate_read_completed_at_utc");
            Check(proof.CandidateStoppedAtUtc, prefix + ".candidate_stopped_at_utc");
            Check(proof.PriorStartedAtUtc, prefix + ".prior_started_at_utc");
            Check(proof.PriorReadCompletedAtUtc, prefix + ".prior_read_completed_at_utc");
            Check(proof.CleanupRequestedAtUtc, prefix + ".cleanup_requested_at_utc");
            Check(proof.CleanupVerifiedAtUtc, prefix + ".cleanup_verified_at_utc");
            Check(proof.CandidateRestoredAtUtc, prefix + ".candidate_restored_at_utc");
            Check(proof.CompletedAtUtc, prefix + ".completed_at_utc");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateApprovedRuntime(
        ApprovedRuntimeRecord record,
        DateOnly evaluatedAsOf)
    {
        var errors = new List<string>();
        var cutoff = EndOfDay(evaluatedAsOf);
        void Check(DateTimeOffset value, string field)
        {
            if (value != default && value.ToUniversalTime() > cutoff)
            {
                errors.Add($"{field} postdates evaluated_as_of '{evaluatedAsOf:yyyy-MM-dd}'");
            }
        }

        Check(record.Artifact.CreatedAt, "artifact.created_at");
        Check(record.Approval.ReviewedAt, "approval.reviewed_at");
        if (record.Qualification is not null)
        {
            Check(record.Qualification.QualifiedAt, "qualification.qualified_at");
            CheckRuntime(
                record.Qualification.RollbackTarget,
                "qualification.rollback_target",
                Check);
        }
        if (record.Revocation is not null)
        {
            Check(record.Revocation.RevokedAt, "revocation.revoked_at");
        }

        return errors;
    }

    private static DateTimeOffset EndOfDay(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));

    private static void CheckRuntime(
        QualificationSealedRuntimeIdentity? identity,
        string prefix,
        Action<DateTimeOffset, string> check)
    {
        if (identity is null)
        {
            return;
        }
        check(identity.Producer.RunStartedAt, prefix + ".producer.run_started_at");
        check(identity.Artifact.CreatedAt, prefix + ".artifact.created_at");
    }
}

public sealed class WorkloadGaCertificationIndex
{
    public int SchemaVersion { get; set; } = 2;
    public WorkloadGaEvaluationMetadata Evaluation { get; set; } = new();
    public WorkloadGaAuthorityMetadata Authority { get; set; } = new();
    public List<WorkloadGaReport> Profiles { get; set; } = new();
}

public sealed class WorkloadGaProfileCertification
{
    public int SchemaVersion { get; set; } = 2;
    public WorkloadGaEvaluationMetadata Evaluation { get; set; } = new();
    public WorkloadGaAuthorityMetadata Authority { get; set; } = new();
    public WorkloadGaReport Profile { get; set; } = new();
}
