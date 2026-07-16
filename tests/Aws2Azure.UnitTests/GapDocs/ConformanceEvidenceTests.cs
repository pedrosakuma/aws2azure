using Aws2Azure.GapDocs;
using System.Text.Json;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class ConformanceEvidenceTests
{
    [Fact]
    public void Generate_aggregates_results_and_computes_conservative_operation_eligibility()
    {
        var matrix = Matrix();
        var results = new TrxTestResult[]
        {
            new("Tests.Core.Passes", ConformanceOutcome.Passed, TimeSpan.FromSeconds(1), "one.trx"),
            new("Tests.Core.Passes", ConformanceOutcome.Passed, TimeSpan.FromSeconds(2), "retry.trx"),
            new("Tests.Core.Fails", ConformanceOutcome.Passed, TimeSpan.FromMilliseconds(250), "one.trx"),
            new("Tests.Core.Fails", ConformanceOutcome.Failed, TimeSpan.FromMilliseconds(500), "retry.trx"),
            new("Tests.Unmapped.Result", ConformanceOutcome.Passed, TimeSpan.Zero, "one.trx")
        };

        var evidence = ConformanceEvidenceGenerator.Generate(
            matrix,
            results,
            "123",
            "https://github.com/example/repo/actions/runs/123",
            DateTimeOffset.Parse("2026-07-15T12:00:00Z"));

        var service = Assert.Single(evidence.Services);
        Assert.Equal(1, service.Summary.Passed);
        Assert.Equal(1, service.Summary.Failed);
        Assert.Equal(1, service.Summary.NotRun);
        Assert.Equal(3750, service.Summary.DurationMilliseconds);
        Assert.Equal("failed", service.Scenarios[0].Outcome);
        Assert.Equal("not_run", service.Scenarios[1].Outcome);
        Assert.False(service.Operations.Single(operation => operation.Operation == "GetObject").EligibleForVerifiedRealAzure);
        Assert.True(service.Operations.Single(operation => operation.Operation == "PutObject").EligibleForVerifiedRealAzure);
        var deterministicOnly = service.Operations.Single(operation => operation.Operation == "ListBuckets");
        Assert.False(deterministicOnly.EligibleForVerifiedRealAzure);
        Assert.Equal(["no_positive_real_azure_evidence"], deterministicOnly.BlockingOutcomes);
        Assert.Equal(["Tests.Unmapped.Result"], evidence.UnmappedTests);
    }

    [Fact]
    public void Generate_requires_all_scenarios_to_pass_even_with_passing_real_azure_evidence()
    {
        var matrix = Matrix();
        matrix.Services[0].Scenarios.Add(new RealAzureScenario
        {
            Id = "put-deterministic-guard",
            Priority = "p1",
            Category = "service_unavailable",
            EvidenceSource = "deterministic",
            EstablishesVerification = false,
            Description = "Injected service unavailability.",
            Operations = ["PutObject"],
            Tests = ["Tests.Deterministic.Fails"]
        });

        var evidence = ConformanceEvidenceGenerator.Generate(
            matrix,
            [
                new("Tests.Core.Passes", ConformanceOutcome.Passed, TimeSpan.Zero, "run.trx"),
                new("Tests.Deterministic.Fails", ConformanceOutcome.Failed, TimeSpan.Zero, "run.trx")
            ],
            "run-guard",
            "https://github.com/example/repo/actions/runs/guard");

        var operation = Assert.Single(
            Assert.Single(evidence.Services).Operations,
            operation => operation.Operation == "PutObject");
        Assert.False(operation.EligibleForVerifiedRealAzure);
        Assert.Equal(["put-deterministic-guard:failed"], operation.BlockingOutcomes);
    }

    [Fact]
    public void Generate_reports_non_blocking_optional_scenario_without_affecting_operation_eligibility()
    {
        var matrix = Matrix();
        var optionalScenario = matrix.Services[0].Scenarios.Single(scenario => scenario.Id == "missing");
        optionalScenario.EvidenceSource = "real_azure";
        optionalScenario.Category = "read";
        optionalScenario.OptionalCoverage = true;

        var evidence = ConformanceEvidenceGenerator.Generate(
            matrix,
            [
                new("Tests.Core.Passes", ConformanceOutcome.Passed, TimeSpan.Zero, "run.trx"),
                new("Tests.Core.Fails", ConformanceOutcome.Passed, TimeSpan.Zero, "run.trx")
            ],
            "run-optional",
            "https://github.com/example/repo/actions/runs/optional");

        var service = Assert.Single(evidence.Services);
        var optional = Assert.Single(service.Scenarios, scenario => scenario.Id == "missing");
        Assert.True(optional.OptionalCoverage);
        Assert.Equal("not_run", optional.Outcome);

        var operation = Assert.Single(
            service.Operations,
            operation => operation.Operation == "GetObject");
        Assert.True(operation.EligibleForVerifiedRealAzure);
        Assert.Equal(["core"], operation.Scenarios);
        Assert.Empty(operation.BlockingOutcomes);
    }

    [Fact]
    public void Render_includes_run_totals_scenarios_and_eligibility_without_mutation_claim()
    {
        var evidence = ConformanceEvidenceGenerator.Generate(
            Matrix(),
            [new("Tests.Core.Passes", ConformanceOutcome.Passed, TimeSpan.FromSeconds(1), "run.trx")],
            "run-7",
            "https://github.com/example/repo/actions/runs/7",
            DateTimeOffset.Parse("2026-07-15T12:00:00Z"));
        var service = Assert.Single(evidence.Services);

        var summary = ConformanceEvidenceRenderer.RenderSummary(evidence);
        var serviceReport = ConformanceEvidenceRenderer.RenderService(evidence, service);

        Assert.Contains("[run-7](https://github.com/example/repo/actions/runs/7)", summary, StringComparison.Ordinal);
        Assert.Contains("# Real-Azure conformance: s3", serviceReport, StringComparison.Ordinal);
        Assert.Contains("| real_azure |", serviceReport, StringComparison.Ordinal);
        Assert.Contains("| deterministic |", serviceReport, StringComparison.Ordinal);
        Assert.Contains("✅ passed", serviceReport, StringComparison.Ordinal);
        Assert.Contains("❌ no", serviceReport, StringComparison.Ordinal);
        Assert.Contains("no_positive_real_azure_evidence", serviceReport, StringComparison.Ordinal);
        Assert.Contains("failure-only and deterministic scenarios", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never modified automatically", serviceReport, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_writes_machine_readable_evidence_and_per_service_report()
    {
        var evidence = ConformanceEvidenceGenerator.Generate(
            Matrix(),
            [new("Tests.Core.Passes", ConformanceOutcome.Passed, TimeSpan.FromSeconds(1), "run.trx")],
            "run-8",
            "https://github.com/example/repo/actions/runs/8",
            DateTimeOffset.Parse("2026-07-15T12:00:00Z"));
        var output = Path.Combine(AppContext.BaseDirectory, $"conformance-evidence-{Guid.NewGuid():N}");

        try
        {
            ConformanceEvidenceRenderer.Render(evidence, output);

            var jsonPath = Path.Combine(output, "real-azure-evidence.json");
            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(Path.Combine(output, "summary.md")));
            Assert.True(File.Exists(Path.Combine(output, "services", "s3.md")));
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
            Assert.Equal("run-8", json.RootElement.GetProperty("run_id").GetString());
            Assert.Equal("s3", json.RootElement.GetProperty("services")[0].GetProperty("service").GetString());
            Assert.Equal(
                "real_azure",
                json.RootElement.GetProperty("services")[0].GetProperty("scenarios")[0]
                    .GetProperty("evidence_source").GetString());
            Assert.True(
                json.RootElement.GetProperty("services")[0].GetProperty("scenarios")[0]
                    .GetProperty("establishes_verification").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static RealAzureConformanceMatrix Matrix() => new()
    {
        SchemaVersion = 1,
        Services =
        [
            new RealAzureService
            {
                Service = "s3",
                Scenarios =
                [
                    new RealAzureScenario
                    {
                        Id = "core",
                        Priority = "p0",
                        Category = "core",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = true,
                        Description = "Core read.",
                        Operations = ["GetObject"],
                        Tests = ["Tests.Core.Passes", "Tests.Core.Fails"]
                    },
                    new RealAzureScenario
                    {
                        Id = "missing",
                        Priority = "p1",
                        Category = "service_unavailable",
                        EvidenceSource = "deterministic",
                        EstablishesVerification = false,
                        Description = "Missing deterministic guard.",
                        Operations = ["GetObject"],
                        Tests = ["Tests.Future.Missing"]
                    },
                    new RealAzureScenario
                    {
                        Id = "write",
                        Priority = "p0",
                        Category = "write",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = true,
                        Description = "Current write.",
                        Operations = ["PutObject"],
                        Tests = ["Tests.Core.Passes"]
                    },
                    new RealAzureScenario
                    {
                        Id = "deterministic-only",
                        Priority = "p1",
                        Category = "throttling",
                        EvidenceSource = "deterministic",
                        EstablishesVerification = false,
                        Description = "Injected throttling.",
                        Operations = ["ListBuckets"],
                        Tests = ["Tests.Core.Passes"]
                    },
                    new RealAzureScenario
                    {
                        Id = "failure-only-real-azure",
                        Priority = "p0",
                        Category = "invalid_credentials",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = false,
                        Description = "Live-Azure failure-only probe.",
                        Operations = ["ListBuckets"],
                        Tests = ["Tests.Core.Passes"]
                    }
                ]
            }
        ]
    };
}
