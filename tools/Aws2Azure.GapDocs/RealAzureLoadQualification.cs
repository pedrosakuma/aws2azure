using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public sealed class WorkloadQualificationPolicy
{
    public int SchemaVersion { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public int ProfileVersion { get; set; }
    public SloQualificationRules Rules { get; set; } = new();
    public RealAzureLoadShape LoadShape { get; set; } = new();
    public List<WorkloadQualificationScenarioPolicy> Scenarios { get; set; } = new();
}

public sealed class WorkloadQualificationScenarioPolicy
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public List<WorkloadQualificationSignalPolicy> Signals { get; set; } = new();
}

public sealed class WorkloadQualificationSignalPolicy
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public string ThresholdStatus { get; set; } = "resolved";
    public string ThresholdReason { get; set; } = string.Empty;
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
}

public sealed class RealAzureLoadEvidence
{
    public int SchemaVersion { get; set; }
    public SloQualificationProfile Profile { get; set; } = new();
    public SloQualificationCandidate Candidate { get; set; } = new();
    public RealAzureLoadEvidenceProvenance Provenance { get; set; } = new();
    public RealAzureLoadShape LoadShape { get; set; } = new();
    public List<RealAzureLoadOperationMeasurement> OperationMix { get; set; } = new();
    public List<SloQualificationScenario> Scenarios { get; set; } = new();
    public List<RealAzureLoadSignalMeasurement> Signals { get; set; } = new();
    public List<RealAzureCredentialRotationProof> CredentialRotationProofs { get; set; } = new();
}

public sealed class RealAzureLoadShape
{
    public int Concurrency { get; set; }
    public double RequestedDurationSeconds { get; set; }
}

public sealed class RealAzureLoadOperationMeasurement
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public long Completions { get; set; }
    public long Failures { get; set; }
    public double P95Milliseconds { get; set; }
    public double P99Milliseconds { get; set; }
}

public sealed class RealAzureLoadEvidenceProvenance
{
    public string RunId { get; set; } = string.Empty;
    public string RunUrl { get; set; } = string.Empty;
    public int RunAttempt { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset WindowStartUtc { get; set; }
    public DateTimeOffset WindowEndUtc { get; set; }
    public string Region { get; set; } = string.Empty;
    public string BackendDescription { get; set; } = string.Empty;
    public string ProducerConfigDigest { get; set; } = string.Empty;
}

public sealed class RealAzureLoadSignalMeasurement
{
    public string Id { get; set; } = string.Empty;
    public string ScenarioId { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public double MeasuredValue { get; set; }
    public long Samples { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public sealed class RealAzureCredentialRotationProof
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string RotationKind { get; set; } = string.Empty;
    public string AuthenticationMode { get; set; } = string.Empty;
    public string BackendKind { get; set; } = string.Empty;
    public string IdentityAClientId { get; set; } = string.Empty;
    public string IdentityAObjectId { get; set; } = string.Empty;
    public string IdentityBClientId { get; set; } = string.Empty;
    public string IdentityBObjectId { get; set; } = string.Empty;
    public string RoleAssignmentAId { get; set; } = string.Empty;
    public string RoleAssignmentBId { get; set; } = string.Empty;
    public string RoleDefinitionId { get; set; } = string.Empty;
    public string RoleScopeDigestA { get; set; } = string.Empty;
    public string RoleScopeDigestB { get; set; } = string.Empty;
    public string FederatedIssuerDigest { get; set; } = string.Empty;
    public string FederatedSubjectDigest { get; set; } = string.Empty;
    public string FederatedAudienceDigest { get; set; } = string.Empty;
    public string RuntimeArtifactDigestA { get; set; } = string.Empty;
    public string RuntimeArtifactDigestB { get; set; } = string.Empty;
    public string CandidateConfigDigestA { get; set; } = string.Empty;
    public string CandidateConfigDigestB { get; set; } = string.Empty;
    public string ProxyConfigDigestA { get; set; } = string.Empty;
    public string ProxyConfigDigestB { get; set; } = string.Empty;
    public string AwsBindingDigestA { get; set; } = string.Empty;
    public string AwsBindingDigestB { get; set; } = string.Empty;
    public string BackendTargetDigestA { get; set; } = string.Empty;
    public string BackendTargetDigestB { get; set; } = string.Empty;
    public long SetupPropagationRetries { get; set; }
    public long FederatedCredentialCompletions { get; set; }
    public long RevocationPolls { get; set; }
    public long GreenReadCompletions { get; set; }
    public long OldAccessDeniedCompletions { get; set; }
    public string OldAccessDeniedErrorCode { get; set; } = string.Empty;
    public int OldAccessDeniedHttpStatus { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset RevocationRequestedAtUtc { get; set; }
    public DateTimeOffset OldAccessDeniedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

public sealed class RealAzureLoadQualificationMetadata
{
    public string RunId { get; set; } = string.Empty;
    public string RunUrl { get; set; } = string.Empty;
    public int RunAttempt { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
}

public static class WorkloadQualificationPolicyLoader
{
    public static WorkloadQualificationPolicy Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Workload qualification policy not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        using var reader = new StreamReader(path);
        var policy = deserializer.Deserialize<WorkloadQualificationPolicy>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        policy.Rules ??= new SloQualificationRules();
        policy.LoadShape ??= new RealAzureLoadShape();
        policy.Scenarios ??= new List<WorkloadQualificationScenarioPolicy>();
        foreach (var scenario in policy.Scenarios)
        {
            scenario.Signals ??= new List<WorkloadQualificationSignalPolicy>();
        }
        return policy;
    }
}

public static class RealAzureLoadQualificationGenerator
{
    public static SloQualificationDocument Generate(
        WorkloadGaManifest manifest,
        SloQualificationDocument candidate,
        WorkloadQualificationPolicy policy,
        IReadOnlyList<RealAzureLoadEvidence> evidence,
        RealAzureLoadQualificationMetadata metadata)
    {
        ValidateInputs(manifest, candidate, policy, evidence, metadata);

        var orderedEvidence = evidence
            .OrderBy(item => item.Provenance.WindowStartUtc)
            .ThenBy(item => item.Provenance.RunId, StringComparer.Ordinal)
            .ThenBy(item => item.Provenance.RunAttempt)
            .ToList();
        var first = orderedEvidence[0];
        var document = new SloQualificationDocument
        {
            SchemaVersion = SloQualificationValidator.CurrentSchemaVersion,
            ArtifactKind = "real_azure_workload_qualification",
            Verdict = "candidate",
            Profile = CloneProfile(candidate.Profile),
            Candidate = new SloQualificationCandidate
            {
                GitSha = candidate.Candidate.GitSha,
                ArtifactDigest = candidate.Candidate.ArtifactDigest,
                ConfigDigest = candidate.Candidate.ConfigDigest,
            },
            Provenance = new SloQualificationProvenance
            {
                RunId = metadata.RunId,
                RunUrl = metadata.RunUrl,
                RunAttempt = metadata.RunAttempt,
                GeneratedAtUtc = metadata.GeneratedAtUtc.ToUniversalTime(),
                WindowStartUtc = orderedEvidence.Min(item => item.Provenance.WindowStartUtc),
                WindowEndUtc = orderedEvidence.Max(item => item.Provenance.WindowEndUtc),
                Region = first.Provenance.Region,
                BackendDescription = first.Provenance.BackendDescription,
                CorrectnessRun = new SloQualificationSourceRun
                {
                    RunId = candidate.Provenance.RunId,
                    RunUrl = candidate.Provenance.RunUrl,
                    RunAttempt = candidate.Provenance.RunAttempt,
                    WindowStartUtc = candidate.Provenance.WindowStartUtc,
                    WindowEndUtc = candidate.Provenance.WindowEndUtc,
                    GitSha = candidate.Candidate.GitSha,
                    ArtifactDigest = candidate.Candidate.ArtifactDigest,
                    ConfigDigest = candidate.Candidate.ConfigDigest,
                },
                SourceRuns = orderedEvidence.Select(item => new SloQualificationSourceRun
                {
                    RunId = item.Provenance.RunId,
                    RunUrl = item.Provenance.RunUrl,
                    RunAttempt = item.Provenance.RunAttempt,
                    WindowStartUtc = item.Provenance.WindowStartUtc,
                    WindowEndUtc = item.Provenance.WindowEndUtc,
                    GitSha = item.Candidate.GitSha,
                    ArtifactDigest = item.Candidate.ArtifactDigest,
                    ConfigDigest = item.Candidate.ConfigDigest,
                }).ToList(),
            },
            Rules = policy.Rules,
        };

        var blocked = candidate.Verdict != "candidate";
        if (blocked)
        {
            AddFinding(
                document,
                "conformance_not_candidate",
                $"Correctness qualification verdict is '{candidate.Verdict}', not 'candidate'.");
        }
        if (orderedEvidence.Count < policy.Rules.MinDistinctRuns)
        {
            blocked = true;
            AddFinding(
                document,
                "insufficient_distinct_runs",
                $"Collected {orderedEvidence.Count} immutable run(s); " +
                $"policy requires {policy.Rules.MinDistinctRuns}.");
        }
        foreach (var run in orderedEvidence)
        {
            if (metadata.GeneratedAtUtc.ToUniversalTime()
                - run.Provenance.WindowEndUtc.ToUniversalTime()
                > TimeSpan.FromHours(policy.Rules.MaxArtifactAgeHours))
            {
                blocked = true;
                AddFinding(
                    document,
                    "stale_source_run",
                    $"Run {run.Provenance.RunId}/{run.Provenance.RunAttempt} is older than " +
                    $"{policy.Rules.MaxArtifactAgeHours} hours.");
            }
        }
        if (metadata.GeneratedAtUtc.ToUniversalTime()
            - candidate.Provenance.WindowEndUtc.ToUniversalTime()
            > TimeSpan.FromHours(policy.Rules.MaxArtifactAgeHours))
        {
            blocked = true;
            AddFinding(
                document,
                "stale_correctness_candidate",
                $"Correctness run {candidate.Provenance.RunId}/{candidate.Provenance.RunAttempt} " +
                $"is older than {policy.Rules.MaxArtifactAgeHours} hours.");
        }

        foreach (var scenarioPolicy in policy.Scenarios)
        {
            var measurements = new List<SloQualificationScenario>();
            foreach (var run in orderedEvidence)
            {
                var matches = run.Scenarios
                    .Where(item => item.Id.Equals(scenarioPolicy.Id, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count != 1)
                {
                    blocked = true;
                    AddFinding(
                        document,
                        matches.Count == 0 ? "scenario_missing" : "scenario_duplicated",
                        $"Run {run.Provenance.RunId}/{run.Provenance.RunAttempt} contains " +
                        $"{matches.Count} '{scenarioPolicy.Id}' scenario row(s).");
                    continue;
                }
                var measurement = matches[0];
                if (measurement.Service != scenarioPolicy.Service
                    || measurement.Operation != scenarioPolicy.Operation
                    || measurement.EvidenceSource != scenarioPolicy.EvidenceSource)
                {
                    throw new InvalidDataException(
                        $"Scenario '{scenarioPolicy.Id}' does not match its qualification policy.");
                }
                if (measurement.Completions == 0)
                {
                    blocked = true;
                    AddFinding(
                        document,
                        "zero_completions",
                        $"Run {run.Provenance.RunId}/{run.Provenance.RunAttempt} scenario " +
                        $"'{scenarioPolicy.Id}' has zero completions.",
                        scenarioPolicy.Id);
                }
                var runAttempts = (double)measurement.Completions + measurement.Failures;
                var runFailureRate = runAttempts == 0
                    ? 0
                    : measurement.Failures / runAttempts;
                if (runFailureRate > policy.Rules.MaxFailureRate)
                {
                    blocked = true;
                    AddFinding(
                        document,
                        "failure_rate_exceeded",
                        $"Run {run.Provenance.RunId}/{run.Provenance.RunAttempt} scenario " +
                        $"'{scenarioPolicy.Id}' failure rate {runFailureRate:F6} exceeds " +
                        $"{policy.Rules.MaxFailureRate:F6}.",
                        scenarioPolicy.Id);
                }
                measurements.Add(measurement);
            }

            if (measurements.Count == 0)
            {
                continue;
            }

            var scenario = new SloQualificationScenario
            {
                Id = scenarioPolicy.Id,
                Service = scenarioPolicy.Service,
                Operation = scenarioPolicy.Operation,
                EvidenceSource = scenarioPolicy.EvidenceSource,
                Completions = measurements.Sum(item => item.Completions),
                Failures = measurements.Sum(item => item.Failures),
                Skipped = measurements.Sum(item => item.Skipped),
                DurationSeconds = measurements.Sum(item => item.DurationSeconds),
                CapturedAtUtc = measurements.Max(item => item.CapturedAtUtc),
            };
            document.Scenarios.Add(scenario);

            var requiresCapacityVolume = scenarioPolicy.Signals.Any(
                signal => signal.Source == "backend_capacity");
            if (scenario.Completions == 0
                || (scenario.Completions < policy.Rules.MinSamplesPerScenario
                    && scenario.EvidenceSource == "real_azure"
                    && requiresCapacityVolume)
                || (scenario.DurationSeconds < policy.Rules.MinDurationSeconds
                    && scenario.EvidenceSource == "real_azure"
                    && requiresCapacityVolume))
            {
                blocked = true;
                AddFinding(
                    document,
                    scenario.Completions == 0
                        ? "zero_completions"
                        : "insufficient_scenario_evidence",
                    $"Scenario '{scenario.Id}' has {scenario.Completions} completions over " +
                    $"{scenario.DurationSeconds:F3}s.",
                    scenario.Id);
            }
            var attempts = (double)scenario.Completions + scenario.Failures;
            var failureRate = attempts == 0 ? 0 : scenario.Failures / attempts;
            if (failureRate > policy.Rules.MaxFailureRate)
            {
                blocked = true;
                AddFinding(
                    document,
                    "failure_rate_exceeded",
                    $"Scenario '{scenario.Id}' failure rate {failureRate:F6} exceeds " +
                    $"{policy.Rules.MaxFailureRate:F6}.",
                    scenario.Id);
            }

            foreach (var signalPolicy in scenarioPolicy.Signals)
            {
                var signalMeasurements = new List<RealAzureLoadSignalMeasurement>();
                foreach (var run in orderedEvidence)
                {
                    var matches = run.Signals
                        .Where(item => item.Id.Equals(signalPolicy.Id, StringComparison.Ordinal)
                                       && item.ScenarioId.Equals(
                                           scenarioPolicy.Id,
                                           StringComparison.Ordinal))
                        .ToList();
                    if (matches.Count != 1)
                    {
                        blocked = true;
                        AddFinding(
                            document,
                            matches.Count == 0 ? "signal_missing" : "signal_duplicated",
                            $"Run {run.Provenance.RunId}/{run.Provenance.RunAttempt} contains " +
                            $"{matches.Count} '{signalPolicy.Id}' signal measurement(s).",
                            scenarioPolicy.Id);
                        continue;
                    }
                    signalMeasurements.Add(matches[0]);
                }
                if (signalMeasurements.Count == 0)
                {
                    continue;
                }
                if (signalMeasurements.Any(item => item.Metric != signalPolicy.Metric
                                                   || !double.IsFinite(item.MeasuredValue)
                                                   || item.Samples <= 0))
                {
                    throw new InvalidDataException(
                        $"Signal '{signalPolicy.Id}' contains an invalid measurement.");
                }

                var measuredValue = signalPolicy.MinValue is not null
                                    || signalPolicy.ThresholdStatus == "unresolved"
                                    && signalPolicy.Metric == "throughput_per_sec"
                    ? signalMeasurements.Min(item => item.MeasuredValue)
                    : signalMeasurements.Max(item => item.MeasuredValue);
                if (signalPolicy.ThresholdStatus == "unresolved")
                {
                    blocked = true;
                    document.Signals.Add(new SloQualificationSignal
                    {
                        Id = signalPolicy.Id,
                        ScenarioId = scenarioPolicy.Id,
                        Source = signalPolicy.Source,
                        Disposition = "report_only",
                        Metric = signalPolicy.Metric,
                        MeasuredValue = measuredValue,
                        Samples = signalMeasurements.Sum(item => item.Samples),
                        CapturedAtUtc = signalMeasurements.Max(item => item.CapturedAtUtc),
                    });
                    AddFinding(
                        document,
                        "signal_threshold_unresolved",
                        $"Signal '{signalPolicy.Id}' remains unresolved: " +
                        signalPolicy.ThresholdReason,
                        scenarioPolicy.Id);
                    continue;
                }
                var signal = new SloQualificationSignal
                {
                    Id = signalPolicy.Id,
                    ScenarioId = scenarioPolicy.Id,
                    Source = signalPolicy.Source,
                    Disposition = signalPolicy.Disposition,
                    Metric = signalPolicy.Metric,
                    MinValue = signalPolicy.MinValue,
                    MaxValue = signalPolicy.MaxValue,
                    MeasuredValue = measuredValue,
                    Samples = signalMeasurements.Sum(item => item.Samples),
                    CapturedAtUtc = signalMeasurements.Max(item => item.CapturedAtUtc),
                };
                document.Signals.Add(signal);

                if (signal.Disposition == "blocking"
                    && (signal.MinValue is not null && measuredValue < signal.MinValue
                        || signal.MaxValue is not null && measuredValue > signal.MaxValue))
                {
                    blocked = true;
                    AddFinding(
                        document,
                        "signal_threshold_failed",
                        $"Signal '{signal.Id}' measured {measuredValue.ToString("G17", CultureInfo.InvariantCulture)}.",
                        scenario.Id);
                }
            }
        }

        document.Verdict = blocked ? "candidate" : "qualified";
        return document;
    }

    public static RealAzureLoadEvidence LoadEvidence(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Real-Azure load evidence not found", path);
        }
        return JsonSerializer.Deserialize(
                   File.ReadAllText(path),
                   RealAzureLoadEvidenceJsonContext.Default.RealAzureLoadEvidence)
               ?? throw new InvalidDataException($"{path}: empty load evidence");
    }

    public static void RenderTrend(
        IReadOnlyList<RealAzureLoadEvidence> evidence,
        string path)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "artifact_kind,run_id,run_attempt,profile_id,scenario_id,evidence_source," +
            "window_start_utc,window_end_utc,completions,failures,skipped,duration_seconds");
        foreach (var run in evidence.OrderBy(item => item.Provenance.WindowStartUtc))
        {
            foreach (var scenario in run.Scenarios.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                builder.AppendLine(string.Join(
                    ',',
                    "real_azure_workload_qualification",
                    Csv(run.Provenance.RunId),
                    run.Provenance.RunAttempt.ToString(CultureInfo.InvariantCulture),
                    Csv(run.Profile.Id),
                    Csv(scenario.Id),
                    Csv(scenario.EvidenceSource),
                    run.Provenance.WindowStartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    run.Provenance.WindowEndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    scenario.Completions.ToString(CultureInfo.InvariantCulture),
                    scenario.Failures.ToString(CultureInfo.InvariantCulture),
                    scenario.Skipped.ToString(CultureInfo.InvariantCulture),
                    scenario.DurationSeconds.ToString("G17", CultureInfo.InvariantCulture)));
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, builder.ToString());
    }

    private static void ValidateInputs(
        WorkloadGaManifest manifest,
        SloQualificationDocument candidate,
        WorkloadQualificationPolicy policy,
        IReadOnlyList<RealAzureLoadEvidence> evidence,
        RealAzureLoadQualificationMetadata metadata)
    {
        if (policy.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported workload qualification policy schema: {policy.SchemaVersion}");
        }
        if (string.IsNullOrWhiteSpace(metadata.RunId)
            || !Uri.TryCreate(metadata.RunUrl, UriKind.Absolute, out var metadataRunUri)
            || (metadataRunUri.Scheme != Uri.UriSchemeHttps
                && metadataRunUri.Scheme != Uri.UriSchemeHttp)
            || metadata.RunAttempt <= 0
            || metadata.GeneratedAtUtc == default)
        {
            throw new InvalidDataException("Qualification run provenance is incomplete.");
        }
        if (policy.Rules.MaxArtifactAgeHours <= 0
            || policy.Rules.MaxArtifactAgeHours > TimeSpan.MaxValue.TotalHours
            || policy.Rules.MinDistinctRuns <= 0
            || policy.Rules.MinSamplesPerScenario <= 0
            || !double.IsFinite(policy.Rules.MinDurationSeconds)
            || policy.Rules.MinDurationSeconds <= 0
            || !double.IsFinite(policy.Rules.MaxFailureRate)
            || policy.Rules.MaxFailureRate is < 0 or > 1)
        {
            throw new InvalidDataException("Workload qualification policy rules are invalid.");
        }
        if (policy.LoadShape.Concurrency <= 0
            || !double.IsFinite(policy.LoadShape.RequestedDurationSeconds)
            || policy.LoadShape.RequestedDurationSeconds <= 0)
        {
            throw new InvalidDataException("Workload qualification policy load shape is invalid.");
        }
        if (evidence.Count == 0)
        {
            throw new ArgumentException("At least one load evidence document is required.", nameof(evidence));
        }
        if (candidate.ArtifactKind != "real_azure_workload_qualification")
        {
            throw new InvalidDataException("Correctness candidate has the wrong artifact kind.");
        }
        if (policy.ProfileId != manifest.Id
            || policy.ProfileVersion != manifest.Version
            || candidate.Profile.Id != manifest.Id
            || candidate.Profile.Version != manifest.Version)
        {
            throw new InvalidDataException("Manifest, policy, and correctness candidate profile do not match.");
        }

        var requiredScenarios = manifest.Evidence.RequiredScenarios
            .ToHashSet(StringComparer.Ordinal);
        var policyScenarios = policy.Scenarios
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!requiredScenarios.SetEquals(policyScenarios))
        {
            throw new InvalidDataException(
                "Qualification policy scenarios must exactly match the workload manifest.");
        }
        if (policy.Scenarios.Count != policyScenarios.Count)
        {
            throw new InvalidDataException("Qualification policy contains duplicate scenario ids.");
        }

        var expectedOperations = manifest.Operations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateOperations = candidate.Profile.Services
            .SelectMany(item => item.Operations.Select(operation => $"{item.Service}:{operation}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedOperations.SetEquals(candidateOperations))
        {
            throw new InvalidDataException(
                "Correctness candidate operation set does not match the workload manifest.");
        }
        foreach (var scenario in policy.Scenarios)
        {
            if (!expectedOperations.Contains($"{scenario.Service}:{scenario.Operation}"))
            {
                throw new InvalidDataException(
                    $"Policy scenario '{scenario.Id}' references an operation outside the profile.");
            }
            if (!SloQualificationValues.EvidenceSources.Contains(scenario.EvidenceSource))
            {
                throw new InvalidDataException(
                    $"Policy scenario '{scenario.Id}' has invalid evidence_source.");
            }
            if (scenario.EvidenceSource == "emulator")
            {
                throw new InvalidDataException(
                    $"Policy scenario '{scenario.Id}' cannot use emulator evidence.");
            }
            if (manifest.Evidence.RequiredRealAzureScenarios.Contains(
                    scenario.Id,
                    StringComparer.Ordinal)
                && scenario.EvidenceSource != "real_azure")
            {
                throw new InvalidDataException(
                    $"Policy scenario '{scenario.Id}' must use real_azure evidence.");
            }
            if (scenario.Signals.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count()
                != scenario.Signals.Count)
            {
                throw new InvalidDataException(
                    $"Policy scenario '{scenario.Id}' contains duplicate signal ids.");
            }
            foreach (var signal in scenario.Signals)
            {
                var thresholdResolved = signal.ThresholdStatus == "resolved";
                var thresholdUnresolved = signal.ThresholdStatus == "unresolved";
                if (!SloQualificationValues.SignalSources.Contains(signal.Source)
                    || !SloQualificationValues.Dispositions.Contains(signal.Disposition)
                    || !SloQualificationValues.Metrics.Contains(signal.Metric)
                    || !thresholdResolved && !thresholdUnresolved
                    || signal.MinValue is not null && signal.MaxValue is not null
                    || thresholdResolved
                       && signal.Disposition == "blocking"
                       && signal.MinValue is null
                       && signal.MaxValue is null
                    || thresholdResolved
                       && signal.Disposition == "report_only"
                       && (signal.MinValue is not null || signal.MaxValue is not null))
                {
                    throw new InvalidDataException(
                        $"Policy signal '{signal.Id}' in scenario '{scenario.Id}' is invalid.");
                }
                if (thresholdUnresolved
                    && (signal.Disposition != "blocking"
                        || signal.Source != "backend_capacity"
                        || signal.Metric is not ("throughput_per_sec" or "p95_ms" or "p99_ms")
                        || signal.MinValue is not null
                        || signal.MaxValue is not null
                        || string.IsNullOrWhiteSpace(signal.ThresholdReason)))
                {
                    throw new InvalidDataException(
                        $"Unresolved policy signal '{signal.Id}' in scenario " +
                        $"'{scenario.Id}' must be a reasoned blocking backend-capacity signal.");
                }
            }
        }
        if (!policy.Scenarios.SelectMany(item => item.Signals).Any(
                item => item.Disposition == "blocking"
                        && item.Source == "backend_capacity"
                        && item.Metric is "throughput_per_sec" or "p95_ms" or "p99_ms"))
        {
            throw new InvalidDataException(
                "Qualification policy requires a blocking backend-capacity throughput or latency signal.");
        }

        var first = evidence[0];
        var seenRuns = new HashSet<string>(StringComparer.Ordinal);
        var seenRotationIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in evidence)
        {
            var runOperations = run.Profile.Services
                .SelectMany(item => item.Operations.Select(operation => $"{item.Service}:{operation}"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (run.SchemaVersion != 1
                || run.Profile.Id != manifest.Id
                || run.Profile.Version != manifest.Version
                || !expectedOperations.SetEquals(runOperations)
                || run.Candidate.GitSha != candidate.Candidate.GitSha
                || run.Candidate.ArtifactDigest != candidate.Candidate.ArtifactDigest
                || run.Candidate.ConfigDigest != candidate.Candidate.ConfigDigest
                || run.Provenance.Region != first.Provenance.Region
                || run.Provenance.BackendDescription != first.Provenance.BackendDescription
                || string.IsNullOrWhiteSpace(run.Provenance.ProducerConfigDigest)
                || run.Provenance.ProducerConfigDigest
                    != first.Provenance.ProducerConfigDigest
                || run.Provenance.RunAttempt <= 0
                || run.Provenance.WindowStartUtc >= run.Provenance.WindowEndUtc
                || run.Provenance.GeneratedAtUtc < run.Provenance.WindowEndUtc
                || !seenRuns.Add(run.Provenance.RunId))
            {
                throw new InvalidDataException(
                    "Load evidence provenance, candidate, environment, or run identity is inconsistent.");
            }
            ValidateLoadShapeAndOperationMix(run, expectedOperations);
            if (run.LoadShape.Concurrency != policy.LoadShape.Concurrency
                || run.LoadShape.RequestedDurationSeconds
                    != policy.LoadShape.RequestedDurationSeconds
                || run.LoadShape.Concurrency != first.LoadShape.Concurrency
                || run.LoadShape.RequestedDurationSeconds
                    != first.LoadShape.RequestedDurationSeconds)
            {
                throw new InvalidDataException(
                    "Load evidence runs must use the policy's reviewed concurrency and duration.");
            }
            if (run.Scenarios.Any(item =>
                    item.CapturedAtUtc < run.Provenance.WindowStartUtc
                    || item.CapturedAtUtc > run.Provenance.WindowEndUtc)
                || run.Signals.Any(item =>
                    item.CapturedAtUtc < run.Provenance.WindowStartUtc
                    || item.CapturedAtUtc > run.Provenance.WindowEndUtc))
            {
                throw new InvalidDataException(
                    $"Load evidence run {run.Provenance.RunId}/{run.Provenance.RunAttempt} " +
                    "contains measurements outside its immutable run window.");
            }
            ValidateCredentialRotationProof(
                run,
                candidate.Candidate,
                policy.Scenarios.Any(item => item.Id == "credential-rotation"),
                seenRotationIdentities);
        }
    }

    private static void ValidateCredentialRotationProof(
        RealAzureLoadEvidence run,
        SloQualificationCandidate candidate,
        bool required,
        ISet<string> seenIdentities)
    {
        if (!required)
        {
            if (run.CredentialRotationProofs.Count != 0)
            {
                throw new InvalidDataException(
                    "Load evidence contains credential rotation proof outside its policy.");
            }
            return;
        }
        if (run.CredentialRotationProofs.Count != 1)
        {
            throw new InvalidDataException(
                "Each load run must contain exactly one credential rotation proof.");
        }

        var proof = run.CredentialRotationProofs[0];
        var scenario = run.Scenarios.SingleOrDefault(item => item.Id == "credential-rotation");
        if (scenario is null
            || scenario.Completions != 1
            || scenario.Failures != 0
            || scenario.Skipped != 0
            || scenario.CapturedAtUtc != proof.CompletedAtUtc
            || proof.ScenarioId != "credential-rotation"
            || proof.Service != "secretsmanager"
            || proof.Operation != "GetSecretValue"
            || proof.RotationKind != "azure_backend_identity"
            || proof.AuthenticationMode != "workload_identity"
            || proof.BackendKind != "key_vault")
        {
            throw new InvalidDataException(
                "Credential rotation proof does not match its successful real-Azure scenario.");
        }

        if (string.IsNullOrWhiteSpace(proof.IdentityAClientId)
            || string.IsNullOrWhiteSpace(proof.IdentityAObjectId)
            || string.IsNullOrWhiteSpace(proof.IdentityBClientId)
            || string.IsNullOrWhiteSpace(proof.IdentityBObjectId)
            || proof.IdentityAClientId.Equals(proof.IdentityBClientId, StringComparison.OrdinalIgnoreCase)
            || proof.IdentityAObjectId.Equals(proof.IdentityBObjectId, StringComparison.OrdinalIgnoreCase)
            || proof.RoleAssignmentAId.Equals(proof.RoleAssignmentBId, StringComparison.OrdinalIgnoreCase)
            || !seenIdentities.Add(proof.IdentityAObjectId)
            || !seenIdentities.Add(proof.IdentityBObjectId))
        {
            throw new InvalidDataException(
                "Credential rotation proof must use fresh, distinct blue/green runtime identities.");
        }

        if (!IsRoleAssignmentId(proof.RoleAssignmentAId)
            || !IsRoleAssignmentId(proof.RoleAssignmentBId)
            || proof.RoleDefinitionId != "b86a8fe4-44ce-4948-aee5-eccb2c155cd7"
            || !IsDigest(proof.RoleScopeDigestA)
            || proof.RoleScopeDigestA != proof.RoleScopeDigestB
            || !IsDigest(proof.FederatedIssuerDigest)
            || !IsDigest(proof.FederatedSubjectDigest)
            || !IsDigest(proof.FederatedAudienceDigest)
            || proof.RuntimeArtifactDigestA != candidate.ArtifactDigest
            || proof.RuntimeArtifactDigestB != candidate.ArtifactDigest
            || proof.RuntimeArtifactDigestA != proof.RuntimeArtifactDigestB
            || proof.CandidateConfigDigestA != candidate.ConfigDigest
            || proof.CandidateConfigDigestB != candidate.ConfigDigest
            || proof.CandidateConfigDigestA != proof.CandidateConfigDigestB
            || !IsDigest(proof.ProxyConfigDigestA)
            || proof.ProxyConfigDigestA != proof.ProxyConfigDigestB
            || !IsDigest(proof.AwsBindingDigestA)
            || proof.AwsBindingDigestA != proof.AwsBindingDigestB
            || !IsDigest(proof.BackendTargetDigestA)
            || proof.BackendTargetDigestA != proof.BackendTargetDigestB)
        {
            throw new InvalidDataException(
                "Credential rotation proof contains runtime, configuration, binding, or backend drift.");
        }

        if (proof.SetupPropagationRetries < 0
            || proof.FederatedCredentialCompletions != 2
            || proof.RevocationPolls <= 0
            || proof.GreenReadCompletions < 3
            || proof.OldAccessDeniedCompletions != 1
            || proof.OldAccessDeniedErrorCode != "AccessDeniedException"
            || proof.OldAccessDeniedHttpStatus != 403
            || proof.StartedAtUtc < run.Provenance.WindowStartUtc
            || proof.StartedAtUtc >= proof.RevocationRequestedAtUtc
            || proof.RevocationRequestedAtUtc > proof.OldAccessDeniedAtUtc
            || proof.OldAccessDeniedAtUtc > proof.CompletedAtUtc
            || proof.CompletedAtUtc > run.Provenance.WindowEndUtc)
        {
            throw new InvalidDataException(
                "Credential rotation proof lacks bounded overlap, old-access denial, or valid timing.");
        }
    }

    private static bool IsDigest(string value)
    {
        return value.StartsWith("sha256:", StringComparison.Ordinal)
               && value.Length == 71
               && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }

    private static bool IsRoleAssignmentId(string value)
    {
        return value.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
               && value.Contains(
                   "/providers/Microsoft.Authorization/roleAssignments/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateLoadShapeAndOperationMix(
        RealAzureLoadEvidence run,
        IReadOnlySet<string> expectedOperations)
    {
        if (run.LoadShape.Concurrency <= 0
            || !double.IsFinite(run.LoadShape.RequestedDurationSeconds)
            || run.LoadShape.RequestedDurationSeconds <= 0
            || run.OperationMix.Count != expectedOperations.Count)
        {
            throw new InvalidDataException("Load evidence shape or operation mix is incomplete.");
        }

        var operations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var measurement in run.OperationMix)
        {
            if (string.IsNullOrWhiteSpace(measurement.Service)
                || string.IsNullOrWhiteSpace(measurement.Operation)
                || measurement.Completions < 0
                || measurement.Failures < 0
                || measurement.Completions + measurement.Failures == 0
                || !double.IsFinite(measurement.P95Milliseconds)
                || !double.IsFinite(measurement.P99Milliseconds)
                || measurement.P95Milliseconds < 0
                || measurement.P99Milliseconds < measurement.P95Milliseconds)
            {
                throw new InvalidDataException(
                    $"Load evidence operation '{measurement.Operation}' is invalid.");
            }
            var operationKey = $"{measurement.Service}:{measurement.Operation}";
            if (!expectedOperations.Contains(operationKey) || !operations.Add(operationKey))
            {
                throw new InvalidDataException(
                    $"Load evidence operation '{operationKey}' is outside the profile or duplicated.");
            }
        }

        if (!expectedOperations.SetEquals(operations))
        {
            throw new InvalidDataException(
                "Load evidence operation mix must exactly match the workload profile.");
        }

        foreach (var scenario in run.Scenarios)
        {
            var operation = run.OperationMix.SingleOrDefault(item =>
                item.Service.Equals(scenario.Service, StringComparison.OrdinalIgnoreCase)
                && item.Operation.Equals(scenario.Operation, StringComparison.OrdinalIgnoreCase));
            if (operation is null
                || scenario.Completions > operation.Completions
                || scenario.Failures > operation.Failures)
            {
                throw new InvalidDataException(
                    $"Load evidence scenario '{scenario.Id}' is not supported by its operation mix.");
            }
        }
    }

    private static SloQualificationProfile CloneProfile(SloQualificationProfile profile) => new()
    {
        Id = profile.Id,
        Version = profile.Version,
        Services = profile.Services.Select(item => new SloQualificationProfileService
        {
            Service = item.Service,
            Operations = item.Operations.ToList(),
        }).ToList(),
    };

    private static void AddFinding(
        SloQualificationDocument document,
        string code,
        string message,
        string? scenarioId = null)
    {
        document.Findings.Add(new SloQualificationFinding
        {
            Code = code,
            Disposition = "blocking",
            ScenarioId = scenarioId,
            Message = message,
        });
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

[JsonSerializable(typeof(RealAzureLoadEvidence))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal sealed partial class RealAzureLoadEvidenceJsonContext : JsonSerializerContext;
