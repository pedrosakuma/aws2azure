using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.GapDocs;

public sealed class ConformanceExecutionPlan
{
    public int SchemaVersion { get; set; } = 1;
    public ConformancePlanSelection Selection { get; set; } = new();
    public bool HasPositiveRealAzureEvidence { get; set; }
    public List<PlannedConformanceScenario> Scenarios { get; set; } = new();
    public List<PlannedConformanceOperation> Operations { get; set; } = new();
    public List<ConformanceTestProjectPlan> TestProjects { get; set; } = new();
}

public sealed class ConformancePlanSelection
{
    public string? Service { get; set; }
    public string? Scenario { get; set; }
    public string? Profile { get; set; }
    public bool IsFiltered { get; set; }
}

public sealed class PlannedConformanceScenario
{
    public string Service { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public bool EstablishesVerification { get; set; }
    public bool OptionalCoverage { get; set; }
    public List<string> Operations { get; set; } = new();
    public List<string> Tests { get; set; } = new();
}

public sealed class PlannedConformanceOperation
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
}

public sealed class ConformanceTestProjectPlan
{
    public string Project { get; set; } = string.Empty;
    public List<string> Tests { get; set; } = new();
}

public sealed class ConformanceMatrixSelection
{
    public RealAzureConformanceMatrix Matrix { get; set; } = new();
    public string? Service { get; set; }
    public string? Scenario { get; set; }
}

public static class ConformanceMatrixSelector
{
    public static ConformanceMatrixSelection Select(
        RealAzureConformanceMatrix matrix,
        string? service = null,
        string? scenario = null)
    {
        service = NormalizeSelector(service);
        scenario = NormalizeSelector(scenario);

        var selectedServices = SelectServices(matrix, service);
        var selectedScenarios = selectedServices
            .SelectMany(
                matrixService => matrixService.Scenarios.Select(
                    matrixScenario => (Service: matrixService.Service, Scenario: matrixScenario)))
            .Where(entry => scenario is null
                            || string.Equals(entry.Scenario.Id, scenario, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (scenario is not null && selectedScenarios.Count == 0)
        {
            throw new ArgumentException(
                service is null
                    ? $"Unknown conformance scenario '{scenario}'."
                    : $"Unknown conformance scenario '{scenario}' for service '{service}'.",
                nameof(scenario));
        }

        if (scenario is not null && service is null)
        {
            var matchingServices = selectedScenarios
                .Select(entry => entry.Service)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            if (matchingServices.Count > 1)
            {
                throw new ArgumentException(
                    $"Conformance scenario '{scenario}' is ambiguous; specify --service. " +
                    $"Matching services: {string.Join(", ", matchingServices)}.",
                    nameof(scenario));
            }
        }

        var selectedMatrix = new RealAzureConformanceMatrix
        {
            SchemaVersion = matrix.SchemaVersion,
            SourceFile = matrix.SourceFile,
            Services = selectedScenarios
                .GroupBy(entry => entry.Service, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new RealAzureService
                {
                    Service = group.First().Service,
                    Scenarios = group
                        .Select(entry => entry.Scenario)
                        .OrderBy(entry => entry.Id, StringComparer.Ordinal)
                        .ToList()
                })
                .ToList()
        };

        return new ConformanceMatrixSelection
        {
            Matrix = selectedMatrix,
            Service = service,
            Scenario = scenario
        };
    }

    private static IReadOnlyList<RealAzureService> SelectServices(
        RealAzureConformanceMatrix matrix,
        string? service)
    {
        if (service is null)
        {
            return matrix.Services;
        }

        var selected = matrix.Services
            .Where(entry => string.Equals(entry.Service, service, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (selected.Count == 0)
        {
            throw new ArgumentException($"Unknown conformance service '{service}'.", nameof(service));
        }

        return selected;
    }

    private static string? NormalizeSelector(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

public static class ConformancePlanGenerator
{
    private const string IntegrationTestPrefix = "Aws2Azure.IntegrationTests.";
    private const string UnitTestPrefix = "Aws2Azure.UnitTests.";
    private const string IntegrationTestProject = "tests/Aws2Azure.IntegrationTests";
    private const string UnitTestProject = "tests/Aws2Azure.UnitTests";

    public static ConformanceExecutionPlan Generate(
        RealAzureConformanceMatrix matrix,
        string? service = null,
        string? scenario = null,
        IReadOnlySet<string>? excludedScenarioIds = null)
    {
        var selection = ConformanceMatrixSelector.Select(matrix, service, scenario);
        var exclusions = excludedScenarioIds is null
            ? null
            : new HashSet<string>(excludedScenarioIds, StringComparer.OrdinalIgnoreCase);
        var orderedScenarios = selection.Matrix.Services
            .SelectMany(
                matrixService => matrixService.Scenarios.Select(
                    matrixScenario => (Service: matrixService.Service, Scenario: matrixScenario)))
            .Where(entry => exclusions is null || !exclusions.Contains(entry.Scenario.Id))
            .OrderBy(entry => entry.Service, StringComparer.Ordinal)
            .ThenBy(entry => entry.Scenario.Id, StringComparer.Ordinal)
            .ToList();
        var plan = new ConformanceExecutionPlan
        {
            Selection = new ConformancePlanSelection
            {
                Service = selection.Service,
                Scenario = selection.Scenario
            },
            HasPositiveRealAzureEvidence = orderedScenarios.Any(
                entry => entry.Scenario.EstablishesVerification == true
                         && string.Equals(
                             entry.Scenario.EvidenceSource,
                             "real_azure",
                             StringComparison.OrdinalIgnoreCase))
        };

        foreach (var entry in orderedScenarios)
        {
            plan.Scenarios.Add(new PlannedConformanceScenario
            {
                Service = entry.Service,
                Id = entry.Scenario.Id,
                Priority = entry.Scenario.Priority.ToLowerInvariant(),
                Category = entry.Scenario.Category.ToLowerInvariant(),
                EvidenceSource = entry.Scenario.EvidenceSource.ToLowerInvariant(),
                EstablishesVerification = entry.Scenario.EstablishesVerification == true,
                OptionalCoverage = entry.Scenario.OptionalCoverage == true,
                Operations = entry.Scenario.Operations
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
                Tests = entry.Scenario.Tests
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList()
            });
        }

        plan.Operations = orderedScenarios
            .SelectMany(entry => entry.Scenario.Operations.Select(
                operation => new PlannedConformanceOperation
                {
                    Service = entry.Service,
                    Operation = operation
                }))
            .DistinctBy(
                operation => $"{operation.Service}\0{operation.Operation}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(operation => operation.Service, StringComparer.Ordinal)
            .ThenBy(operation => operation.Operation, StringComparer.Ordinal)
            .ToList();

        var tests = orderedScenarios
            .SelectMany(entry => entry.Scenario.Tests)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        AddProjectPlan(plan, tests, IntegrationTestPrefix, IntegrationTestProject);
        AddProjectPlan(plan, tests, UnitTestPrefix, UnitTestProject);

        var unknownTests = tests
            .Where(test => !test.StartsWith(IntegrationTestPrefix, StringComparison.Ordinal)
                           && !test.StartsWith(UnitTestPrefix, StringComparison.Ordinal))
            .ToList();
        if (unknownTests.Count > 0)
        {
            throw new InvalidOperationException(
                "Conformance tests must belong to Aws2Azure.IntegrationTests or Aws2Azure.UnitTests: " +
                string.Join(", ", unknownTests));
        }

        return plan;
    }

    public static RealAzureConformanceMatrix SelectPlannedMatrix(
        RealAzureConformanceMatrix matrix,
        ConformanceExecutionPlan plan)
    {
        var planned = plan.Scenarios
            .Select(scenario => $"{scenario.Service}\0{scenario.Id}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new RealAzureConformanceMatrix
        {
            SchemaVersion = matrix.SchemaVersion,
            SourceFile = matrix.SourceFile
        };
        foreach (var service in matrix.Services)
        {
            var scenarios = service.Scenarios
                .Where(scenario => planned.Contains($"{service.Service}\0{scenario.Id}"))
                .ToList();
            if (scenarios.Count == 0) continue;
            selected.Services.Add(new RealAzureService
            {
                Service = service.Service,
                Scenarios = scenarios
            });
        }
        if (selected.Services.Sum(service => service.Scenarios.Count) != plan.Scenarios.Count)
            throw new InvalidDataException("Conformance plan references scenarios not present in the matrix.");
        return selected;
    }

    private static void AddProjectPlan(
        ConformanceExecutionPlan plan,
        IReadOnlyList<string> tests,
        string prefix,
        string project)
    {
        var projectTests = tests
            .Where(test => test.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (projectTests.Count == 0)
        {
            return;
        }

        plan.TestProjects.Add(new ConformanceTestProjectPlan
        {
            Project = project,
            Tests = projectTests
        });
    }

}

public static class ConformancePlanRenderer
{
    public static string RenderJson(ConformanceExecutionPlan plan) =>
        JsonSerializer.Serialize(plan, ConformancePlanJsonContext.Default.ConformanceExecutionPlan);

    public static ConformanceExecutionPlan ParseJson(string path) =>
        JsonSerializer.Deserialize(
            File.ReadAllText(path),
            ConformancePlanJsonContext.Default.ConformanceExecutionPlan)
        ?? throw new InvalidDataException($"{path}: empty conformance plan");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(ConformanceExecutionPlan))]
internal sealed partial class ConformancePlanJsonContext : JsonSerializerContext
{
}
