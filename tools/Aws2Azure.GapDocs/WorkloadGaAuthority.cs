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
    public string EvaluatedAsOfUtc { get; set; } = string.Empty;
    public string SourceRepository { get; set; } = string.Empty;
    public string ExpectedCanonicalInputsRevision { get; set; } = string.Empty;
    public int ExpectedEvaluatorSchemaVersion { get; set; }
    public string ExpectedEvaluatorImplementationRevision { get; set; } = string.Empty;

    [YamlIgnore]
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class WorkloadGaEvaluationMetadata
{
    public string EvaluatedAsOfUtc { get; set; } = string.Empty;
    public string Contract { get; set; } = string.Empty;
    public WorkloadGaSourceIdentity Source { get; set; } = new();
}

public sealed class WorkloadGaSourceIdentity
{
    public string Repository { get; set; } = string.Empty;
    public string CanonicalInputsRevisionType { get; set; } = "normalized_yaml_sha256";
    public string CanonicalInputsRevision { get; set; } = string.Empty;
    public int EvaluatorSchemaVersion { get; set; }
    public string EvaluatorImplementationRevisionType { get; set; } =
        "gapdocs_evaluator_implementation_sha256";
    public string EvaluatorImplementationRevision { get; set; } = string.Empty;
    public List<string> CanonicalInputRoots { get; set; } =
    [
        "docs/gaps/**/*.yaml",
        "docs/workloads/**/*.yaml",
    ];
    public List<string> ExcludedCanonicalInputs { get; set; } =
    [
        WorkloadGaEvaluationMetadataBuilder.ContractPath,
    ];
    public List<string> EvaluatorImplementationRoots { get; set; } =
    [
        "tools/Aws2Azure.GapDocs/**/*.cs",
        "tools/Aws2Azure.GapDocs/Aws2Azure.GapDocs.csproj",
        "tools/Aws2Azure.GapDocs/GenerateEvaluatorIdentity.targets",
        "Directory.Build.props",
        "global.json",
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

        return Load(File.ReadAllBytes(path), path);
    }

    public static WorkloadGaEvaluationContract Load(
        ReadOnlyMemory<byte> bytes,
        string sourceFile)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var contract = deserializer.Deserialize<WorkloadGaEvaluationContract>(reader)
            ?? throw new InvalidDataException($"{sourceFile}: empty document");
        contract.SourceFile = sourceFile;
        return contract;
    }
}

public static class WorkloadGaEvaluationContractValidator
{
    public const int CurrentSchemaVersion = 3;

    public static IReadOnlyList<string> Validate(
        WorkloadGaEvaluationContract contract,
        DateTimeOffset trustedUtcNow,
        string? canonicalInputsRevision = null,
        string? evaluatorImplementationRevision = null)
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
        if (!DateTimeOffset.TryParseExact(
                contract.EvaluatedAsOfUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var evaluatedAsOfUtc))
        {
            Err("evaluated_as_of_utc must be an exact UTC instant in yyyy-MM-ddTHH:mm:ssZ format");
        }
        else if (evaluatedAsOfUtc > trustedUtcNow.ToUniversalTime())
        {
            Err(
                $"evaluated_as_of_utc '{contract.EvaluatedAsOfUtc}' must not be later than " +
                $"trusted UTC instant '{trustedUtcNow.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'");
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
        if (contract.ExpectedEvaluatorSchemaVersion
            != WorkloadGaEvaluationMetadataBuilder.CurrentEvaluatorSchemaVersion)
        {
            Err(
                $"expected_evaluator_schema_version '{contract.ExpectedEvaluatorSchemaVersion}' " +
                $"does not match evaluator schema revision " +
                $"'{WorkloadGaEvaluationMetadataBuilder.CurrentEvaluatorSchemaVersion}'");
        }
        ValidateSha256Revision(
            contract.ExpectedEvaluatorImplementationRevision,
            evaluatorImplementationRevision,
            "expected_evaluator_implementation_revision",
            Err);
        if (evaluatorImplementationRevision is not null
            && !evaluatorImplementationRevision.Equals(
                WorkloadGaEvaluationMetadataBuilder.EmbeddedEvaluatorImplementationRevision,
                StringComparison.Ordinal))
        {
            Err(
                $"captured evaluator implementation revision '{evaluatorImplementationRevision}' " +
                "does not match executing assembly revision " +
                $"'{WorkloadGaEvaluationMetadataBuilder.EmbeddedEvaluatorImplementationRevision}'; " +
                "rebuild the GapDocs evaluator");
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
    public const int CurrentEvaluatorSchemaVersion = 3;
    public static string EmbeddedEvaluatorImplementationRevision =>
        WorkloadGaEmbeddedEvaluatorIdentity.Revision;

    public static WorkloadGaEvaluationMetadata Build(WorkloadGaInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return BuildFromRevisions(
            snapshot.Contract,
            snapshot.CanonicalInputsRevision,
            snapshot.EvaluatorImplementationRevision);
    }

    private static WorkloadGaEvaluationMetadata BuildFromRevisions(
        WorkloadGaEvaluationContract contract,
        string canonicalInputsRevision,
        string evaluatorImplementationRevision)
    {
        return new WorkloadGaEvaluationMetadata
        {
            EvaluatedAsOfUtc = contract.EvaluatedAsOfUtc,
            Contract = ContractPath,
            Source = new WorkloadGaSourceIdentity
            {
                Repository = contract.SourceRepository,
                CanonicalInputsRevision = canonicalInputsRevision,
                EvaluatorSchemaVersion = CurrentEvaluatorSchemaVersion,
                EvaluatorImplementationRevision = evaluatorImplementationRevision,
            },
        };
    }

    public static DateTimeOffset ParseEvaluatedAsOfUtc(
        WorkloadGaEvaluationContract contract) =>
        DateTimeOffset.ParseExact(
            contract.EvaluatedAsOfUtc,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string ComputeCanonicalInputRevision(string repoRoot)
    {
        var canonicalFiles = EnumerateCanonicalFiles(repoRoot);
        return ComputeNormalizedTextRevision(repoRoot, canonicalFiles);
    }

    public static string ComputeEvaluatorImplementationRevision(string repoRoot)
    {
        var evaluatorFiles = EnumerateEvaluatorImplementationFiles(repoRoot);
        return ComputeNormalizedTextRevision(repoRoot, evaluatorFiles);
    }

    internal static string ComputeNormalizedTextRevision(
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

    internal static string ComputeNormalizedTextRevision(
        IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relativePath, bytes) in files.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            Append(hash, relativePath);
            Append(hash, "\n");
            var content = Encoding.UTF8.GetString(bytes);
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }
            content = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
            Append(hash, content);
            Append(hash, "\n\0");
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static IReadOnlyList<string> EnumerateCanonicalFiles(string repoRoot)
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

    internal static IReadOnlyList<string> EnumerateEvaluatorImplementationFiles(string repoRoot)
    {
        var toolRoot = Path.Combine(repoRoot, "tools", "Aws2Azure.GapDocs");
        if (!Directory.Exists(toolRoot))
        {
            throw new DirectoryNotFoundException(
                $"GapDocs evaluator source directory not found: {toolRoot}");
        }

        var files = Directory.EnumerateFiles(toolRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .ToList();
        foreach (var relativePath in new[]
                 {
                     "tools/Aws2Azure.GapDocs/Aws2Azure.GapDocs.csproj",
                     "tools/Aws2Azure.GapDocs/GenerateEvaluatorIdentity.targets",
                     "Directory.Build.props",
                     "global.json",
                 })
        {
            var path = Path.Combine(
                repoRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"GapDocs evaluator implementation input not found: {relativePath}",
                    path);
            }
            files.Add(path);
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

public sealed class WorkloadGaInputSnapshot : IDisposable
{
    private readonly string snapshotRoot;

    private WorkloadGaInputSnapshot(
        string snapshotRoot,
        WorkloadGaEvaluationContract contract,
        string canonicalInputsRevision,
        string evaluatorImplementationRevision)
    {
        this.snapshotRoot = snapshotRoot;
        Contract = contract;
        CanonicalInputsRevision = canonicalInputsRevision;
        EvaluatorImplementationRevision = evaluatorImplementationRevision;
    }

    public string RootPath => snapshotRoot;
    public WorkloadGaEvaluationContract Contract { get; }
    public string CanonicalInputsRevision { get; }
    public string EvaluatorImplementationRevision { get; }

    public static WorkloadGaInputSnapshot Capture(string repoRoot)
    {
        var fullRepoRoot = Path.GetFullPath(repoRoot);
        var contractPath = Path.Combine(
            fullRepoRoot,
            WorkloadGaEvaluationMetadataBuilder.ContractPath
                .Replace('/', Path.DirectorySeparatorChar));
        var contractBytes = CaptureRegularFile(fullRepoRoot, contractPath);
        var canonicalFiles = CaptureFiles(
            fullRepoRoot,
            WorkloadGaEvaluationMetadataBuilder.EnumerateCanonicalFiles(fullRepoRoot));
        var evaluatorFiles = CaptureFiles(
            fullRepoRoot,
            WorkloadGaEvaluationMetadataBuilder.EnumerateEvaluatorImplementationFiles(
                fullRepoRoot));
        var canonicalInputsRevision =
            WorkloadGaEvaluationMetadataBuilder.ComputeNormalizedTextRevision(canonicalFiles);
        var evaluatorImplementationRevision =
            WorkloadGaEvaluationMetadataBuilder.ComputeNormalizedTextRevision(evaluatorFiles);

        var snapshotRoot = Path.Combine(
            Path.GetTempPath(),
            $"aws2azure-workload-ga-snapshot-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(snapshotRoot);
            Materialize(
                snapshotRoot,
                WorkloadGaEvaluationMetadataBuilder.ContractPath,
                contractBytes);
            foreach (var (relativePath, bytes) in canonicalFiles)
            {
                Materialize(snapshotRoot, relativePath, bytes);
            }

            var contract = WorkloadGaEvaluationContractLoader.Load(
                contractBytes,
                WorkloadGaEvaluationMetadataBuilder.ContractPath);
            return new WorkloadGaInputSnapshot(
                snapshotRoot,
                contract,
                canonicalInputsRevision,
                evaluatorImplementationRevision);
        }
        catch
        {
            DeleteSnapshotDirectory(snapshotRoot);
            throw;
        }
    }

    public string GetPath(string repoRelativePath)
    {
        var normalized = repoRelativePath.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            normalized.Replace('/', Path.DirectorySeparatorChar),
            snapshotRoot);
        var relative = Path.GetRelativePath(snapshotRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || !File.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"'{repoRelativePath}' is not part of the workload authority snapshot.");
        }
        return fullPath;
    }

    public void Dispose()
    {
        DeleteSnapshotDirectory(snapshotRoot);
    }

    private static Dictionary<string, byte[]> CaptureFiles(
        string repoRoot,
        IReadOnlyList<string> paths)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var relativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            files.Add(relativePath, CaptureRegularFile(repoRoot, path));
        }
        return files;
    }

    private static byte[] CaptureRegularFile(string repoRoot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || ContainsReparsePoint(repoRoot, fullPath))
        {
            throw new InvalidDataException(
                $"Workload authority input '{path}' must be a regular repository file.");
        }
        return File.ReadAllBytes(fullPath);
    }

    internal static bool ContainsReparsePoint(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return true;
        }

        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }
        return false;
    }

    private static void Materialize(string root, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    private static void DeleteSnapshotDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }
}

public static class WorkloadGaAuthoritativeManifest
{
    public static string ResolveRelativePath(string repoRoot, string manifestPath)
    {
        var workloadsRoot = Path.GetFullPath(
            Path.Combine(repoRoot, "docs", "workloads"));
        var fullPath = Path.GetFullPath(manifestPath);
        var relativeToWorkloads = Path.GetRelativePath(workloadsRoot, fullPath);
        if (relativeToWorkloads.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeToWorkloads)
            || !Path.GetExtension(fullPath).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || WorkloadGaInputSnapshot.ContainsReparsePoint(workloadsRoot, fullPath))
        {
            throw new InvalidDataException(
                "Authoritative certification manifests must be regular .yaml files under " +
                "docs/workloads and must not traverse symbolic links.");
        }

        return Path.GetRelativePath(Path.GetFullPath(repoRoot), fullPath).Replace('\\', '/');
    }
}

public static class WorkloadGaTemporalValidator
{
    public static IReadOnlyList<string> ValidateQualification(
        SloQualificationDocument qualification,
        DateTimeOffset evaluatedAsOfUtc)
    {
        var errors = new List<string>();
        var cutoff = evaluatedAsOfUtc.ToUniversalTime();
        void Check(DateTimeOffset value, string field)
        {
            if (value != default && value.ToUniversalTime() > cutoff)
            {
                errors.Add(
                    $"{field} postdates evaluated_as_of_utc " +
                    $"'{cutoff:yyyy-MM-ddTHH:mm:ssZ}'");
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
        DateTimeOffset evaluatedAsOfUtc)
    {
        var errors = new List<string>();
        var cutoff = evaluatedAsOfUtc.ToUniversalTime();
        void Check(DateTimeOffset value, string field)
        {
            if (value != default && value.ToUniversalTime() > cutoff)
            {
                errors.Add(
                    $"{field} postdates evaluated_as_of_utc " +
                    $"'{cutoff:yyyy-MM-ddTHH:mm:ssZ}'");
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
