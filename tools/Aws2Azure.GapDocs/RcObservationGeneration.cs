using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class RcObservationPolicy
{
    public int SchemaVersion { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public int ProfileVersion { get; set; }
    public int MinimumWindowMinutes { get; set; }
    public int MaximumWindowMinutes { get; set; }
    public int MaximumEvidenceAgeHours { get; set; }
    public long MinimumSamplesPerCohort { get; set; }
    public RcObservationPolicyLoadShape LoadShape { get; set; } = new();
    public List<RcObservationPolicyMetric> Metrics { get; set; } = [];

    [YamlIgnore]
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class RcObservationPolicyLoadShape
{
    public int CandidateConcurrency { get; set; }
    public int StableConcurrency { get; set; }
    public string OperationMixIdentity { get; set; } = string.Empty;
}

public sealed class RcObservationPolicyMetric
{
    public string Id { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string ThresholdSource { get; set; } = string.Empty;
    public string ThresholdReference { get; set; } = string.Empty;
}

public static class RcObservationPolicyLoader
{
    public static IReadOnlyList<RcObservationPolicy> LoadAll(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"RC observation policy directory not found: {root}");
        }

        return Directory.EnumerateFiles(root, "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToArray();
    }

    public static RcObservationPolicy Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("RC observation policy not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        using var reader = new StreamReader(path);
        var policy = deserializer.Deserialize<RcObservationPolicy>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        policy.Metrics ??= [];
        policy.SourceFile = path;
        return policy;
    }
}

public static class RcObservationPolicyValidator
{
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<RcObservationPolicy> policies,
        IReadOnlyList<WorkloadGaManifest> workloads,
        IReadOnlyList<ApprovedRuntimeRecord> approvedRuntimes,
        string workloadsRoot)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(workloads);
        ArgumentNullException.ThrowIfNull(approvedRuntimes);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadsRoot);

        var errors = new List<string>();
        var policyKeys = new HashSet<(string Id, int Version)>();
        foreach (var policy in policies)
        {
            var source = string.IsNullOrWhiteSpace(policy.SourceFile)
                ? "RC observation policy"
                : policy.SourceFile;
            var key = (policy.ProfileId, policy.ProfileVersion);
            if (!policyKeys.Add(key))
            {
                errors.Add(
                    $"{source}: duplicate RC observation policy for " +
                    $"'{policy.ProfileId}' v{policy.ProfileVersion}");
                continue;
            }

            var workload = workloads.SingleOrDefault(item =>
                item.Id == policy.ProfileId && item.Version == policy.ProfileVersion);
            if (workload is null)
            {
                errors.Add(
                    $"{source}: no matching workload profile exists for " +
                    $"'{policy.ProfileId}' v{policy.ProfileVersion}");
                continue;
            }
            if (!approvedRuntimes.Any(item =>
                    item.Profile.Id == policy.ProfileId
                    && item.Profile.Version == policy.ProfileVersion))
            {
                errors.Add(
                    $"{source}: RC observation policy does not have a matching " +
                    "approved-runtime ledger record");
                continue;
            }

            var qualificationPath = Path.Combine(
                workloadsRoot,
                "qualification",
                policy.ProfileId + ".yaml");
            try
            {
                var qualification = WorkloadQualificationPolicyLoader.Load(
                    qualificationPath);
                RcObservationGenerator.ValidatePolicyContract(
                    policy,
                    qualification,
                    workload);
            }
            catch (Exception exception) when (exception is FileNotFoundException
                                              or InvalidDataException)
            {
                errors.Add($"{source}: {exception.Message}");
            }
        }

        var approvedKeys = approvedRuntimes
            .Select(item => (item.Profile.Id, item.Profile.Version))
            .ToHashSet();
        foreach (var missing in approvedKeys.Except(policyKeys).Order())
        {
            errors.Add(
                $"missing RC observation policy for approved runtime " +
                $"'{missing.Id}' v{missing.Version}");
        }
        foreach (var extra in policyKeys.Except(approvedKeys).Order())
        {
            errors.Add(
                $"RC observation policy '{extra.Id}' v{extra.Version} has no " +
                "approved-runtime ledger record");
        }

        return errors;
    }
}

public sealed class RcObservationCapture
{
    public int SchemaVersion { get; set; }
    public RcObservationCaptureProfile Profile { get; set; } = new();
    public RcObservationAzureEnvironment Azure { get; set; } = new();
    public RcObservationCaptureWindow Observation { get; set; } = new();
    public RcObservationCaptureLoadShape LoadShape { get; set; } = new();
    public List<RcObservationCohort> Cohorts { get; set; } = [];
    public List<RcObservationCaptureMetric> Metrics { get; set; } = [];
    public RcObservationRestoration? Restoration { get; set; }
}

public sealed class RcObservationCaptureLoadShape
{
    public int CandidateConcurrency { get; set; }
    public int StableConcurrency { get; set; }
    public string OperationMixIdentity { get; set; } = string.Empty;
}

public sealed class RcObservationCaptureProfile
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
}

public sealed class RcObservationCaptureWindow
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset MeasurementEndedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public int RequestedWindowMinutes { get; set; }
}

public sealed class RcObservationCaptureMetric
{
    public string Id { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public double CandidateValue { get; set; }
    public double StableValue { get; set; }
    public long CandidateSamples { get; set; }
    public long StableSamples { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public sealed class RcObservationCaptureArtifactSelection
{
    public int SchemaVersion { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string WorkflowPath { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public long RunId { get; set; }
    public int RunAttempt { get; set; }
    public string RunUrl { get; set; } = string.Empty;
    public string AttemptUrl { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public RcObservationArtifactIdentity Artifact { get; set; } = new();
}

public sealed class RcObservationArchiveInputSelection
{
    public int SchemaVersion { get; set; }
    public string CandidateId { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public RcObservationArchiveProducer Producer { get; set; } = new();
    public RcObservationArtifactIdentity Artifact { get; set; } = new();
    public RcObservationArchiveWorkloadIdentity Workload { get; set; } = new();
}

public sealed class RcObservationArchiveWorkloadIdentity
{
    public string ProfileId { get; set; } = string.Empty;
    public int ProfileVersion { get; set; }
    public string WorkloadManifestDigest { get; set; } = string.Empty;
    public string ApprovedRuntimeLedgerDigest { get; set; } = string.Empty;
    public string ApprovedRuntimeSourceSha { get; set; } = string.Empty;
    public string ApprovedRuntimeAggregateDigest { get; set; } = string.Empty;
    public string ApprovedRuntimeExecutableDigest { get; set; } = string.Empty;
    public RcObservationArtifactIdentity ApprovedRuntimeArtifact { get; set; } = new();
}

public sealed class RcObservationGhcrInputSelection
{
    public int SchemaVersion { get; set; }
    public string CandidateId { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public RcObservationArchiveProducer Producer { get; set; } = new();
    public RcObservationArtifactIdentity Artifact { get; set; } = new();
    public string ArchiveContentDigest { get; set; } = string.Empty;
    public RcObservationArtifactIdentity ArchiveArtifact { get; set; } = new();
    public string IndexDigest { get; set; } = string.Empty;
}

public sealed class RcObservationCanonicalIdentitySelection
{
    public int SchemaVersion { get; set; }
    public string ArtifactKind { get; set; } = string.Empty;
    public string CandidateId { get; set; } = string.Empty;
    public string IdentityDigest { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
    public string ArchiveInputsDigest { get; set; } = string.Empty;
    public string GhcrInputsDigest { get; set; } = string.Empty;
}

public static class RcObservationCaptureLoader
{
    public static RcObservationCapture Load(string path) =>
        LoadJson(
            path,
            RcObservationGenerationJsonContext.Default.RcObservationCapture)
        ?? throw new InvalidDataException($"{path}: empty RC observation capture");

    public static RcObservationCaptureArtifactSelection LoadSelection(string path) =>
        LoadJson(
            path,
            RcObservationGenerationJsonContext.Default
                .RcObservationCaptureArtifactSelection)
        ?? throw new InvalidDataException($"{path}: empty RC observation artifact selection");

    public static RcObservationArchiveInputSelection LoadArchiveSelection(string path) =>
        LoadJson(
            path,
            RcObservationGenerationJsonContext.Default
                .RcObservationArchiveInputSelection)
        ?? throw new InvalidDataException($"{path}: empty RC archive-input selection");

    public static RcObservationGhcrInputSelection LoadGhcrSelection(string path) =>
        LoadJson(
            path,
            RcObservationGenerationJsonContext.Default
                .RcObservationGhcrInputSelection)
        ?? throw new InvalidDataException($"{path}: empty RC GHCR-input selection");

    public static RcObservationCanonicalIdentitySelection LoadIdentitySelection(
        string path) =>
        LoadJson(
            path,
            RcObservationGenerationJsonContext.Default
                .RcObservationCanonicalIdentitySelection)
        ?? throw new InvalidDataException($"{path}: empty canonical RC identity selection");

    public static RcObservationValidationContext LoadBinding(string path) =>
        LoadJson(
            path,
            RcObservationGenerationJsonContext.Default.RcObservationValidationContext)
        ?? throw new InvalidDataException($"{path}: empty RC observation binding");

    private static T? LoadJson<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("RC observation JSON not found", path);
        }

        var bytes = File.ReadAllBytes(path);
        RejectDuplicateProperties(bytes, path);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        var context = new RcObservationGenerationJsonContext(options);
        var selectedTypeInfo = typeof(T) == typeof(RcObservationCapture)
            ? (JsonTypeInfo<T>)(object)context.RcObservationCapture
            : typeof(T) == typeof(RcObservationCaptureArtifactSelection)
                ? (JsonTypeInfo<T>)(object)context.RcObservationCaptureArtifactSelection
                : typeof(T) == typeof(RcObservationArchiveInputSelection)
                    ? (JsonTypeInfo<T>)(object)context.RcObservationArchiveInputSelection
                    : typeof(T) == typeof(RcObservationGhcrInputSelection)
                        ? (JsonTypeInfo<T>)(object)context.RcObservationGhcrInputSelection
                        : typeof(T) == typeof(RcObservationCanonicalIdentitySelection)
                            ? (JsonTypeInfo<T>)(object)context
                                .RcObservationCanonicalIdentitySelection
                            : (JsonTypeInfo<T>)(object)context
                                .RcObservationValidationContext;
        return JsonSerializer.Deserialize(bytes, selectedTypeInfo);
    }

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
}

public sealed record RcObservationGenerationInput
{
    public string ReleaseCandidateId { get; init; } = string.Empty;
    public string DecisionOwner { get; init; } = string.Empty;
    public string CandidateIdentityDigest { get; init; } = string.Empty;
    public string PriorIdentityDigest { get; init; } = string.Empty;
    public string ApprovedRuntimeLedgerDigest { get; init; } = string.Empty;
    public string WorkloadManifestDigest { get; init; } = string.Empty;
    public string QualificationPolicyDigest { get; init; } = string.Empty;
    public string ObservationPolicyDigest { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

public sealed record RcObservationGenerationResult(
    RcObservationEvidence Evidence,
    RcObservationValidationContext Binding);

public static class RcObservationGenerator
{
    public static void ValidatePolicyContract(
        RcObservationPolicy policy,
        WorkloadQualificationPolicy qualificationPolicy,
        WorkloadGaManifest workloadManifest)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(qualificationPolicy);
        ArgumentNullException.ThrowIfNull(workloadManifest);
        _ = ResolvePolicy(policy, qualificationPolicy, workloadManifest);
    }

    public static RcObservationGenerationResult Generate(
        RcObservationCapture capture,
        RcObservationPolicy policy,
        WorkloadQualificationPolicy qualificationPolicy,
        WorkloadGaManifest workloadManifest,
        QualificationSealedRuntimeIdentity candidate,
        QualificationSealedRuntimeIdentity prior,
        ApprovedRuntimeRecord approvedRuntime,
        RcObservationArchiveInputSelection archiveSelection,
        RcObservationGhcrInputSelection ghcrSelection,
        RcObservationCanonicalIdentitySelection identitySelection,
        RcObservationCaptureArtifactSelection selection,
        RcObservationGenerationInput input)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(qualificationPolicy);
        ArgumentNullException.ThrowIfNull(workloadManifest);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(approvedRuntime);
        ArgumentNullException.ThrowIfNull(archiveSelection);
        ArgumentNullException.ThrowIfNull(ghcrSelection);
        ArgumentNullException.ThrowIfNull(identitySelection);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(input);

        var generatedAt = input.GeneratedAtUtc.ToUniversalTime();
        ValidateRuntimeSelection(candidate, prior, approvedRuntime, generatedAt);
        ValidateArchiveSelection(
            archiveSelection,
            approvedRuntime,
            candidate,
            input);
        ValidateGhcrSelection(ghcrSelection, archiveSelection, candidate, input);
        ValidateIdentitySelection(
            identitySelection,
            archiveSelection,
            ghcrSelection,
            input);
        var resolvedPolicy = ResolvePolicy(
            policy,
            qualificationPolicy,
            workloadManifest);
        ValidateCapture(
            capture,
            policy,
            candidate,
            prior,
            selection,
            input,
            qualificationPolicy,
            workloadManifest,
            resolvedPolicy);

        var metrics = resolvedPolicy.Select(item =>
        {
            var measured = capture.Metrics.Single(metric => metric.Id == item.Id);
            var breached = item.Comparison switch
            {
                "greater_than_or_equal" => measured.CandidateValue < item.Threshold,
                "less_than_or_equal" => measured.CandidateValue > item.Threshold,
                _ => throw new InvalidDataException(
                    $"Unsupported RC observation comparison '{item.Comparison}'."),
            };
            return new RcObservationMetric
            {
                Id = item.Id,
                Unit = item.Unit,
                Comparison = item.Comparison,
                Threshold = item.Threshold,
                CandidateValue = measured.CandidateValue,
                StableValue = measured.StableValue,
                CandidateSamples = measured.CandidateSamples,
                StableSamples = measured.StableSamples,
                Samples = Math.Min(measured.CandidateSamples, measured.StableSamples),
                CapturedAtUtc = measured.CapturedAtUtc.ToUniversalTime(),
                Result = breached ? "breach" : "pass",
            };
        }).ToArray();
        var breachedIds = metrics
            .Where(metric => metric.Result == "breach")
            .Select(metric => metric.Id)
            .ToArray();
        var verdict = breachedIds.Length == 0 ? "pass" : "rollback";
        var reason = verdict == "pass"
            ? "All reviewed profile thresholds passed for the complete observation window."
            : "Rollback triggers fired: " + string.Join(", ", breachedIds);
        var evidenceEndedAt = verdict == "rollback"
            ? capture.Observation.EndedAtUtc
            : capture.Observation.MeasurementEndedAtUtc;
        var cohorts = capture.Cohorts.Select(cohort => cohort with
        {
            ObservedUntilUtc = cohort.Role == "candidate" && verdict == "rollback"
                ? capture.Restoration!.StartedAtUtc
                : evidenceEndedAt,
        }).ToArray();

        var evidence = new RcObservationEvidence
        {
            SchemaVersion = RcObservationValidator.CurrentSchemaVersion,
            ArtifactKind = "rc_observation",
            ReleaseCandidate = new RcObservationReleaseCandidate
            {
                Id = input.ReleaseCandidateId,
                ManifestDigest = identitySelection.IdentityDigest,
                SourceSha = candidate.Source.Sha,
                ArchiveInputs = new RcObservationArchiveInputsIdentity
                {
                    ContentDigest = archiveSelection.ContentDigest,
                    Producer = archiveSelection.Producer,
                    Artifact = archiveSelection.Artifact,
                },
                GhcrInputs = new RcObservationGhcrInputsIdentity
                {
                    ContentDigest = ghcrSelection.ContentDigest,
                    Producer = ghcrSelection.Producer,
                    Artifact = ghcrSelection.Artifact,
                    IndexDigest = ghcrSelection.IndexDigest,
                },
            },
            Candidate = new RcObservationRuntimeIdentity
            {
                IdentityDigest = input.CandidateIdentityDigest,
                RuntimeDigest = candidate.Runtime.AggregateDigest,
                SourceSha = candidate.Source.Sha,
            },
            Prior = new RcObservationRuntimeIdentity
            {
                IdentityDigest = input.PriorIdentityDigest,
                RuntimeDigest = prior.Runtime.AggregateDigest,
                SourceSha = prior.Source.Sha,
            },
            Profile = new RcObservationProfile
            {
                Id = capture.Profile.Id,
                Version = capture.Profile.Version,
            },
            Policy = new RcObservationPolicyIdentity
            {
                WorkloadManifestDigest = input.WorkloadManifestDigest,
                QualificationPolicyDigest = input.QualificationPolicyDigest,
                ObservationPolicyDigest = input.ObservationPolicyDigest,
            },
            Azure = capture.Azure,
            Producer = new RcObservationProducer
            {
                Repository = selection.Repository,
                WorkflowPath = selection.WorkflowPath,
                EventName = selection.EventName,
                RunId = selection.RunId,
                RunAttempt = selection.RunAttempt,
                RunUrl = selection.RunUrl,
                AttemptUrl = selection.AttemptUrl,
                SourceSha = selection.SourceSha,
                SourceRef = selection.SourceRef,
            },
            CaptureArtifact = selection.Artifact,
            Observation = new RcObservationWindow
            {
                StartedAtUtc = capture.Observation.StartedAtUtc.ToUniversalTime(),
                EndedAtUtc = evidenceEndedAt.ToUniversalTime(),
                GeneratedAtUtc = generatedAt,
                MinimumWindowMinutes = policy.MinimumWindowMinutes,
            },
            LoadShape = new RcObservationLoadShape
            {
                CandidateConcurrency = capture.LoadShape.CandidateConcurrency,
                StableConcurrency = capture.LoadShape.StableConcurrency,
                OperationMixIdentity = capture.LoadShape.OperationMixIdentity,
            },
            Cohorts = cohorts,
            Metrics = metrics,
            RollbackTriggers = metrics.Select(metric => new RcObservationRollbackTrigger
            {
                Id = "rollback-on-" + metric.Id,
                MetricId = metric.Id,
                Status = metric.Result == "breach" ? "fired" : "armed",
                OverrideApplied = false,
                Suppressed = false,
            }).ToArray(),
            Decision = new RcObservationDecision
            {
                Verdict = verdict,
                Owner = input.DecisionOwner,
                Reason = reason,
                DecidedAtUtc = generatedAt,
            },
            Restoration = verdict == "rollback" ? capture.Restoration : null,
        };
        evidence = evidence with
        {
            EvidenceDigest = RcObservationIntegrity.ComputePayloadDigest(evidence),
        };

        var binding = new RcObservationValidationContext
        {
            ExpectedEvidenceDigest = evidence.EvidenceDigest,
            ReleaseCandidateId = input.ReleaseCandidateId,
            RcManifestDigest = identitySelection.IdentityDigest,
            ArchiveInputsDigest = archiveSelection.ContentDigest,
            ArchiveProducerRepository = archiveSelection.Producer.Repository,
            ArchiveProducerWorkflowPath = archiveSelection.Producer.WorkflowPath,
            ArchiveProducerRunId = archiveSelection.Producer.RunId,
            ArchiveProducerRunAttempt = archiveSelection.Producer.RunAttempt,
            ArchiveProducerSourceSha = archiveSelection.Producer.SourceSha,
            ArchiveProducerSourceRef = archiveSelection.Producer.SourceRef,
            ArchiveArtifactId = archiveSelection.Artifact.Id,
            ArchiveArtifactName = archiveSelection.Artifact.Name,
            ArchiveArtifactUploadDigest = archiveSelection.Artifact.UploadDigest,
            GhcrInputsDigest = ghcrSelection.ContentDigest,
            GhcrProducerRepository = ghcrSelection.Producer.Repository,
            GhcrProducerWorkflowPath = ghcrSelection.Producer.WorkflowPath,
            GhcrProducerRunId = ghcrSelection.Producer.RunId,
            GhcrProducerRunAttempt = ghcrSelection.Producer.RunAttempt,
            GhcrProducerSourceSha = ghcrSelection.Producer.SourceSha,
            GhcrProducerSourceRef = ghcrSelection.Producer.SourceRef,
            GhcrArtifactId = ghcrSelection.Artifact.Id,
            GhcrArtifactName = ghcrSelection.Artifact.Name,
            GhcrArtifactUploadDigest = ghcrSelection.Artifact.UploadDigest,
            GhcrIndexDigest = ghcrSelection.IndexDigest,
            CandidateSourceSha = candidate.Source.Sha,
            CandidateIdentityDigest = input.CandidateIdentityDigest,
            CandidateRuntimeDigest = candidate.Runtime.AggregateDigest,
            PriorSourceSha = prior.Source.Sha,
            PriorIdentityDigest = input.PriorIdentityDigest,
            PriorRuntimeDigest = prior.Runtime.AggregateDigest,
            ProfileId = capture.Profile.Id,
            ProfileVersion = capture.Profile.Version,
            WorkloadManifestDigest = input.WorkloadManifestDigest,
            QualificationPolicyDigest = input.QualificationPolicyDigest,
            ObservationPolicyDigest = input.ObservationPolicyDigest,
            AzureBackendKind = capture.Azure.BackendKind,
            AzureRegion = capture.Azure.Region,
            AzureBackendIdentityDigest = capture.Azure.BackendIdentityDigest,
            ConfigDigest = capture.Azure.ConfigDigest,
            AwsBindingDigest = capture.Azure.AwsBindingDigest,
            ProducerRepository = selection.Repository,
            ProducerWorkflowPath = selection.WorkflowPath,
            ProducerRunId = selection.RunId,
            ProducerRunAttempt = selection.RunAttempt,
            ProducerSourceSha = selection.SourceSha,
            ProducerSourceRef = selection.SourceRef,
            CaptureArtifactId = selection.Artifact.Id,
            CaptureArtifactName = selection.Artifact.Name,
            CaptureArtifactUploadDigest = selection.Artifact.UploadDigest,
            MinimumWindowMinutes = policy.MinimumWindowMinutes,
            MaximumEvidenceAge = TimeSpan.FromHours(policy.MaximumEvidenceAgeHours),
        };
        var errors = RcObservationValidator.Validate(evidence, binding, generatedAt);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
        return new RcObservationGenerationResult(evidence, binding);
    }

    private static void ValidateRuntimeSelection(
        QualificationSealedRuntimeIdentity candidate,
        QualificationSealedRuntimeIdentity prior,
        ApprovedRuntimeRecord approvedRuntime,
        DateTimeOffset now)
    {
        SealedRuntimeEvidenceValidator.ValidateApprovedCandidate(
            candidate,
            approvedRuntime,
            now);
        var trustedPrior = approvedRuntime.Qualification?.RollbackTarget
            ?? throw new InvalidDataException(
                "Approved runtime does not contain a trusted rollback target.");
        SealedRuntimeEvidenceValidator.ValidateRollbackTarget(prior, trustedPrior, now);
    }

    private static void ValidateArchiveSelection(
        RcObservationArchiveInputSelection archive,
        ApprovedRuntimeRecord approvedRuntime,
        QualificationSealedRuntimeIdentity candidate,
        RcObservationGenerationInput input)
    {
        var producer = archive.Producer;
        var expectedAttemptUrl =
            $"https://github.com/{candidate.Source.Repository}/actions/runs/" +
            $"{producer.RunId}/attempts/{producer.RunAttempt}";
        var contentDigestHex = IsDigest(archive.ContentDigest)
            ? archive.ContentDigest["sha256:".Length..]
            : string.Empty;
        var expectedArtifactName =
            $"aws2azure-rc-archives-{input.ReleaseCandidateId}-" +
            $"{contentDigestHex}-run-{producer.RunId}-" +
            $"attempt-{producer.RunAttempt}";
        var workload = archive.Workload;
        var approvedArtifact = workload.ApprovedRuntimeArtifact;
        if (archive.SchemaVersion != 1
            || archive.CandidateId != input.ReleaseCandidateId
            || !IsDigest(archive.ContentDigest)
            || archive.SourceSha != candidate.Source.Sha
            || archive.SourceRef != "refs/tags/" + input.ReleaseCandidateId
            || producer.Repository != candidate.Source.Repository
            || producer.WorkflowPath != ".github/workflows/release-candidate.yml"
            || producer.EventName != "workflow_dispatch"
            || producer.RunId <= 0
            || producer.RunAttempt <= 0
            || producer.AttemptUrl != expectedAttemptUrl
            || !IsGitSha(producer.SourceSha)
            || producer.SourceRef != "refs/heads/main"
            || archive.Artifact.Id <= 0
            || archive.Artifact.Name != expectedArtifactName
            || !IsDigest(archive.Artifact.UploadDigest)
            || workload.ProfileId != approvedRuntime.Profile.Id
            || workload.ProfileVersion != approvedRuntime.Profile.Version
            || workload.WorkloadManifestDigest != input.WorkloadManifestDigest
            || workload.ApprovedRuntimeLedgerDigest !=
                input.ApprovedRuntimeLedgerDigest
            || workload.ApprovedRuntimeSourceSha != approvedRuntime.Runtime.SourceSha
            || workload.ApprovedRuntimeAggregateDigest
                != approvedRuntime.Runtime.AggregateDigest
            || workload.ApprovedRuntimeExecutableDigest
                != approvedRuntime.Runtime.ExecutableDigest
            || approvedArtifact.Id != approvedRuntime.Artifact.Id
            || approvedArtifact.Name != approvedRuntime.Artifact.Name
            || approvedArtifact.UploadDigest != approvedRuntime.Artifact.UploadDigest)
        {
            throw new InvalidDataException(
                "RC archive inputs do not match the exact trusted candidate, " +
                "approved runtime, workflow attempt, and artifact.");
        }
    }

    private static void ValidateGhcrSelection(
        RcObservationGhcrInputSelection ghcr,
        RcObservationArchiveInputSelection archive,
        QualificationSealedRuntimeIdentity candidate,
        RcObservationGenerationInput input)
    {
        var producer = ghcr.Producer;
        var expectedAttemptUrl =
            $"https://github.com/{candidate.Source.Repository}/actions/runs/" +
            $"{producer.RunId}/attempts/{producer.RunAttempt}";
        var contentDigestHex = IsDigest(ghcr.ContentDigest)
            ? ghcr.ContentDigest["sha256:".Length..]
            : string.Empty;
        var expectedArtifactName =
            $"aws2azure-rc-ghcr-{input.ReleaseCandidateId}-" +
            $"{contentDigestHex}-run-{producer.RunId}-" +
            $"attempt-{producer.RunAttempt}";
        if (ghcr.SchemaVersion != 1
            || ghcr.CandidateId != input.ReleaseCandidateId
            || !IsDigest(ghcr.ContentDigest)
            || ghcr.SourceSha != candidate.Source.Sha
            || producer.Repository != candidate.Source.Repository
            || producer.WorkflowPath != ".github/workflows/release-candidate-image.yml"
            || producer.EventName != "workflow_dispatch"
            || producer.RunId <= 0
            || producer.RunAttempt <= 0
            || producer.AttemptUrl != expectedAttemptUrl
            || !IsGitSha(producer.SourceSha)
            || producer.SourceRef != "refs/heads/main"
            || ghcr.Artifact.Id <= 0
            || ghcr.Artifact.Name != expectedArtifactName
            || !IsDigest(ghcr.Artifact.UploadDigest)
            || ghcr.ArchiveContentDigest != archive.ContentDigest
            || ghcr.ArchiveArtifact.Id != archive.Artifact.Id
            || ghcr.ArchiveArtifact.Name != archive.Artifact.Name
            || ghcr.ArchiveArtifact.UploadDigest != archive.Artifact.UploadDigest
            || !IsDigest(ghcr.IndexDigest))
        {
            throw new InvalidDataException(
                "RC GHCR inputs do not match the exact candidate, archive, " +
                "protected-main producer attempt, artifact, and index.");
        }
    }

    private static void ValidateIdentitySelection(
        RcObservationCanonicalIdentitySelection identity,
        RcObservationArchiveInputSelection archive,
        RcObservationGhcrInputSelection ghcr,
        RcObservationGenerationInput input)
    {
        if (identity.SchemaVersion != 1
            || identity.ArtifactKind != "release_candidate_identity"
            || identity.CandidateId != input.ReleaseCandidateId
            || !IsDigest(identity.IdentityDigest)
            || !IsDigest(identity.ContentDigest)
            || identity.ArchiveInputsDigest != archive.ContentDigest
            || identity.GhcrInputsDigest != ghcr.ContentDigest)
        {
            throw new InvalidDataException(
                "Canonical RC identity does not match the exact archive and GHCR interfaces.");
        }
    }

    private static IReadOnlyList<ResolvedMetric> ResolvePolicy(
        RcObservationPolicy policy,
        WorkloadQualificationPolicy qualification,
        WorkloadGaManifest workload)
    {
        var blockingSignals = qualification.Scenarios
            .SelectMany(scenario => scenario.Signals)
            .Where(signal => signal.Disposition == "blocking")
            .ToArray();
        var configuredSignalReferences = policy.Metrics
            .Where(metric => metric.ThresholdSource == "qualification_signal")
            .Select(metric => metric.ThresholdReference)
            .ToArray();
        var configuredFailureRules = policy.Metrics.Count(metric =>
            metric.ThresholdSource == "qualification_rule"
            && metric.ThresholdReference == "max_failure_rate"
            && metric.Unit == "ratio");
        if (policy.SchemaVersion != 1
            || policy.ProfileId != qualification.ProfileId
            || policy.ProfileVersion != qualification.ProfileVersion
            || policy.ProfileId != workload.Id
            || policy.ProfileVersion != workload.Version
            || policy.MinimumWindowMinutes < 60
            || policy.MaximumWindowMinutes < policy.MinimumWindowMinutes
            || policy.MaximumWindowMinutes > 180
            || policy.MaximumEvidenceAgeHours != qualification.Rules.MaxArtifactAgeHours
            || policy.MinimumSamplesPerCohort != qualification.Rules.MinSamplesPerScenario
            || policy.LoadShape.CandidateConcurrency <= 0
            || policy.LoadShape.StableConcurrency <= 0
            || !IsDigest(policy.LoadShape.OperationMixIdentity)
            || policy.Metrics.Count == 0
            || policy.Metrics.Select(metric => metric.Id)
                .Distinct(StringComparer.Ordinal).Count() != policy.Metrics.Count
            || configuredSignalReferences.Length != blockingSignals.Length
            || configuredSignalReferences
                .Distinct(StringComparer.Ordinal).Count()
                != configuredSignalReferences.Length
            || blockingSignals.Any(signal =>
                configuredSignalReferences.Count(reference =>
                    reference == signal.Id) != 1)
            || configuredFailureRules != 1
            || policy.Metrics.Count != blockingSignals.Length + 1)
        {
            throw new InvalidDataException(
                "RC observation policy does not match the reviewed workload qualification policy.");
        }

        var resolved = new List<ResolvedMetric>(policy.Metrics.Count);
        foreach (var metric in policy.Metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Id)
                || string.IsNullOrWhiteSpace(metric.Unit))
            {
                throw new InvalidDataException("RC observation metric identity is incomplete.");
            }

            switch (metric.ThresholdSource)
            {
                case "qualification_signal":
                {
                    var signals = qualification.Scenarios
                        .SelectMany(scenario => scenario.Signals)
                        .Where(signal =>
                            signal.Id == metric.ThresholdReference
                            && signal.Disposition == "blocking")
                        .ToArray();
                    if (signals.Length != 1 || signals[0].Metric != metric.Unit)
                    {
                        throw new InvalidDataException(
                            $"RC observation metric '{metric.Id}' does not resolve one blocking qualification signal.");
                    }
                    var signal = signals[0];
                    if (signal.MinValue is double min && signal.MaxValue is null)
                    {
                        resolved.Add(new ResolvedMetric(
                            metric.Id,
                            metric.Unit,
                            "greater_than_or_equal",
                            min));
                    }
                    else if (signal.MaxValue is double max && signal.MinValue is null)
                    {
                        resolved.Add(new ResolvedMetric(
                            metric.Id,
                            metric.Unit,
                            "less_than_or_equal",
                            max));
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"RC observation metric '{metric.Id}' has an ambiguous qualification threshold.");
                    }
                    break;
                }
                case "qualification_rule"
                    when metric.ThresholdReference == "max_failure_rate"
                         && metric.Unit == "ratio":
                    resolved.Add(new ResolvedMetric(
                        metric.Id,
                        metric.Unit,
                        "less_than_or_equal",
                        qualification.Rules.MaxFailureRate));
                    break;
                default:
                    throw new InvalidDataException(
                        $"RC observation metric '{metric.Id}' has an unknown threshold source.");
            }
        }
        return resolved;
    }

    private static void ValidateCapture(
        RcObservationCapture capture,
        RcObservationPolicy policy,
        QualificationSealedRuntimeIdentity candidate,
        QualificationSealedRuntimeIdentity prior,
        RcObservationCaptureArtifactSelection selection,
        RcObservationGenerationInput input,
        WorkloadQualificationPolicy qualification,
        WorkloadGaManifest workload,
        IReadOnlyList<ResolvedMetric> resolvedPolicy)
    {
        if (capture.SchemaVersion != 1
            || capture.Profile.Id != policy.ProfileId
            || capture.Profile.Version != policy.ProfileVersion
            || capture.LoadShape.CandidateConcurrency
                != policy.LoadShape.CandidateConcurrency
            || capture.LoadShape.StableConcurrency
                != policy.LoadShape.StableConcurrency
            || capture.LoadShape.OperationMixIdentity
                != policy.LoadShape.OperationMixIdentity
            || capture.Observation.RequestedWindowMinutes < policy.MinimumWindowMinutes
            || capture.Observation.RequestedWindowMinutes > policy.MaximumWindowMinutes
            || capture.Observation.MeasurementEndedAtUtc
                - capture.Observation.StartedAtUtc
                < TimeSpan.FromMinutes(capture.Observation.RequestedWindowMinutes)
            || capture.Observation.EndedAtUtc < capture.Observation.MeasurementEndedAtUtc
            || capture.Restoration is null
            || !capture.Restoration.Verified
            || capture.Restoration.RuntimeIdentityDigest != input.PriorIdentityDigest
            || capture.Restoration.RuntimeDigest != prior.Runtime.AggregateDigest
            || capture.Restoration.BackendIdentityDigest
                != capture.Azure.BackendIdentityDigest
            || capture.Restoration.ConfigDigest != capture.Azure.ConfigDigest
            || capture.Restoration.AwsBindingDigest != capture.Azure.AwsBindingDigest
            || capture.Restoration.StartedAtUtc <
                capture.Observation.MeasurementEndedAtUtc
            || capture.Restoration.VerifiedAtUtc <= capture.Restoration.StartedAtUtc
            || capture.Restoration.VerifiedAtUtc != capture.Observation.EndedAtUtc
            || capture.Cohorts.Count != 2
            || capture.Metrics.Count != resolvedPolicy.Count
            || capture.Metrics.Select(metric => metric.Id)
                .Distinct(StringComparer.Ordinal).Count() != capture.Metrics.Count)
        {
            throw new InvalidDataException(
                "RC observation capture is incomplete, inconsistent, or outside policy bounds.");
        }
        if (selection.SchemaVersion != 1
            || selection.ProfileId != capture.Profile.Id
            || selection.Repository != candidate.Source.Repository
            || selection.WorkflowPath != ".github/workflows/rc-observation-real-azure.yml"
            || selection.EventName != "workflow_dispatch"
            || !IsGitSha(selection.SourceSha)
            || selection.SourceRef != "refs/heads/main"
            || selection.RunId <= 0
            || selection.RunAttempt <= 0
            || selection.RunUrl !=
                $"https://github.com/{selection.Repository}/actions/runs/{selection.RunId}"
            || selection.AttemptUrl !=
                selection.RunUrl + "/attempts/" + selection.RunAttempt
            || selection.Artifact.Id <= 0
            || selection.Artifact.Name !=
                "real-azure-rc-observation-capture-" + capture.Profile.Id +
                "-run-" + selection.RunId.ToString(CultureInfo.InvariantCulture) +
                "-attempt-" +
                selection.RunAttempt.ToString(CultureInfo.InvariantCulture)
            || !IsDigest(selection.Artifact.UploadDigest))
        {
            throw new InvalidDataException(
                "RC observation capture artifact selection is not the exact trusted workflow upload.");
        }
        if (!IsDigest(input.CandidateIdentityDigest)
            || !IsDigest(input.PriorIdentityDigest)
            || !IsDigest(input.ApprovedRuntimeLedgerDigest)
            || !IsDigest(input.WorkloadManifestDigest)
            || !IsDigest(input.QualificationPolicyDigest)
            || !IsDigest(input.ObservationPolicyDigest)
            || string.IsNullOrWhiteSpace(input.DecisionOwner))
        {
            throw new InvalidDataException("RC observation generation input is incomplete.");
        }

        foreach (var expected in resolvedPolicy)
        {
            var measured = capture.Metrics.SingleOrDefault(metric => metric.Id == expected.Id);
            if (measured is null
                || measured.Unit != expected.Unit
                || !double.IsFinite(measured.CandidateValue)
                || !double.IsFinite(measured.StableValue)
                || measured.CandidateSamples < policy.MinimumSamplesPerCohort
                || measured.StableSamples < policy.MinimumSamplesPerCohort
                || measured.CapturedAtUtc < capture.Observation.StartedAtUtc
                || measured.CapturedAtUtc > capture.Observation.MeasurementEndedAtUtc)
            {
                throw new InvalidDataException(
                    $"RC observation capture metric '{expected.Id}' is incomplete or below sample policy.");
            }
        }

        var candidateCohort = capture.Cohorts.SingleOrDefault(cohort =>
            cohort.Role == "candidate");
        var stableCohort = capture.Cohorts.SingleOrDefault(cohort =>
            cohort.Role == "stable");
        if (candidateCohort is null
            || stableCohort is null
            || candidateCohort.MemberDigests.Count
                != capture.LoadShape.CandidateConcurrency
            || stableCohort.MemberDigests.Count
                != capture.LoadShape.StableConcurrency
            || candidateCohort.RuntimeIdentityDigest != input.CandidateIdentityDigest
            || candidateCohort.RuntimeDigest != candidate.Runtime.AggregateDigest
            || stableCohort.RuntimeIdentityDigest != input.PriorIdentityDigest
            || stableCohort.RuntimeDigest != prior.Runtime.AggregateDigest)
        {
            throw new InvalidDataException(
                "RC observation capture cohorts do not match the exact selected runtimes.");
        }
        ValidateCaptureDiagnostics(
            capture,
            qualification,
            workload,
            candidateCohort,
            stableCohort);
    }

    private static void ValidateCaptureDiagnostics(
        RcObservationCapture capture,
        WorkloadQualificationPolicy qualification,
        WorkloadGaManifest workload,
        RcObservationCohort candidateCohort,
        RcObservationCohort stableCohort)
    {
        var failureMetric = capture.Metrics.SingleOrDefault(metric =>
            metric.Id == "operation-failure-rate");
        var throughputMetric = capture.Metrics.SingleOrDefault(metric =>
            metric.Id == "representative-load-throughput");
        var representativeScenario = qualification.Scenarios.SingleOrDefault(scenario =>
            scenario.Id == "representative-load");
        if (failureMetric is null
            || throughputMetric is null
            || representativeScenario is null)
        {
            throw new InvalidDataException(
                "RC observation capture is missing required aggregate diagnostics.");
        }

        var expectedOperations = workload.Operations
            .Select(ParseOperationReference)
            .OrderBy(item => item.Service, StringComparer.Ordinal)
            .ThenBy(item => item.Operation, StringComparer.Ordinal)
            .ToArray();
        if (expectedOperations.Length == 0)
        {
            throw new InvalidDataException(
                "RC observation workload does not declare attributable operations.");
        }

        ValidateCohortDiagnostics(
            candidateCohort,
            expectedOperations,
            representativeScenario.Service,
            representativeScenario.Operation,
            failureMetric.CandidateSamples,
            failureMetric.CandidateValue,
            throughputMetric.CandidateSamples);
        ValidateCohortDiagnostics(
            stableCohort,
            expectedOperations,
            representativeScenario.Service,
            representativeScenario.Operation,
            failureMetric.StableSamples,
            failureMetric.StableValue,
            throughputMetric.StableSamples);
    }

    private static void ValidateCohortDiagnostics(
        RcObservationCohort cohort,
        IReadOnlyList<(string Service, string Operation)> expectedOperations,
        string representativeService,
        string representativeOperation,
        long expectedFailureSamples,
        double expectedFailureRate,
        long expectedThroughputSamples)
    {
        if (cohort.OperationDiagnostics.Count != expectedOperations.Count)
        {
            throw new InvalidDataException(
                $"RC observation cohort '{cohort.Id}' diagnostics do not cover every operation.");
        }

        var diagnostics = new Dictionary<string, RcObservationOperationDiagnostic>(
            StringComparer.Ordinal);
        foreach (var diagnostic in cohort.OperationDiagnostics)
        {
            if (diagnostic.Completions < 0
                || diagnostic.Failures < 0
                || diagnostic.Throttles < 0
                || diagnostic.Throttles > diagnostic.Failures
                || (diagnostic.Failures == 0) != (diagnostic.FirstFailure is null)
                || (diagnostic.FirstFailure is not null
                    && !IsSanitizedFirstFailure(diagnostic.FirstFailure)))
            {
                throw new InvalidDataException(
                    $"RC observation cohort '{cohort.Id}' contains malformed operation diagnostics.");
            }
            if (!diagnostics.TryAdd(
                    diagnostic.Service + ":" + diagnostic.Operation,
                    diagnostic))
            {
                throw new InvalidDataException(
                    $"RC observation cohort '{cohort.Id}' has duplicate operation diagnostics.");
            }
        }

        foreach (var expected in expectedOperations)
        {
            if (!diagnostics.ContainsKey(expected.Service + ":" + expected.Operation))
            {
                throw new InvalidDataException(
                    $"RC observation cohort '{cohort.Id}' diagnostics omit {expected.Service}:{expected.Operation}.");
            }
        }

        var completions = cohort.OperationDiagnostics.Sum(item => item.Completions);
        var failures = cohort.OperationDiagnostics.Sum(item => item.Failures);
        var attempts = completions + failures;
        if (attempts != expectedFailureSamples
            || !NearlyEqual(attempts == 0 ? 1 : (double)failures / attempts, expectedFailureRate))
        {
            throw new InvalidDataException(
                $"RC observation cohort '{cohort.Id}' diagnostics do not match aggregate failure-rate samples.");
        }

        var representative = diagnostics[
            representativeService + ":" + representativeOperation];
        if (representative.Completions + representative.Failures != expectedThroughputSamples)
        {
            throw new InvalidDataException(
                $"RC observation cohort '{cohort.Id}' diagnostics do not match representative throughput samples.");
        }
    }

    private static (string Service, string Operation) ParseOperationReference(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new InvalidDataException(
                $"RC observation workload operation reference '{value}' is invalid.");
        }
        return (value[..separator], value[(separator + 1)..]);
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-12;

    private static bool IsSanitizedFirstFailure(RcObservationFirstFailure failure) =>
        IsSafeToken(failure.Category)
        && (failure.StatusCode is null or >= 100 and <= 599)
        && IsSafeToken(failure.ErrorCode);

    private static bool IsSafeToken(string value) =>
        value.Length is > 0 and <= 128
        && value.AsSpan().IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-") < 0;

    private static bool IsDigest(string value) =>
        value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool IsGitSha(string value) =>
        value.Length == 40
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private sealed record ResolvedMetric(
        string Id,
        string Unit,
        string Comparison,
        double Threshold);
}

public static class RcObservationRenderer
{
    public static void Render(RcObservationEvidence evidence, string path)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var builder = new StringBuilder(8192);
        Line(builder, 0, "schema_version", evidence.SchemaVersion);
        Line(builder, 0, "artifact_kind", evidence.ArtifactKind);
        Line(builder, 0, "evidence_digest", evidence.EvidenceDigest);
        builder.AppendLine("release_candidate:");
        Line(builder, 1, "id", evidence.ReleaseCandidate.Id);
        Line(builder, 1, "manifest_digest", evidence.ReleaseCandidate.ManifestDigest);
        Line(builder, 1, "source_sha", evidence.ReleaseCandidate.SourceSha);
        builder.Append(' ', 2).AppendLine("archive_inputs:");
        Line(
            builder,
            2,
            "content_digest",
            evidence.ReleaseCandidate.ArchiveInputs.ContentDigest);
        builder.Append(' ', 4).AppendLine("producer:");
        Line(
            builder,
            3,
            "repository",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.Repository);
        Line(
            builder,
            3,
            "workflow_path",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.WorkflowPath);
        Line(
            builder,
            3,
            "event_name",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.EventName);
        Line(
            builder,
            3,
            "run_id",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.RunId);
        Line(
            builder,
            3,
            "run_attempt",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.RunAttempt);
        Line(
            builder,
            3,
            "attempt_url",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.AttemptUrl);
        Line(
            builder,
            3,
            "source_sha",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceSha);
        Line(
            builder,
            3,
            "source_ref",
            evidence.ReleaseCandidate.ArchiveInputs.Producer.SourceRef);
        builder.Append(' ', 4).AppendLine("artifact:");
        Line(builder, 3, "id", evidence.ReleaseCandidate.ArchiveInputs.Artifact.Id);
        Line(builder, 3, "name", evidence.ReleaseCandidate.ArchiveInputs.Artifact.Name);
        Line(
            builder,
            3,
            "upload_digest",
            evidence.ReleaseCandidate.ArchiveInputs.Artifact.UploadDigest);
        builder.Append(' ', 2).AppendLine("ghcr_inputs:");
        Line(
            builder,
            2,
            "content_digest",
            evidence.ReleaseCandidate.GhcrInputs.ContentDigest);
        builder.Append(' ', 4).AppendLine("producer:");
        Line(builder, 3, "repository", evidence.ReleaseCandidate.GhcrInputs.Producer.Repository);
        Line(
            builder,
            3,
            "workflow_path",
            evidence.ReleaseCandidate.GhcrInputs.Producer.WorkflowPath);
        Line(builder, 3, "event_name", evidence.ReleaseCandidate.GhcrInputs.Producer.EventName);
        Line(builder, 3, "run_id", evidence.ReleaseCandidate.GhcrInputs.Producer.RunId);
        Line(builder, 3, "run_attempt", evidence.ReleaseCandidate.GhcrInputs.Producer.RunAttempt);
        Line(builder, 3, "attempt_url", evidence.ReleaseCandidate.GhcrInputs.Producer.AttemptUrl);
        Line(builder, 3, "source_sha", evidence.ReleaseCandidate.GhcrInputs.Producer.SourceSha);
        Line(builder, 3, "source_ref", evidence.ReleaseCandidate.GhcrInputs.Producer.SourceRef);
        builder.Append(' ', 4).AppendLine("artifact:");
        Line(builder, 3, "id", evidence.ReleaseCandidate.GhcrInputs.Artifact.Id);
        Line(builder, 3, "name", evidence.ReleaseCandidate.GhcrInputs.Artifact.Name);
        Line(
            builder,
            3,
            "upload_digest",
            evidence.ReleaseCandidate.GhcrInputs.Artifact.UploadDigest);
        Line(builder, 2, "index_digest", evidence.ReleaseCandidate.GhcrInputs.IndexDigest);
        Runtime(builder, "candidate", evidence.Candidate);
        Runtime(builder, "prior", evidence.Prior);
        builder.AppendLine("profile:");
        Line(builder, 1, "id", evidence.Profile.Id);
        Line(builder, 1, "version", evidence.Profile.Version);
        builder.AppendLine("policy:");
        Line(builder, 1, "workload_manifest_digest", evidence.Policy.WorkloadManifestDigest);
        Line(builder, 1, "qualification_policy_digest", evidence.Policy.QualificationPolicyDigest);
        Line(builder, 1, "observation_policy_digest", evidence.Policy.ObservationPolicyDigest);
        builder.AppendLine("azure:");
        Line(builder, 1, "backend_kind", evidence.Azure.BackendKind);
        Line(builder, 1, "region", evidence.Azure.Region);
        Line(builder, 1, "backend_identity_digest", evidence.Azure.BackendIdentityDigest);
        Line(builder, 1, "config_digest", evidence.Azure.ConfigDigest);
        Line(builder, 1, "aws_binding_digest", evidence.Azure.AwsBindingDigest);
        builder.AppendLine("producer:");
        Line(builder, 1, "repository", evidence.Producer.Repository);
        Line(builder, 1, "workflow_path", evidence.Producer.WorkflowPath);
        Line(builder, 1, "event_name", evidence.Producer.EventName);
        Line(builder, 1, "run_id", evidence.Producer.RunId);
        Line(builder, 1, "run_attempt", evidence.Producer.RunAttempt);
        Line(builder, 1, "run_url", evidence.Producer.RunUrl);
        Line(builder, 1, "attempt_url", evidence.Producer.AttemptUrl);
        Line(builder, 1, "source_sha", evidence.Producer.SourceSha);
        Line(builder, 1, "source_ref", evidence.Producer.SourceRef);
        builder.AppendLine("capture_artifact:");
        Line(builder, 1, "id", evidence.CaptureArtifact.Id);
        Line(builder, 1, "name", evidence.CaptureArtifact.Name);
        Line(builder, 1, "upload_digest", evidence.CaptureArtifact.UploadDigest);
        builder.AppendLine("observation:");
        Line(builder, 1, "started_at_utc", evidence.Observation.StartedAtUtc);
        Line(builder, 1, "ended_at_utc", evidence.Observation.EndedAtUtc);
        Line(builder, 1, "generated_at_utc", evidence.Observation.GeneratedAtUtc);
        Line(builder, 1, "minimum_window_minutes", evidence.Observation.MinimumWindowMinutes);
        builder.AppendLine("load_shape:");
        Line(builder, 1, "candidate_concurrency", evidence.LoadShape.CandidateConcurrency);
        Line(builder, 1, "stable_concurrency", evidence.LoadShape.StableConcurrency);
        Line(builder, 1, "operation_mix_identity", evidence.LoadShape.OperationMixIdentity);
        builder.AppendLine("cohorts:");
        foreach (var cohort in evidence.Cohorts)
        {
            Line(builder, 1, "- id", cohort.Id);
            Line(builder, 2, "role", cohort.Role);
            Line(builder, 2, "runtime_identity_digest", cohort.RuntimeIdentityDigest);
            Line(builder, 2, "runtime_digest", cohort.RuntimeDigest);
            Line(builder, 2, "backend_kind", cohort.BackendKind);
            Line(builder, 2, "region", cohort.Region);
            Line(builder, 2, "backend_identity_digest", cohort.BackendIdentityDigest);
            Line(builder, 2, "config_digest", cohort.ConfigDigest);
            Line(builder, 2, "aws_binding_digest", cohort.AwsBindingDigest);
            Line(builder, 2, "observed_from_utc", cohort.ObservedFromUtc);
            Line(builder, 2, "observed_until_utc", cohort.ObservedUntilUtc);
            builder.Append(' ', 4).AppendLine("member_digests:");
            foreach (var member in cohort.MemberDigests)
            {
                builder.Append(' ', 6).Append("- ").Append(Quote(member)).AppendLine();
            }
            builder.Append(' ', 4).AppendLine("operation_diagnostics:");
            foreach (var diagnostic in cohort.OperationDiagnostics)
            {
                Line(builder, 3, "- service", diagnostic.Service);
                Line(builder, 4, "operation", diagnostic.Operation);
                Line(builder, 4, "completions", diagnostic.Completions);
                Line(builder, 4, "failures", diagnostic.Failures);
                Line(builder, 4, "throttles", diagnostic.Throttles);
                if (diagnostic.FirstFailure is not null)
                {
                    builder.Append(' ', 8).AppendLine("first_failure:");
                    Line(builder, 5, "category", diagnostic.FirstFailure.Category);
                    if (diagnostic.FirstFailure.StatusCode is int statusCode)
                    {
                        Line(builder, 5, "status_code", statusCode);
                    }
                    Line(builder, 5, "error_code", diagnostic.FirstFailure.ErrorCode);
                }
            }
        }
        builder.AppendLine("metrics:");
        foreach (var metric in evidence.Metrics)
        {
            Line(builder, 1, "- id", metric.Id);
            Line(builder, 2, "unit", metric.Unit);
            Line(builder, 2, "comparison", metric.Comparison);
            Line(builder, 2, "threshold", metric.Threshold!.Value);
            Line(builder, 2, "candidate_value", metric.CandidateValue!.Value);
            Line(builder, 2, "stable_value", metric.StableValue!.Value);
            Line(builder, 2, "candidate_samples", metric.CandidateSamples);
            Line(builder, 2, "stable_samples", metric.StableSamples);
            Line(builder, 2, "samples", metric.Samples);
            Line(builder, 2, "captured_at_utc", metric.CapturedAtUtc);
            Line(builder, 2, "result", metric.Result);
        }
        builder.AppendLine("rollback_triggers:");
        foreach (var trigger in evidence.RollbackTriggers)
        {
            Line(builder, 1, "- id", trigger.Id);
            Line(builder, 2, "metric_id", trigger.MetricId);
            Line(builder, 2, "status", trigger.Status);
            Line(builder, 2, "override_applied", trigger.OverrideApplied!.Value);
            Line(builder, 2, "suppressed", trigger.Suppressed!.Value);
        }
        builder.AppendLine("decision:");
        Line(builder, 1, "verdict", evidence.Decision.Verdict);
        Line(builder, 1, "owner", evidence.Decision.Owner);
        Line(builder, 1, "reason", evidence.Decision.Reason);
        Line(builder, 1, "decided_at_utc", evidence.Decision.DecidedAtUtc);
        if (evidence.Restoration is not null)
        {
            builder.AppendLine("restoration:");
            Line(builder, 1, "verified", evidence.Restoration.Verified);
            Line(builder, 1, "runtime_identity_digest",
                evidence.Restoration.RuntimeIdentityDigest);
            Line(builder, 1, "runtime_digest", evidence.Restoration.RuntimeDigest);
            Line(builder, 1, "backend_identity_digest",
                evidence.Restoration.BackendIdentityDigest);
            Line(builder, 1, "config_digest", evidence.Restoration.ConfigDigest);
            Line(builder, 1, "aws_binding_digest", evidence.Restoration.AwsBindingDigest);
            Line(builder, 1, "started_at_utc", evidence.Restoration.StartedAtUtc);
            Line(builder, 1, "verified_at_utc", evidence.Restoration.VerifiedAtUtc);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    public static void RenderBinding(RcObservationValidationContext binding, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                binding,
                RcObservationGenerationJsonContext.Default.RcObservationValidationContext)
            + Environment.NewLine,
            new UTF8Encoding(false));
    }

    public static string DigestFile(string path) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Runtime(
        StringBuilder builder,
        string name,
        RcObservationRuntimeIdentity runtime)
    {
        builder.Append(name).AppendLine(":");
        Line(builder, 1, "identity_digest", runtime.IdentityDigest);
        Line(builder, 1, "runtime_digest", runtime.RuntimeDigest);
        Line(builder, 1, "source_sha", runtime.SourceSha);
    }

    private static void Line(StringBuilder builder, int indent, string name, string value) =>
        builder.Append(' ', indent * 2).Append(name).Append(": ")
            .Append(Quote(value)).AppendLine();

    private static void Line(StringBuilder builder, int indent, string name, int value) =>
        builder.Append(' ', indent * 2).Append(name).Append(": ")
            .Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine();

    private static void Line(StringBuilder builder, int indent, string name, long value) =>
        builder.Append(' ', indent * 2).Append(name).Append(": ")
            .Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine();

    private static void Line(StringBuilder builder, int indent, string name, double value) =>
        builder.Append(' ', indent * 2).Append(name).Append(": ")
            .Append(value.ToString("R", CultureInfo.InvariantCulture)).AppendLine();

    private static void Line(StringBuilder builder, int indent, string name, bool value) =>
        builder.Append(' ', indent * 2).Append(name).Append(": ")
            .Append(value ? "true" : "false").AppendLine();

    private static void Line(
        StringBuilder builder,
        int indent,
        string name,
        DateTimeOffset value) =>
        Line(builder, indent, name, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}

[JsonSerializable(typeof(RcObservationCapture))]
[JsonSerializable(typeof(RcObservationCaptureArtifactSelection))]
[JsonSerializable(typeof(RcObservationArchiveInputSelection))]
[JsonSerializable(typeof(RcObservationGhcrInputSelection))]
[JsonSerializable(typeof(RcObservationCanonicalIdentitySelection))]
[JsonSerializable(typeof(RcObservationValidationContext))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class RcObservationGenerationJsonContext : JsonSerializerContext;
