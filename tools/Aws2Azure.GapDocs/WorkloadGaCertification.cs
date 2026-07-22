using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class WorkloadGaManifest
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MinimumProxyVersion { get; set; } = string.Empty;
    public int RealAzureSealMaxAgeDays { get; set; }
    public List<string> Operations { get; set; } = new();
    public List<string> Requirements { get; set; } = new();
    public List<string> AcceptedPartialOperations { get; set; } = new();
    public List<string> AcceptedDesignGaps { get; set; } = new();

    /// <summary>
    /// Optional narrower real-Azure evidence requirements for a profile whose
    /// required operations can be served by more than one Azure backend (for
    /// example SNS Publish over Service Bus Topics vs. Event Grid). Each entry
    /// requires the named documented sub-feature to carry its own fresh
    /// <c>verified_real_azure</c> seal, in addition to the operation-level seal,
    /// so a profile that claims one specific backend cannot be mechanically
    /// certified from evidence that only ever exercised a different backend
    /// (issue #630).
    /// </summary>
    public List<WorkloadGaSubFeatureSeal> RequiredSubFeatureSeals { get; set; } = new();

    public WorkloadGaEvidence Evidence { get; set; } = new();
    [YamlIgnore]
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class WorkloadGaSubFeatureSeal
{
    public string Operation { get; set; } = string.Empty;
    public string SubFeature { get; set; } = string.Empty;
}

public sealed class WorkloadGaEvidence
{
    public string QualificationArtifact { get; set; } = string.Empty;
    public List<string> RequiredScenarios { get; set; } = new();
    public List<string> RequiredRealAzureScenarios { get; set; } = new();
}

public sealed class WorkloadGaReport
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = string.Empty;
    public int ProfileVersion { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MinimumProxyVersion { get; set; } = string.Empty;
    public string Verdict { get; set; } = "blocked";
    public List<WorkloadGaFinding> Findings { get; set; } = new();
}

public sealed class WorkloadGaFinding
{
    public string Code { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public static class WorkloadGaManifestLoader
{
    public static WorkloadGaManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Workload GA manifest not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        using var reader = new StreamReader(path);
        var manifest = deserializer.Deserialize<WorkloadGaManifest>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        manifest.SourceFile = path;
        Normalize(manifest);
        return manifest;
    }

    public static IReadOnlyList<WorkloadGaManifest> LoadAll(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workload GA manifest directory not found: {root}");
        }

        return Directory.EnumerateFiles(root, "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToList();
    }

    private static void Normalize(WorkloadGaManifest manifest)
    {
        manifest.Operations ??= new List<string>();
        manifest.Requirements ??= new List<string>();
        manifest.AcceptedPartialOperations ??= new List<string>();
        manifest.AcceptedDesignGaps ??= new List<string>();
        manifest.RequiredSubFeatureSeals ??= new List<WorkloadGaSubFeatureSeal>();
        manifest.Evidence ??= new WorkloadGaEvidence();
        manifest.Evidence.RequiredScenarios ??= new List<string>();
        manifest.Evidence.RequiredRealAzureScenarios ??= new List<string>();
    }
}

public static class WorkloadGaManifestValidator
{
    public const int CurrentSchemaVersion = 1;

    public static IReadOnlyList<string> Validate(
        WorkloadGaManifest manifest,
        IReadOnlyList<OperationDoc> operationDocs,
        IReadOnlyList<ServiceDesignDoc> designDocs)
    {
        var errors = new List<string>();
        var source = string.IsNullOrWhiteSpace(manifest.SourceFile)
            ? "workload GA manifest"
            : manifest.SourceFile;
        void Err(string message) => errors.Add($"{source}: {message}");

        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            Err($"unsupported schema_version '{manifest.SchemaVersion}'; expected {CurrentSchemaVersion}");
        }
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            Err("id missing");
        }
        if (manifest.Version <= 0)
        {
            Err("version must be greater than zero");
        }
        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            Err("name missing");
        }
        if (!System.Version.TryParse(manifest.MinimumProxyVersion, out _))
        {
            Err("minimum_proxy_version must be a numeric semantic version");
        }
        if (manifest.RealAzureSealMaxAgeDays <= 0)
        {
            Err("real_azure_seal_max_age_days must be greater than zero");
        }
        if (manifest.Operations.Count == 0)
        {
            Err("operations must contain at least one operation");
        }
        if (manifest.Evidence.RequiredScenarios.Count == 0)
        {
            Err("evidence.required_scenarios must contain at least one scenario");
        }

        var operationsByKey = operationDocs.ToDictionary(
            operation => WorkloadManifestValidator.OperationKey(operation.Service, operation.Operation),
            StringComparer.OrdinalIgnoreCase);
        ValidateReferences(
            manifest.Operations,
            "operation",
            value => WorkloadManifestValidator.TryParseOperation(value, out var service, out var operation)
                     && operationsByKey.ContainsKey(
                         WorkloadManifestValidator.OperationKey(service, operation)),
            Err);

        var partialOperations = operationsByKey.Values
            .Where(operation => operation.Status.Equals("partial", StringComparison.OrdinalIgnoreCase))
            .Select(operation => operation.Service + ":" + operation.Operation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateReferences(
            manifest.AcceptedPartialOperations,
            "accepted partial operation",
            partialOperations.Contains,
            Err);
        foreach (var accepted in manifest.AcceptedPartialOperations)
        {
            if (!manifest.Operations.Contains(accepted, StringComparer.OrdinalIgnoreCase))
            {
                Err($"accepted partial operation '{accepted}' is not required by the profile");
            }
        }

        var patternsById = designDocs
            .SelectMany(
                design => design.WorkloadPatterns.Select(pattern => (design.Service, Pattern: pattern)))
            .ToDictionary(item => item.Pattern.Id, StringComparer.OrdinalIgnoreCase);
        ValidateReferences(
            manifest.Requirements,
            "requirement",
            patternsById.ContainsKey,
            Err);
        var requiredOperations =
            new HashSet<string>(manifest.Operations, StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in manifest.Requirements)
        {
            if (!patternsById.TryGetValue(requirement, out var pattern))
            {
                continue;
            }
            foreach (var operation in pattern.Pattern.Operations)
            {
                var reference = pattern.Service + ":" + operation;
                if (!requiredOperations.Contains(reference))
                {
                    Err(
                        $"requirement '{requirement}' operation '{reference}' is missing from operations");
                }
            }
        }

        var designGaps = designDocs
            .SelectMany(
                design => design.DesignGaps.Select(gap => design.Service + ":" + gap.Area))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateReferences(
            manifest.AcceptedDesignGaps,
            "accepted design gap",
            designGaps.Contains,
            Err);

        var seenSubFeatureSeals = new HashSet<(string Operation, string SubFeature)>();
        foreach (var seal in manifest.RequiredSubFeatureSeals)
        {
            if (string.IsNullOrWhiteSpace(seal.Operation) || string.IsNullOrWhiteSpace(seal.SubFeature))
            {
                Err("required_sub_feature_seals entry must set both operation and sub_feature");
                continue;
            }
            if (!seenSubFeatureSeals.Add((seal.Operation, seal.SubFeature)))
            {
                Err($"duplicate required sub-feature seal '{seal.Operation}#{seal.SubFeature}'");
            }
            if (!manifest.Operations.Contains(seal.Operation, StringComparer.OrdinalIgnoreCase))
            {
                Err($"required sub-feature seal operation '{seal.Operation}' is not required by the profile");
                continue;
            }
            if (!WorkloadManifestValidator.TryParseOperation(seal.Operation, out var service, out var operation)
                || !operationsByKey.TryGetValue(
                    WorkloadManifestValidator.OperationKey(service, operation),
                    out var doc))
            {
                continue;
            }
            if (!doc.SubFeatures.Any(
                    feature => feature.Name.Equals(seal.SubFeature, StringComparison.OrdinalIgnoreCase)))
            {
                Err(
                    $"required sub-feature seal '{seal.SubFeature}' does not exist under operation " +
                    $"'{seal.Operation}'");
            }
        }

        ValidateUnique(manifest.Evidence.RequiredScenarios, "evidence scenario", Err);
        ValidateUnique(
            manifest.Evidence.RequiredRealAzureScenarios,
            "real-Azure evidence scenario",
            Err);
        if (manifest.Evidence.RequiredScenarios.Any(string.IsNullOrWhiteSpace))
        {
            Err("evidence.required_scenarios contains an empty scenario");
        }
        foreach (var scenario in manifest.Evidence.RequiredRealAzureScenarios)
        {
            if (!manifest.Evidence.RequiredScenarios.Contains(scenario, StringComparer.Ordinal))
            {
                Err(
                    $"evidence.required_real_azure_scenarios entry '{scenario}' " +
                    "is not present in evidence.required_scenarios");
            }
        }

        return errors;
    }

    private static void ValidateReferences(
        IReadOnlyList<string> values,
        string kind,
        Func<string, bool> exists,
        Action<string> err)
    {
        ValidateUnique(values, kind, err);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                err($"{kind} reference must not be empty");
            }
            else if (!exists(value))
            {
                err($"unknown {kind} '{value}'");
            }
        }
    }

    private static void ValidateUnique(
        IReadOnlyList<string> values,
        string kind,
        Action<string> err)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                err($"duplicate {kind} '{value}'");
            }
        }
    }
}

public static class WorkloadGaEvaluator
{
    public static WorkloadGaReport Evaluate(
        WorkloadGaManifest manifest,
        IReadOnlyList<OperationDoc> operationDocs,
        IReadOnlyList<ServiceDesignDoc> designDocs,
        string repoRoot,
        DateOnly currentDate)
    {
        var report = new WorkloadGaReport
        {
            ProfileId = manifest.Id,
            ProfileVersion = manifest.Version,
            Name = manifest.Name,
            MinimumProxyVersion = manifest.MinimumProxyVersion,
        };
        var operationsByKey = operationDocs.ToDictionary(
            operation => WorkloadManifestValidator.OperationKey(operation.Service, operation.Operation),
            StringComparer.OrdinalIgnoreCase);
        var patternsById = designDocs
            .SelectMany(
                design => design.WorkloadPatterns.Select(pattern => (Design: design, Pattern: pattern)))
            .ToDictionary(item => item.Pattern.Id, StringComparer.OrdinalIgnoreCase);
        var acceptedPartialOperations =
            new HashSet<string>(manifest.AcceptedPartialOperations, StringComparer.OrdinalIgnoreCase);
        var acceptedDesignGaps =
            new HashSet<string>(manifest.AcceptedDesignGaps, StringComparer.OrdinalIgnoreCase);
        var hasCompatibilityBlocker = false;
        var hasSealBlocker = false;

        foreach (var reference in manifest.Operations.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            WorkloadManifestValidator.TryParseOperation(reference, out var service, out var operation);
            var doc = operationsByKey[WorkloadManifestValidator.OperationKey(service, operation)];
            if (doc.Status.Equals("stub", StringComparison.OrdinalIgnoreCase)
                || doc.Status.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
            {
                hasCompatibilityBlocker = true;
                Add(report, "operation_not_supported", "blocking", reference,
                    $"Required operation is documented as {doc.Status}.");
                continue;
            }
            if (doc.Status.Equals("partial", StringComparison.OrdinalIgnoreCase)
                && !acceptedPartialOperations.Contains(reference))
            {
                hasCompatibilityBlocker = true;
                Add(report, "partial_operation_not_accepted", "blocking", reference,
                    "Required operation is partial and is not explicitly accepted by the profile.");
            }
            else if (doc.Status.Equals("partial", StringComparison.OrdinalIgnoreCase))
            {
                Add(report, "partial_operation_accepted", "advisory", reference,
                    "The profile explicitly accepts the operation's documented partial semantics.");
            }

            if (!HasFreshSeal(doc.VerifiedRealAzure, currentDate, manifest.RealAzureSealMaxAgeDays))
            {
                hasSealBlocker = true;
                Add(report, doc.VerifiedRealAzure is null ? "real_azure_seal_missing" : "real_azure_seal_expired",
                    "blocking", reference,
                    doc.VerifiedRealAzure is null
                        ? "Required operation has no real-Azure verification seal."
                        : $"Real-Azure verification is older than {manifest.RealAzureSealMaxAgeDays} days.");
            }
        }

        foreach (var requirement in manifest.Requirements.OrderBy(value => value, StringComparer.Ordinal))
        {
            var (design, pattern) = patternsById[requirement];
            foreach (var area in pattern.DesignGaps.OrderBy(value => value, StringComparer.Ordinal))
            {
                var reference = design.Service + ":" + area;
                if (!acceptedDesignGaps.Contains(reference))
                {
                    hasCompatibilityBlocker = true;
                    Add(report, "design_gap_not_accepted", "blocking", reference,
                        $"Requirement '{requirement}' depends on a design gap not explicitly accepted by the profile.");
                }
                else
                {
                    Add(report, "design_gap_accepted", "advisory", reference,
                        $"The profile explicitly accepts this documented design gap for requirement '{requirement}'.");
                }
            }
        }

        foreach (var seal in manifest.RequiredSubFeatureSeals
                     .OrderBy(value => value.Operation, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.SubFeature, StringComparer.OrdinalIgnoreCase))
        {
            var subject = $"{seal.Operation}#{seal.SubFeature}";
            if (!WorkloadManifestValidator.TryParseOperation(seal.Operation, out var service, out var operation)
                || !operationsByKey.TryGetValue(
                    WorkloadManifestValidator.OperationKey(service, operation),
                    out var sealDoc))
            {
                continue;
            }
            var subFeature = sealDoc.SubFeatures.FirstOrDefault(
                feature => feature.Name.Equals(seal.SubFeature, StringComparison.OrdinalIgnoreCase));
            if (subFeature is null)
            {
                continue;
            }
            if (!HasFreshSeal(subFeature.VerifiedRealAzure, currentDate, manifest.RealAzureSealMaxAgeDays))
            {
                hasSealBlocker = true;
                Add(
                    report,
                    subFeature.VerifiedRealAzure is null
                        ? "sub_feature_real_azure_seal_missing"
                        : "sub_feature_real_azure_seal_expired",
                    "blocking",
                    subject,
                    subFeature.VerifiedRealAzure is null
                        ? "Required backend-specific sub-feature has no real-Azure verification seal."
                        : $"Backend-specific real-Azure verification is older than {manifest.RealAzureSealMaxAgeDays} days.");
            }
        }

        if (hasCompatibilityBlocker)
        {
            report.Verdict = "blocked";
            return report;
        }
        if (hasSealBlocker)
        {
            report.Verdict = "conditional";
            return report;
        }

        var qualificationPath = manifest.Evidence.QualificationArtifact;
        if (string.IsNullOrWhiteSpace(qualificationPath))
        {
            report.Verdict = "candidate";
            Add(report, "qualification_evidence_missing", "blocking", manifest.Id,
                "No reviewed real-Azure workload qualification artifact is referenced.");
            return report;
        }

        var evidenceRoot = Path.GetFullPath(
            Path.Combine(repoRoot, "docs", "workloads", "evidence"));
        var resolvedPath = Path.GetFullPath(qualificationPath, repoRoot);
        if (Path.IsPathRooted(qualificationPath)
            || !resolvedPath.StartsWith(
                evidenceRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            report.Verdict = "candidate";
            Add(report, "qualification_evidence_path_invalid", "blocking", qualificationPath,
                "Qualification artifacts must use a repository-relative path under docs/workloads/evidence.");
            return report;
        }
        if (!File.Exists(resolvedPath))
        {
            report.Verdict = "candidate";
            Add(report, "qualification_evidence_missing", "blocking", qualificationPath,
                "The referenced workload qualification artifact does not exist.");
            return report;
        }
        if (ContainsSymbolicLink(evidenceRoot, resolvedPath))
        {
            report.Verdict = "candidate";
            Add(report, "qualification_evidence_path_invalid", "blocking", qualificationPath,
                "Qualification artifact paths must not contain symbolic links.");
            return report;
        }

        SloQualificationDocument qualification;
        try
        {
            qualification = SloQualificationLoader.Load(resolvedPath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or YamlException)
        {
            report.Verdict = "candidate";
            Add(report, "qualification_evidence_invalid", "blocking", qualificationPath,
                ToRepoRelativeMessage(exception.Message, resolvedPath, repoRoot));
            return report;
        }
        // Reports feed a committed, cross-machine artifact (docs/site/workload-ga.*);
        // an absolute filesystem path here would make regeneration non-reproducible
        // across checkouts (e.g. a contributor's sandbox vs. the CI runner), so every
        // embedded path is normalized to repo-relative immediately after load. Passing
        // qualificationPath as SloQualificationValidator's displayPath means its own
        // messages are already relative; qualification.SourceFile is mutated too so
        // any *other* direct reader (e.g. QualificationMatches below) sees the same
        // relative identifier without needing its own displayPath parameter.
        qualification.SourceFile = ToRepoRelativePath(resolvedPath, repoRoot);
        var qualificationErrors = SloQualificationValidator.Validate(
            qualification,
            currentDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc),
            qualificationPath);
        foreach (var error in qualificationErrors)
        {
            Add(report, "qualification_evidence_invalid", "blocking", qualificationPath, error);
        }
        if (qualificationErrors.Count > 0
            || !QualificationMatches(
                manifest,
                qualification,
                report,
                repoRoot,
                currentDate)
            || qualification.Verdict != "qualified")
        {
            report.Verdict = "candidate";
            if (qualification.Verdict != "qualified")
            {
                Add(report, "qualification_not_qualified", "blocking", qualificationPath,
                    $"Qualification verdict is '{qualification.Verdict}', not 'qualified'.");
            }
            return report;
        }

        report.Verdict = "ga";
        Add(report, "profile_ga", "report_only", manifest.Id,
            "Compatibility, real-Azure seals, and operational qualification gates are satisfied.");
        return report;
    }

    private static bool QualificationMatches(
        WorkloadGaManifest manifest,
        SloQualificationDocument qualification,
        WorkloadGaReport report,
        string repoRoot,
        DateOnly currentDate)
    {
        var matches = true;
        if (!qualification.Profile.Id.Equals(manifest.Id, StringComparison.Ordinal)
            || qualification.Profile.Version != manifest.Version)
        {
            matches = false;
            Add(report, "qualification_profile_mismatch", "blocking", qualification.SourceFile,
                "Qualification profile id/version does not match the manifest.");
        }

        var expectedOperations = manifest.Operations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualOperations = qualification.Profile.Services
            .SelectMany(service => service.Operations.Select(operation => service.Service + ":" + operation))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedOperations.SetEquals(actualOperations))
        {
            matches = false;
            Add(report, "qualification_operations_mismatch", "blocking", qualification.SourceFile,
                "Qualification operation set does not match the manifest.");
        }

        var actualScenarios = qualification.Scenarios
            .ToDictionary(scenario => scenario.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var scenario in manifest.Evidence.RequiredScenarios)
        {
            if (!actualScenarios.ContainsKey(scenario))
            {
                matches = false;
                Add(report, "required_scenario_missing", "blocking", scenario,
                    "Qualification does not contain this required reliability/performance scenario.");
            }
            else if (actualScenarios[scenario].EvidenceSource == "emulator")
            {
                matches = false;
                Add(report, "required_scenario_source_mismatch", "blocking", scenario,
                    "Workload qualification scenarios cannot use emulator evidence.");
            }
        }
        foreach (var scenario in manifest.Evidence.RequiredRealAzureScenarios)
        {
            if (actualScenarios.TryGetValue(scenario, out var evidence)
                && evidence.EvidenceSource != "real_azure")
            {
                matches = false;
                Add(report, "required_scenario_source_mismatch", "blocking", scenario,
                    "Qualification scenario must be backed by real-Azure evidence.");
            }
        }
        if (manifest.Evidence.RequiredScenarios.Contains("rollback", StringComparer.Ordinal))
        {
            var ledgerPath = Path.Combine(
                repoRoot,
                "docs",
                "workloads",
                "approved-runtimes",
                manifest.Id + ".yaml");
            try
            {
                var ledger = ApprovedRuntimeLedgerLoader.Load(ledgerPath);
                // Same reproducibility concern as the qualification artifact above:
                // relativize before any validator can turn this into a reported
                // finding.
                ledger.SourceFile = ToRepoRelativePath(ledgerPath, repoRoot);
                var ledgerErrors = ApprovedRuntimeLedgerValidator.Validate(
                    [ledger],
                    [manifest],
                    currentDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
                if (ledgerErrors.Count > 0)
                {
                    throw new InvalidDataException(string.Join("; ", ledgerErrors));
                }
                if (qualification.Candidate.Runtime is null)
                {
                    throw new InvalidDataException(
                        "Rollback qualification lacks a sealed candidate runtime.");
                }
                SealedRuntimeEvidenceValidator.ValidateCandidate(
                    qualification.Candidate.Runtime,
                    manifest.Id,
                    manifest.Version,
                    qualification.Candidate.GitSha,
                    qualification.Candidate.ArtifactDigest,
                    currentDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
                if (ledger.Status == "approved")
                {
                    SealedRuntimeEvidenceValidator.ValidateApprovedCandidate(
                        qualification.Candidate.Runtime,
                        ledger,
                        currentDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
                    ValidateApprovedQualification(manifest, qualification, ledger, repoRoot);
                    var rollbackTarget = ledger.Qualification!.RollbackTarget!;
                    foreach (var proof in qualification.RollbackProofs)
                    {
                        SealedRuntimeEvidenceValidator.ValidateRollbackTarget(
                            proof.Prior,
                            rollbackTarget,
                            currentDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
                    }
                }
                else
                {
                    foreach (var proof in qualification.RollbackProofs)
                    {
                        SealedRuntimeEvidenceValidator.ValidatePrior(
                            proof.Prior,
                            ledger,
                            currentDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
                    }
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException
                                              or InvalidDataException
                                              or IOException
                                              or YamlException)
            {
                matches = false;
                Add(
                    report,
                    "rollback_ledger_mismatch",
                    "blocking",
                    qualification.SourceFile,
                    ToRepoRelativeMessage(exception.Message, ledgerPath, repoRoot));
            }
        }
        return matches;
    }

    private static void ValidateApprovedQualification(
        WorkloadGaManifest manifest,
        SloQualificationDocument qualification,
        ApprovedRuntimeRecord ledger,
        string repoRoot)
    {
        var decision = ledger.Qualification
            ?? throw new InvalidDataException(
                "Approved profile ledger lacks qualification metadata.");
        var artifactDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(qualification.SourceFile, repoRoot))));
        if (decision.Artifact != manifest.Evidence.QualificationArtifact
            || decision.Digest != artifactDigest
            || decision.Verdict != qualification.Verdict
            || decision.CandidateRuntimeDigest != qualification.Candidate.ArtifactDigest
            || decision.ReviewUrl != qualification.Provenance.RunUrl
            || decision.QualifiedAt.ToUniversalTime()
                != qualification.Provenance.GeneratedAtUtc.ToUniversalTime())
        {
            throw new InvalidDataException(
                "Approved profile ledger does not exactly match the committed qualification.");
        }
    }

    private static bool HasFreshSeal(
        RealAzureVerification? verification,
        DateOnly currentDate,
        int maxAgeDays)
    {
        return verification is not null
               && DateOnly.TryParseExact(
                   verification.Date,
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var date)
               && date >= currentDate.AddDays(-maxAgeDays);
    }

    private static bool ContainsSymbolicLink(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        return false;
    }

    // Findings feed a committed, cross-machine artifact (docs/site/workload-ga.*).
    // An absolute path baked into a finding would make "regenerate and ensure no
    // diff" non-reproducible across checkouts (e.g. a contributor's sandbox path
    // differs from the CI runner's), so every reported path is repo-relative.
    private static string ToRepoRelativePath(string absolutePath, string repoRoot) =>
        Path.GetRelativePath(repoRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private static string ToRepoRelativeMessage(string message, string absolutePath, string repoRoot) =>
        message.Replace(absolutePath, ToRepoRelativePath(absolutePath, repoRoot), StringComparison.Ordinal);

    private static void Add(
        WorkloadGaReport report,
        string code,
        string disposition,
        string subject,
        string message)
    {
        report.Findings.Add(new WorkloadGaFinding
        {
            Code = code,
            Disposition = disposition,
            Subject = subject,
            Message = message,
        });
    }
}

public static class WorkloadGaRenderer
{
    public static string RenderJson(WorkloadGaReport report) =>
        JsonSerializer.Serialize(report, WorkloadGaJsonContext.Default.WorkloadGaReport);

    public static string RenderMarkdown(WorkloadGaReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Workload GA profile: {report.Name}");
        builder.AppendLine();
        builder.AppendLine($"- **Profile:** `{report.ProfileId}` v{report.ProfileVersion}");
        builder.AppendLine($"- **Minimum proxy version:** `{report.MinimumProxyVersion}`");
        builder.AppendLine($"- **Verdict:** {Badge(report.Verdict)}");
        builder.AppendLine();
        builder.AppendLine("| Disposition | Code | Subject | Reason |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var finding in report.Findings)
        {
            builder.AppendLine(
                $"| {Esc(finding.Disposition)} | `{Esc(finding.Code)}` | {Esc(finding.Subject)} | {Esc(finding.Message)} |");
        }
        return builder.ToString();
    }

    public static void RenderIndex(
        IReadOnlyList<WorkloadGaReport> reports,
        string markdownPath,
        string jsonPath)
    {
        var ordered = reports.OrderBy(report => report.ProfileId, StringComparer.Ordinal).ToList();
        var builder = new StringBuilder();
        builder.AppendLine("# Workload GA certification");
        builder.AppendLine();
        builder.AppendLine(
            "These verdicts are generated from versioned profile manifests, gap docs, real-Azure seals, and qualification artifacts.");
        builder.AppendLine();
        builder.AppendLine("Legend: ⛔ blocked · 🟡 conditional · 🔵 candidate · ✅ GA");
        builder.AppendLine();
        builder.AppendLine("| Profile | Version | Minimum proxy | Verdict | Blocking reasons |");
        builder.AppendLine("|---|---:|---|---|---|");
        foreach (var report in ordered)
        {
            var blockers = report.Findings.Count(finding => finding.Disposition == "blocking");
            builder.AppendLine(
                $"| {Esc(report.Name)} (`{report.ProfileId}`) | {report.ProfileVersion} | `{report.MinimumProxyVersion}` | {Badge(report.Verdict)} | {blockers} |");
        }
        builder.AppendLine();
        builder.AppendLine(
            "A profile reaches GA only when every required operation is compatible or explicitly accepted, every real-Azure seal is fresh, and a matching reviewed qualification artifact is `qualified`.");

        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        File.WriteAllText(markdownPath, builder.ToString());
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(ordered, WorkloadGaJsonContext.Default.ListWorkloadGaReport)
            + Environment.NewLine);
    }

    private static string Badge(string verdict) => verdict switch
    {
        "blocked" => "⛔ blocked",
        "conditional" => "🟡 conditional",
        "candidate" => "🔵 candidate",
        "ga" => "✅ GA",
        _ => verdict,
    };

    private static string Esc(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(WorkloadGaReport))]
[JsonSerializable(typeof(List<WorkloadGaReport>))]
internal sealed partial class WorkloadGaJsonContext : JsonSerializerContext;
