using Aws2Azure.GapDocs;
using System.Text.Json;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class ConformancePlanTests
{
    [Fact]
    public void Generate_selects_service_and_groups_deduplicated_test_identities_by_project()
    {
        var matrix = Matrix();

        var plan = ConformancePlanGenerator.Generate(matrix, service: "S3");

        Assert.Equal("s3", plan.Selection.Service);
        Assert.Null(plan.Selection.Scenario);
        Assert.True(plan.HasPositiveRealAzureEvidence);
        Assert.Equal(3, plan.Scenarios.Count);
        Assert.Equal(2, plan.Operations.Count);
        Assert.Collection(
            plan.TestProjects,
            project =>
            {
                Assert.Equal("tests/Aws2Azure.IntegrationTests", project.Project);
                Assert.Equal(
                    [
                        "Aws2Azure.IntegrationTests.S3.Tests.Invalid_credentials",
                        "Aws2Azure.IntegrationTests.S3.Tests.Object_lifecycle",
                        "Aws2Azure.IntegrationTests.Shared.Tests.Retryable_error"
                    ],
                    project.Tests);
            },
            project =>
            {
                Assert.Equal("tests/Aws2Azure.UnitTests", project.Project);
                Assert.Equal(
                    ["Aws2Azure.UnitTests.S3.Tests.Retry_mapping"],
                    project.Tests);
            });
    }

    [Fact]
    public void Generate_selects_unique_scenario_without_service()
    {
        var plan = ConformancePlanGenerator.Generate(Matrix(), scenario: "message-lifecycle");

        var scenario = Assert.Single(plan.Scenarios);
        Assert.Equal("sqs", scenario.Service);
        Assert.Equal("message-lifecycle", plan.Selection.Scenario);
        Assert.True(plan.HasPositiveRealAzureEvidence);
    }

    [Fact]
    public void Generate_rejects_ambiguous_or_unknown_selectors()
    {
        var matrix = Matrix();

        var ambiguous = Assert.Throws<ArgumentException>(
            () => ConformancePlanGenerator.Generate(matrix, scenario: "invalid-credentials"));
        Assert.Contains("ambiguous", ambiguous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--service", ambiguous.Message, StringComparison.Ordinal);

        var unknownService = Assert.Throws<ArgumentException>(
            () => ConformancePlanGenerator.Generate(matrix, service: "missing"));
        Assert.Contains("Unknown conformance service 'missing'", unknownService.Message, StringComparison.Ordinal);

        var unknownScenario = Assert.Throws<ArgumentException>(
            () => ConformancePlanGenerator.Generate(matrix, service: "s3", scenario: "missing"));
        Assert.Contains(
            "Unknown conformance scenario 'missing' for service 's3'",
            unknownScenario.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_marks_deterministic_only_selection_as_not_positive_real_azure_evidence()
    {
        var plan = ConformancePlanGenerator.Generate(
            Matrix(),
            service: "s3",
            scenario: "retryable-failure");

        Assert.False(plan.HasPositiveRealAzureEvidence);
        Assert.All(plan.Scenarios, scenario => Assert.Equal("deterministic", scenario.EvidenceSource));
    }

    [Fact]
    public void RenderJson_uses_deterministic_machine_readable_shape()
    {
        var plan = ConformancePlanGenerator.Generate(
            Matrix(),
            service: "s3",
            scenario: "object-lifecycle");

        using var json = JsonDocument.Parse(ConformancePlanRenderer.RenderJson(plan));

        Assert.Equal(1, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("s3", json.RootElement.GetProperty("selection").GetProperty("service").GetString());
        Assert.True(json.RootElement.GetProperty("has_positive_real_azure_evidence").GetBoolean());
        Assert.Equal(
            "tests/Aws2Azure.IntegrationTests",
            json.RootElement.GetProperty("test_projects")[0].GetProperty("project").GetString());
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
                        Id = "object-lifecycle",
                        Priority = "p0",
                        Category = "core",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = true,
                        Operations = ["PutObject"],
                        Tests =
                        [
                            "Aws2Azure.IntegrationTests.S3.Tests.Object_lifecycle",
                            "Aws2Azure.IntegrationTests.Shared.Tests.Retryable_error"
                        ]
                    },
                    new RealAzureScenario
                    {
                        Id = "retryable-failure",
                        Priority = "p1",
                        Category = "service_unavailable",
                        EvidenceSource = "deterministic",
                        EstablishesVerification = false,
                        Operations = ["ListBuckets"],
                        Tests =
                        [
                            "Aws2Azure.IntegrationTests.Shared.Tests.Retryable_error",
                            "Aws2Azure.UnitTests.S3.Tests.Retry_mapping"
                        ]
                    },
                    new RealAzureScenario
                    {
                        Id = "invalid-credentials",
                        Priority = "p0",
                        Category = "invalid_credentials",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = false,
                        Operations = ["ListBuckets"],
                        Tests = ["Aws2Azure.IntegrationTests.S3.Tests.Invalid_credentials"]
                    }
                ]
            },
            new RealAzureService
            {
                Service = "sqs",
                Scenarios =
                [
                    new RealAzureScenario
                    {
                        Id = "message-lifecycle",
                        Priority = "p0",
                        Category = "core",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = true,
                        Operations = ["SendMessage"],
                        Tests = ["Aws2Azure.IntegrationTests.Sqs.Tests.Message_lifecycle"]
                    },
                    new RealAzureScenario
                    {
                        Id = "invalid-credentials",
                        Priority = "p0",
                        Category = "invalid_credentials",
                        EvidenceSource = "real_azure",
                        EstablishesVerification = false,
                        Operations = ["ListQueues"],
                        Tests = ["Aws2Azure.IntegrationTests.Sqs.Tests.Invalid_credentials"]
                    }
                ]
            }
        ]
    };
}
