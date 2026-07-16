using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aws2Azure.GapDocs;

public sealed class ConformanceEvidence
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = string.Empty;
    public string RunUrl { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<string> TrxFiles { get; set; } = new();
    public EvidenceTotals Summary { get; set; } = new();
    public List<ServiceEvidence> Services { get; set; } = new();
    public List<string> UnmappedTests { get; set; } = new();
}

public sealed class ServiceEvidence
{
    public string Service { get; set; } = string.Empty;
    public EvidenceTotals Summary { get; set; } = new();
    public List<ScenarioEvidence> Scenarios { get; set; } = new();
    public List<OperationEvidence> Operations { get; set; } = new();
}

public sealed class ScenarioEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public bool EstablishesVerification { get; set; }
    public bool OptionalCoverage { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
    public List<TestEvidence> Tests { get; set; } = new();
    public string Outcome { get; set; } = string.Empty;
    public double DurationMilliseconds { get; set; }
}

public sealed class TestEvidence
{
    public string Identity { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public int Executions { get; set; }
    public double DurationMilliseconds { get; set; }
}

public sealed class OperationEvidence
{
    public string Operation { get; set; } = string.Empty;
    public bool EligibleForVerifiedRealAzure { get; set; }
    public List<string> Scenarios { get; set; } = new();
    public List<string> BlockingOutcomes { get; set; } = new();
}

public sealed class EvidenceTotals
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int NotRun { get; set; }
    public double DurationMilliseconds { get; set; }
}

public static class ConformanceEvidenceGenerator
{
    public static ConformanceEvidence Generate(
        RealAzureConformanceMatrix matrix,
        IReadOnlyList<TrxTestResult> trxResults,
        string runId,
        string runUrl,
        DateTimeOffset? generatedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run ID must not be empty.", nameof(runId));
        }
        if (!Uri.TryCreate(runUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Run URL must be an absolute HTTP(S) URL.", nameof(runUrl));
        }

        var resultsByIdentity = trxResults
            .GroupBy(r => r.TestIdentity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TrxTestResult>)g.ToList(), StringComparer.Ordinal);
        var mappedIdentities = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new ConformanceEvidence
        {
            RunId = runId,
            RunUrl = runUrl,
            GeneratedAtUtc = (generatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            TrxFiles = trxResults.Select(r => Path.GetFileName(r.SourceFile)).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList()
        };

        foreach (var matrixService in matrix.Services.OrderBy(s => s.Service, StringComparer.Ordinal))
        {
            var service = new ServiceEvidence { Service = matrixService.Service };
            var serviceTests = new Dictionary<string, TestEvidence>(StringComparer.Ordinal);

            foreach (var matrixScenario in matrixService.Scenarios)
            {
                var scenario = new ScenarioEvidence
                {
                    Id = matrixScenario.Id,
                    Priority = matrixScenario.Priority.ToLowerInvariant(),
                    Category = matrixScenario.Category.ToLowerInvariant(),
                    EvidenceSource = matrixScenario.EvidenceSource.ToLowerInvariant(),
                    EstablishesVerification = matrixScenario.EstablishesVerification == true,
                    OptionalCoverage = matrixScenario.OptionalCoverage == true,
                    Description = matrixScenario.Description,
                    Operations = matrixScenario.Operations.ToList()
                };

                foreach (var identity in matrixScenario.Tests)
                {
                    mappedIdentities.Add(identity);
                    var test = AggregateTest(identity, resultsByIdentity);
                    scenario.Tests.Add(test);
                    serviceTests.TryAdd(identity, test);
                }

                var scenarioOutcome = AggregateOutcome(scenario.Tests.Select(t => ParseOutcome(t.Outcome)));
                scenario.Outcome = FormatOutcome(scenarioOutcome);
                scenario.DurationMilliseconds = scenario.Tests.Sum(t => t.DurationMilliseconds);
                service.Scenarios.Add(scenario);
            }

            service.Summary = CalculateTotals(serviceTests.Values);
            service.Operations = BuildOperations(service.Scenarios);
            evidence.Services.Add(service);
        }

        evidence.Summary = CalculateTotals(
            evidence.Services.SelectMany(s => s.Scenarios.SelectMany(sc => sc.Tests))
                .GroupBy(t => t.Identity, StringComparer.Ordinal)
                .Select(g => g.First()));
        evidence.UnmappedTests = trxResults.Select(r => r.TestIdentity)
            .Where(identity => !mappedIdentities.Contains(identity))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToList();
        return evidence;
    }

    private static TestEvidence AggregateTest(
        string identity,
        IReadOnlyDictionary<string, IReadOnlyList<TrxTestResult>> resultsByIdentity)
    {
        if (!resultsByIdentity.TryGetValue(identity, out var results))
        {
            return new TestEvidence
            {
                Identity = identity,
                Outcome = FormatOutcome(ConformanceOutcome.NotRun)
            };
        }

        return new TestEvidence
        {
            Identity = identity,
            Outcome = FormatOutcome(AggregateOutcome(results.Select(r => r.Outcome))),
            Executions = results.Count,
            DurationMilliseconds = results.Sum(r => r.Duration.TotalMilliseconds)
        };
    }

    private static List<OperationEvidence> BuildOperations(IReadOnlyList<ScenarioEvidence> scenarios)
    {
        return scenarios
            .Where(scenario => !scenario.OptionalCoverage)
            .SelectMany(s => s.Operations.Select(operation => (Operation: operation, Scenario: s)))
            .GroupBy(pair => pair.Operation, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var relevantScenarios = group.Select(pair => pair.Scenario).ToList();
                var allPassed = relevantScenarios.All(
                    scenario => ParseOutcome(scenario.Outcome) == ConformanceOutcome.Passed);
                var hasPositiveRealAzureEvidence = relevantScenarios.Any(
                    scenario => scenario.EvidenceSource == "real_azure"
                        && scenario.EstablishesVerification
                        && ParseOutcome(scenario.Outcome) == ConformanceOutcome.Passed);
                var blockingOutcomes = relevantScenarios
                    .Where(s => ParseOutcome(s.Outcome) != ConformanceOutcome.Passed)
                    .Select(s => $"{s.Id}:{s.Outcome}")
                    .ToList();
                if (!hasPositiveRealAzureEvidence)
                {
                    blockingOutcomes.Add("no_positive_real_azure_evidence");
                }
                return new OperationEvidence
                {
                    Operation = group.First().Operation,
                    EligibleForVerifiedRealAzure = relevantScenarios.Count > 0
                        && allPassed
                        && hasPositiveRealAzureEvidence,
                    Scenarios = relevantScenarios.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    BlockingOutcomes = blockingOutcomes
                };
            })
            .ToList();
    }

    private static EvidenceTotals CalculateTotals(IEnumerable<TestEvidence> tests)
    {
        var totals = new EvidenceTotals();
        foreach (var test in tests)
        {
            switch (ParseOutcome(test.Outcome))
            {
                case ConformanceOutcome.Passed: totals.Passed++; break;
                case ConformanceOutcome.Failed: totals.Failed++; break;
                case ConformanceOutcome.Skipped: totals.Skipped++; break;
                case ConformanceOutcome.NotRun: totals.NotRun++; break;
            }
            totals.DurationMilliseconds += test.DurationMilliseconds;
        }
        return totals;
    }

    private static ConformanceOutcome AggregateOutcome(IEnumerable<ConformanceOutcome> outcomes)
    {
        var values = outcomes.ToList();
        if (values.Contains(ConformanceOutcome.Failed)) return ConformanceOutcome.Failed;
        if (values.Contains(ConformanceOutcome.Skipped)) return ConformanceOutcome.Skipped;
        if (values.Contains(ConformanceOutcome.NotRun)) return ConformanceOutcome.NotRun;
        return ConformanceOutcome.Passed;
    }

    internal static string FormatOutcome(ConformanceOutcome outcome) => outcome switch
    {
        ConformanceOutcome.Passed => "passed",
        ConformanceOutcome.Failed => "failed",
        ConformanceOutcome.Skipped => "skipped",
        ConformanceOutcome.NotRun => "not_run",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static ConformanceOutcome ParseOutcome(string outcome) => outcome switch
    {
        "passed" => ConformanceOutcome.Passed,
        "failed" => ConformanceOutcome.Failed,
        "skipped" => ConformanceOutcome.Skipped,
        "not_run" => ConformanceOutcome.NotRun,
        _ => throw new InvalidDataException($"Unknown evidence outcome '{outcome}'")
    };
}

public static class ConformanceEvidenceRenderer
{
    public static void Render(ConformanceEvidence evidence, string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var servicesRoot = Path.Combine(outputRoot, "services");
        Directory.CreateDirectory(servicesRoot);

        File.WriteAllText(
            Path.Combine(outputRoot, "real-azure-evidence.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonContext.Default.ConformanceEvidence) + Environment.NewLine);
        File.WriteAllText(Path.Combine(outputRoot, "summary.md"), RenderSummary(evidence));
        foreach (var service in evidence.Services)
        {
            File.WriteAllText(Path.Combine(servicesRoot, service.Service + ".md"), RenderService(evidence, service));
        }
    }

    public static string RenderSummary(ConformanceEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Real-Azure conformance evidence");
        builder.AppendLine();
        builder.Append("- Run: [").Append(Escape(evidence.RunId)).Append("](").Append(evidence.RunUrl).AppendLine(")");
        builder.Append("- Generated: `").Append(evidence.GeneratedAtUtc.ToString("O")).AppendLine("`");
        builder.AppendLine("- This report is evidence only. It does **not** edit `verified_real_azure`.");
        builder.AppendLine("- Outcome totals combine `real_azure` and `deterministic` scenario tests; see each service report for the source.");
        builder.AppendLine();
        builder.AppendLine("| Service | Passed | Failed | Skipped | Not run | Duration | Eligible operations |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var service in evidence.Services)
        {
            builder.Append("| [").Append(Escape(service.Service)).Append("](services/").Append(service.Service).Append(".md) | ")
                .Append(service.Summary.Passed).Append(" | ")
                .Append(service.Summary.Failed).Append(" | ")
                .Append(service.Summary.Skipped).Append(" | ")
                .Append(service.Summary.NotRun).Append(" | ")
                .Append(FormatDuration(service.Summary.DurationMilliseconds)).Append(" | ")
                .Append(service.Operations.Count(o => o.EligibleForVerifiedRealAzure)).AppendLine(" |");
        }
        builder.AppendLine();
        builder.AppendLine("An operation is eligible only when every blocking matrix scenario that references it passed and at least one passing scenario has `real_azure` evidence with `establishes_verification: true`. Failure-only and deterministic scenarios never establish verification; explicitly non-blocking optional coverage is reported but does not affect operation eligibility. Without positive live-Azure evidence, the blocker is `no_positive_real_azure_evidence`.");
        return builder.ToString();
    }

    public static string RenderService(ConformanceEvidence evidence, ServiceEvidence service)
    {
        var builder = new StringBuilder();
        builder.Append("# Real-Azure conformance: ").AppendLine(Escape(service.Service));
        builder.AppendLine();
        builder.Append("- Run: [").Append(Escape(evidence.RunId)).Append("](").Append(evidence.RunUrl).AppendLine(")");
        builder.Append("- Test results across real-Azure and deterministic scenarios: **").Append(service.Summary.Passed).Append(" passed**, **")
            .Append(service.Summary.Failed).Append(" failed**, **")
            .Append(service.Summary.Skipped).Append(" skipped**, **")
            .Append(service.Summary.NotRun).AppendLine(" not run**");
        builder.Append("- Duration: **").Append(FormatDuration(service.Summary.DurationMilliseconds)).AppendLine("**");
        builder.AppendLine("- Evidence only: `verified_real_azure` is never modified automatically.");
        builder.AppendLine("- Eligibility requires every blocking referencing scenario to pass and at least one passing `real_azure` scenario with `establishes_verification: true`; explicitly non-blocking optional coverage is reported but does not affect eligibility.");
        builder.AppendLine();
        builder.AppendLine("## Scenarios");
        builder.AppendLine();
        builder.AppendLine("| Priority | Category | Evidence source | Establishes verification | Optional coverage | Scenario | Outcome | Duration | Operations |");
        builder.AppendLine("|---|---|---|---|---|---|---|---:|---|");
        foreach (var scenario in service.Scenarios)
        {
            builder.Append("| ").Append(Escape(scenario.Priority.ToUpperInvariant())).Append(" | ")
                .Append(Escape(scenario.Category)).Append(" | ")
                .Append(Escape(scenario.EvidenceSource)).Append(" | ")
                .Append(scenario.EstablishesVerification ? "yes" : "no").Append(" | ")
                .Append(scenario.OptionalCoverage ? "yes" : "no").Append(" | ")
                .Append(Escape(scenario.Id)).Append(" | ")
                .Append(OutcomeLabel(scenario.Outcome)).Append(" | ")
                .Append(FormatDuration(scenario.DurationMilliseconds)).Append(" | ")
                .Append(Escape(string.Join(", ", scenario.Operations))).AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("## Tests");
        builder.AppendLine();
        builder.AppendLine("| Test identity | Outcome | Executions | Duration |");
        builder.AppendLine("|---|---|---:|---:|");
        foreach (var test in service.Scenarios.SelectMany(s => s.Tests)
                     .GroupBy(t => t.Identity, StringComparer.Ordinal)
                     .Select(group => group.First())
                     .OrderBy(t => t.Identity, StringComparer.Ordinal))
        {
            builder.Append("| `").Append(Escape(test.Identity)).Append("` | ")
                .Append(OutcomeLabel(test.Outcome)).Append(" | ")
                .Append(test.Executions).Append(" | ")
                .Append(FormatDuration(test.DurationMilliseconds)).AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("## Operation eligibility");
        builder.AppendLine();
        builder.AppendLine("| Operation | Eligible for `verified_real_azure` | Scenarios | Blocking evidence |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var operation in service.Operations)
        {
            builder.Append("| ").Append(Escape(operation.Operation)).Append(" | ")
                .Append(operation.EligibleForVerifiedRealAzure ? "✅ yes" : "❌ no").Append(" | ")
                .Append(Escape(string.Join(", ", operation.Scenarios))).Append(" | ")
                .Append(operation.BlockingOutcomes.Count == 0 ? "—" : Escape(string.Join(", ", operation.BlockingOutcomes)))
                .AppendLine(" |");
        }
        return builder.ToString();
    }

    private static string OutcomeLabel(string outcome) => outcome switch
    {
        "passed" => "✅ passed",
        "failed" => "❌ failed",
        "skipped" => "⏭️ skipped",
        "not_run" => "◻️ not run",
        _ => Escape(outcome)
    };

    private static string FormatDuration(double milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\.fff");

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(ConformanceEvidence))]
internal sealed partial class EvidenceJsonContext : JsonSerializerContext
{
}
