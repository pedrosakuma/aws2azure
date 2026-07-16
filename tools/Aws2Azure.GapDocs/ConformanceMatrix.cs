using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aws2Azure.GapDocs;

public static class ConformanceMatrixLoader
{
    public static RealAzureConformanceMatrix Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Real-Azure conformance matrix not found", path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        using var reader = new StreamReader(path);
        var matrix = deserializer.Deserialize<RealAzureConformanceMatrix>(reader)
            ?? throw new InvalidDataException($"{path}: empty document");
        matrix.SourceFile = path;
        return matrix;
    }
}

public static class ConformanceMatrixValidator
{
    public static IReadOnlyList<string> Validate(
        RealAzureConformanceMatrix matrix,
        IReadOnlyList<OperationDoc> operationDocs)
    {
        var errors = new List<string>();
        var source = string.IsNullOrWhiteSpace(matrix.SourceFile)
            ? "real-Azure conformance matrix"
            : matrix.SourceFile;
        void Err(string message) => errors.Add($"{source}: {message}");

        if (matrix.SchemaVersion != 1)
        {
            Err($"unsupported schema_version '{matrix.SchemaVersion}'; expected 1");
        }

        var operationsByService = operationDocs
            .GroupBy(o => o.Service, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new HashSet<string>(group.Select(o => o.Operation), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var seenServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (matrix.Services is null)
        {
            Err("services missing");
            return errors;
        }

        for (var serviceIndex = 0; serviceIndex < matrix.Services.Count; serviceIndex++)
        {
            var serviceEntry = matrix.Services[serviceIndex];
            var servicePrefix = $"services[{serviceIndex}]";
            if (serviceEntry is null)
            {
                Err($"{servicePrefix} is empty");
                continue;
            }
            if (string.IsNullOrWhiteSpace(serviceEntry.Service))
            {
                Err($"{servicePrefix}.service missing");
                continue;
            }

            var service = serviceEntry.Service.Trim();
            if (!seenServices.Add(service))
            {
                Err($"{servicePrefix} duplicates service '{service}'");
            }
            if (!RealAzureConformanceValues.RegisteredServices.Contains(service))
            {
                Err($"{servicePrefix} references unregistered service '{service}'");
            }
            if (!operationsByService.TryGetValue(service, out var knownOperations))
            {
                Err($"{servicePrefix} service '{service}' has no operation gap docs");
                knownOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            if (serviceEntry.Scenarios is null || serviceEntry.Scenarios.Count == 0)
            {
                Err($"{servicePrefix} must declare at least one scenario");
                continue;
            }

            var seenScenarioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var scenarioIndex = 0; scenarioIndex < serviceEntry.Scenarios.Count; scenarioIndex++)
            {
                var scenario = serviceEntry.Scenarios[scenarioIndex];
                var prefix = $"{servicePrefix}.scenarios[{scenarioIndex}]";
                if (scenario is null)
                {
                    Err($"{prefix} is empty");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(scenario.Id))
                {
                    Err($"{prefix}.id missing");
                }
                else if (!seenScenarioIds.Add(scenario.Id))
                {
                    Err($"{prefix} duplicates scenario id '{scenario.Id}' for service '{service}'");
                }
                if (!RealAzureConformanceValues.Priorities.Contains(scenario.Priority))
                {
                    Err($"{prefix} invalid priority '{scenario.Priority}'; allowed: p0, p1, p2");
                }
                if (!RealAzureConformanceValues.Categories.Contains(scenario.Category))
                {
                    Err($"{prefix} invalid category '{scenario.Category}'; allowed: {string.Join(", ", RealAzureConformanceValues.Categories.OrderBy(v => v, StringComparer.Ordinal))}");
                }
                if (!RealAzureConformanceValues.EvidenceSources.Contains(scenario.EvidenceSource))
                {
                    Err($"{prefix} invalid evidence_source '{scenario.EvidenceSource}'; allowed: deterministic, real_azure");
                }
                if (scenario.EstablishesVerification is null)
                {
                    Err($"{prefix}.establishes_verification missing; an explicit true or false is required");
                }
                else if (scenario.EstablishesVerification.Value
                         && (!string.Equals(scenario.EvidenceSource, "real_azure", StringComparison.OrdinalIgnoreCase)
                             || !IsPositiveVerificationCategory(scenario.Category)))
                {
                    Err($"{prefix}.establishes_verification may be true only for positive real-Azure core, read, write, list, pagination, batch, or concurrency scenarios");
                }
                if (string.IsNullOrWhiteSpace(scenario.Description))
                {
                    Err($"{prefix}.description missing");
                }
                var scenarioOperations = scenario.Operations ?? [];
                var scenarioTests = scenario.Tests ?? [];
                if (scenarioOperations.Count == 0)
                {
                    Err($"{prefix}.operations must contain at least one operation");
                }
                if (scenarioTests.Count == 0)
                {
                    Err($"{prefix}.tests must contain at least one fully-qualified test identity");
                }

                var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var operationIndex = 0; operationIndex < scenarioOperations.Count; operationIndex++)
                {
                    var operation = scenarioOperations[operationIndex];
                    if (string.IsNullOrWhiteSpace(operation))
                    {
                        Err($"{prefix}.operations[{operationIndex}] is empty");
                    }
                    else if (!seenOperations.Add(operation))
                    {
                        Err($"{prefix} repeats operation '{operation}'");
                    }
                    else if (!knownOperations.Contains(operation))
                    {
                        Err($"{prefix} references unknown operation '{operation}' for service '{service}'");
                    }
                }

                var seenTests = new HashSet<string>(StringComparer.Ordinal);
                for (var testIndex = 0; testIndex < scenarioTests.Count; testIndex++)
                {
                    var test = scenarioTests[testIndex];
                    if (!IsFullyQualifiedTestIdentity(test))
                    {
                        Err($"{prefix}.tests[{testIndex}] must be a fully-qualified test identity without whitespace");
                    }
                    else if (!seenTests.Add(test))
                    {
                        Err($"{prefix} repeats test identity '{test}'");
                    }
                }
            }
        }

        foreach (var service in RealAzureConformanceValues.RegisteredServices.OrderBy(v => v, StringComparer.Ordinal))
        {
            if (!seenServices.Contains(service))
            {
                Err($"missing registered service '{service}'");
            }
            if (!operationsByService.ContainsKey(service))
            {
                Err($"registered service '{service}' has no operation gap docs");
            }
        }

        return errors;
    }

    private static bool IsFullyQualifiedTestIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || identity.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var lastDot = identity.LastIndexOf('.');
        return lastDot > 0 && lastDot < identity.Length - 1 && identity.IndexOf('.') != lastDot;
    }

    private static bool IsPositiveVerificationCategory(string category) =>
        string.Equals(category, "core", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "read", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "write", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "list", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "pagination", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "batch", StringComparison.OrdinalIgnoreCase)
        || string.Equals(category, "concurrency", StringComparison.OrdinalIgnoreCase);
}
