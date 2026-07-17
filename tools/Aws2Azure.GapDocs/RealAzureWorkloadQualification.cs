using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Aws2Azure.GapDocs;

public sealed class RealAzureWorkloadQualificationMetadata
{
    public string ProfileId { get; set; } = string.Empty;
    public int ProfileVersion { get; set; }
    public string GitSha { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string BackendDescription { get; set; } = string.Empty;
    public int RunAttempt { get; set; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<string> RequiredScenarioIds { get; set; } = new();
}

public sealed class RealAzureWorkloadOperation
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
}

public static class RealAzureWorkloadQualificationGenerator
{
    public static SloQualificationDocument Generate(
        string evidencePath,
        IReadOnlyList<RealAzureWorkloadOperation> requestedOperations,
        RealAzureWorkloadQualificationMetadata metadata)
    {
        if (!File.Exists(evidencePath))
        {
            throw new FileNotFoundException("Real-Azure conformance evidence not found", evidencePath);
        }

        var evidence = JsonSerializer.Deserialize(
                           File.ReadAllText(evidencePath),
                           EvidenceJsonContext.Default.ConformanceEvidence)
                       ?? throw new InvalidDataException($"{evidencePath}: empty evidence document");
        return Generate(evidence, requestedOperations, metadata);
    }

    public static SloQualificationDocument Generate(
        ConformanceEvidence evidence,
        IReadOnlyList<RealAzureWorkloadOperation> requestedOperations,
        RealAzureWorkloadQualificationMetadata metadata)
    {
        ValidateEvidence(evidence);
        metadata.RequiredScenarioIds ??= new List<string>();
        var operations = NormalizeOperations(requestedOperations);
        if (operations.Count == 0)
        {
            throw new ArgumentException(
                "At least one workload operation must be requested.",
                nameof(requestedOperations));
        }

        var capturedAtUtc = evidence.GeneratedAtUtc.ToUniversalTime();
        var generatedAtUtc = metadata.GeneratedAtUtc.ToUniversalTime();
        if (generatedAtUtc < capturedAtUtc)
        {
            throw new ArgumentException(
                "Qualification generation time must not precede conformance evidence.",
                nameof(metadata));
        }

        var document = new SloQualificationDocument
        {
            SchemaVersion = SloQualificationValidator.CurrentSchemaVersion,
            ArtifactKind = "real_azure_workload_qualification",
            Profile = new SloQualificationProfile
            {
                Id = metadata.ProfileId,
                Version = metadata.ProfileVersion,
                Services = operations
                    .GroupBy(operation => operation.Service, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new SloQualificationProfileService
                    {
                        Service = group.First().Service,
                        Operations = group
                            .Select(operation => operation.Operation)
                            .OrderBy(operation => operation, StringComparer.Ordinal)
                            .ToList()
                    })
                    .ToList()
            },
            Candidate = new SloQualificationCandidate
            {
                GitSha = metadata.GitSha,
                ArtifactDigest = metadata.ArtifactDigest,
                ConfigDigest = metadata.ConfigDigest
            },
            Provenance = new SloQualificationProvenance
            {
                RunId = evidence.RunId,
                RunUrl = evidence.RunUrl,
                RunAttempt = metadata.RunAttempt,
                GeneratedAtUtc = generatedAtUtc,
                WindowStartUtc = capturedAtUtc,
                WindowEndUtc = capturedAtUtc.AddTicks(1),
                Region = metadata.Region,
                BackendDescription = metadata.BackendDescription
            },
            Rules = new SloQualificationRules
            {
                MaxArtifactAgeHours = 72,
                MinSamplesPerScenario = 1,
                MinDurationSeconds = 0.001,
                MaxFailureRate = 0,
                ZeroCompletionsDisqualify = true,
                OnlySkippedRealAzureDisqualifies = true
            }
        };

        var blocked = false;
        var inconclusive = !string.IsNullOrWhiteSpace(evidence.Selection.Scenario);
        var emittedSourceScenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scenariosById =
            new Dictionary<string, SloQualificationScenario>(StringComparer.OrdinalIgnoreCase);
        if (inconclusive)
        {
            document.Findings.Add(new SloQualificationFinding
            {
                Code = "scenario_filtered_evidence",
                Disposition = "blocking",
                Message =
                    $"Conformance evidence was filtered to scenario " +
                    $"'{evidence.Selection.Scenario}' and cannot establish complete workload coverage."
            });
        }
        foreach (var requested in operations)
        {
            var service = evidence.Services.FirstOrDefault(
                item => string.Equals(item.Service, requested.Service, StringComparison.OrdinalIgnoreCase));
            var operation = service?.Operations.FirstOrDefault(
                item => string.Equals(item.Operation, requested.Operation, StringComparison.OrdinalIgnoreCase));
            if (service is null || operation is null)
            {
                inconclusive = true;
                AddFinding(
                    document,
                    "conformance_operation_missing",
                    requested,
                    $"Conformance evidence does not contain operation '{requested.Service}/{requested.Operation}'.");
                continue;
            }

            var relevantScenarios = service.Scenarios
                .Where(scenario => !scenario.OptionalCoverage
                                   && scenario.Operations.Contains(
                                       requested.Operation,
                                       StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (relevantScenarios.Count == 0)
            {
                inconclusive = true;
                AddFinding(
                    document,
                    "conformance_scenarios_missing",
                    requested,
                    $"Conformance operation '{requested.Service}/{requested.Operation}' has no scenario evidence.");
                continue;
            }

            foreach (var scenario in relevantScenarios.OrderBy(
                         scenario => scenario.Id,
                         StringComparer.Ordinal))
            {
                var sourceKey = requested.Service + "\n" + scenario.Id;
                if (!emittedSourceScenarios.Add(sourceKey))
                {
                    continue;
                }

                var stableId = StableScenarioId(scenario, metadata.RequiredScenarioIds);
                if (!scenariosById.TryGetValue(stableId, out var emitted))
                {
                    emitted = new SloQualificationScenario
                    {
                        Id = stableId,
                        Service = requested.Service,
                        Operation = requested.Operation,
                        EvidenceSource = scenario.EvidenceSource,
                        CapturedAtUtc = capturedAtUtc
                    };
                    scenariosById.Add(stableId, emitted);
                }
                else if (!emitted.Service.Equals(requested.Service, StringComparison.OrdinalIgnoreCase)
                         || emitted.EvidenceSource != scenario.EvidenceSource)
                {
                    throw new InvalidDataException(
                        $"Stable qualification scenario id '{stableId}' is ambiguous across " +
                        "services or evidence sources.");
                }

                emitted.Completions += scenario.Outcome == "passed" ? 1 : 0;
                emitted.Failures += scenario.Outcome == "failed" ? 1 : 0;
                emitted.Skipped += scenario.Outcome is "skipped" or "not_run" ? 1 : 0;
                emitted.DurationSeconds += scenario.DurationMilliseconds / 1000;
            }

            var failedOutcomes = relevantScenarios
                .Where(scenario => scenario.Outcome == "failed")
                .Select(scenario => $"{scenario.Id}:failed")
                .ToList();
            var incompleteOutcomes = relevantScenarios
                .Where(scenario => scenario.Outcome is "skipped" or "not_run")
                .Select(scenario => $"{scenario.Id}:{scenario.Outcome}")
                .ToList();
            var hasPositiveRealAzureEvidence = relevantScenarios.Any(
                scenario => scenario.EvidenceSource == "real_azure"
                            && scenario.EstablishesVerification
                            && scenario.Outcome == "passed");
            if (failedOutcomes.Count > 0)
            {
                blocked = true;
                AddFinding(
                    document,
                    "conformance_operation_failed",
                    requested,
                    "Required conformance failed: " + string.Join(", ", failedOutcomes));
                continue;
            }
            if (incompleteOutcomes.Count > 0
                || !hasPositiveRealAzureEvidence)
            {
                inconclusive = true;
                var reasons = new List<string>();
                reasons.AddRange(incompleteOutcomes);
                if (!hasPositiveRealAzureEvidence)
                {
                    reasons.Add("no_positive_real_azure_evidence");
                }
                AddFinding(
                    document,
                    "conformance_operation_inconclusive",
                    requested,
                    "Required conformance is incomplete: " + string.Join(", ", reasons));
            }
        }
        document.Scenarios = scenariosById.Values
            .OrderBy(scenario => scenario.Id, StringComparer.Ordinal)
            .ToList();

        if (blocked)
        {
            document.Verdict = "blocked";
        }
        else if (inconclusive)
        {
            document.Verdict = "inconclusive";
        }
        else
        {
            document.Verdict = "candidate";
            document.Findings.Add(new SloQualificationFinding
            {
                Code = "load_evidence_missing",
                Disposition = "blocking",
                Message =
                    "Real-Azure conformance passed, but reviewed workload thresholds and " +
                    "production-shaped load evidence are required before qualification."
            });
        }

        return document;
    }

    private static string StableScenarioId(
        ScenarioEvidence scenario,
        IReadOnlyList<string> requiredScenarioIds)
    {
        var canonical = scenario.Category switch
        {
            "throttling" => "throttling",
            "timeout" => "timeout",
            "service_unavailable" => "service-unavailable",
            "cancellation" => "cancellation",
            "retry_exhaustion" => "retry-exhaustion",
            "restart" => "restart",
            "rollback" => "rollback",
            "concurrency" => "concurrency",
            _ => scenario.Id
        };
        return requiredScenarioIds.Contains(canonical, StringComparer.OrdinalIgnoreCase)
            ? requiredScenarioIds.First(
                value => value.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            : scenario.Id;
    }

    private static List<RealAzureWorkloadOperation> NormalizeOperations(
        IReadOnlyList<RealAzureWorkloadOperation> requestedOperations)
    {
        if (requestedOperations is null)
        {
            throw new ArgumentNullException(nameof(requestedOperations));
        }

        var normalized = new List<RealAzureWorkloadOperation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedOperations)
        {
            if (requested is null
                || string.IsNullOrWhiteSpace(requested.Service)
                || string.IsNullOrWhiteSpace(requested.Operation))
            {
                throw new ArgumentException(
                    "Workload operations require non-empty service and operation names.",
                    nameof(requestedOperations));
            }

            var service = requested.Service.Trim();
            var operation = requested.Operation.Trim();
            if (seen.Add(service + "\n" + operation))
            {
                normalized.Add(new RealAzureWorkloadOperation
                {
                    Service = service,
                    Operation = operation
                });
            }
        }
        return normalized;
    }

    private static void ValidateEvidence(ConformanceEvidence evidence)
    {
        if (evidence.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported conformance evidence schema version: {evidence.SchemaVersion}");
        }
        if (string.IsNullOrWhiteSpace(evidence.RunId)
            || !Uri.TryCreate(evidence.RunUrl, UriKind.Absolute, out var runUri)
            || (runUri.Scheme != Uri.UriSchemeHttps && runUri.Scheme != Uri.UriSchemeHttp)
            || evidence.GeneratedAtUtc == default
            || evidence.Selection is null)
        {
            throw new InvalidDataException("Conformance evidence provenance is incomplete.");
        }
        if (evidence.Services is null || evidence.Services.Any(service => service is null))
        {
            throw new InvalidDataException("Conformance evidence services must not contain null values.");
        }
        foreach (var service in evidence.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Service)
                || service.Operations is null
                || service.Scenarios is null
                || service.Operations.Any(operation => operation is null)
                || service.Scenarios.Any(scenario => scenario is null))
            {
                throw new InvalidDataException(
                    $"Conformance evidence service '{service.Service}' is malformed.");
            }

            var scenarioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scenario in service.Scenarios)
            {
                if (string.IsNullOrWhiteSpace(scenario.Id)
                    || !scenarioIds.Add(scenario.Id)
                    || scenario.EvidenceSource is not ("real_azure" or "deterministic")
                    || scenario.Outcome is not ("passed" or "failed" or "skipped" or "not_run")
                    || scenario.Operations is null
                    || scenario.Operations.Any(string.IsNullOrWhiteSpace)
                    || !double.IsFinite(scenario.DurationMilliseconds)
                    || scenario.DurationMilliseconds < 0)
                {
                    throw new InvalidDataException(
                        $"Conformance evidence scenario in service '{service.Service}' is malformed.");
                }
            }

            var operationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in service.Operations)
            {
                if (string.IsNullOrWhiteSpace(operation.Operation)
                    || !operationNames.Add(operation.Operation)
                    || operation.Scenarios is null
                    || operation.BlockingOutcomes is null
                    || operation.Scenarios.Count == 0
                    || operation.Scenarios.Any(string.IsNullOrWhiteSpace))
                {
                    throw new InvalidDataException(
                        $"Conformance evidence operation in service '{service.Service}' is malformed.");
                }

                var actualScenarioIds = new HashSet<string>(
                    operation.Scenarios,
                    StringComparer.OrdinalIgnoreCase);
                var expectedScenarioIds = service.Scenarios
                    .Where(scenario => !scenario.OptionalCoverage
                                       && scenario.Operations.Contains(
                                           operation.Operation,
                                           StringComparer.OrdinalIgnoreCase))
                    .Select(scenario => scenario.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (actualScenarioIds.Count != operation.Scenarios.Count
                    || !actualScenarioIds.SetEquals(expectedScenarioIds))
                {
                    throw new InvalidDataException(
                        $"Conformance evidence operation '{service.Service}/{operation.Operation}' " +
                        "does not match scenario coverage.");
                }
            }
        }
    }

    private static void AddFinding(
        SloQualificationDocument document,
        string code,
        RealAzureWorkloadOperation operation,
        string message)
    {
        document.Findings.Add(new SloQualificationFinding
        {
            Code = code,
            Disposition = "blocking",
            Message = $"{operation.Service}/{operation.Operation}: {message}"
        });
    }
}
