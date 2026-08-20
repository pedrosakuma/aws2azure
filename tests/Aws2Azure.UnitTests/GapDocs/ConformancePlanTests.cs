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
    public void Generate_excludes_profile_specific_scenarios()
    {
        var plan = ConformancePlanGenerator.Generate(
            Matrix(),
            service: "sqs",
            excludedScenarioIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "message-lifecycle"
            });

        Assert.DoesNotContain(plan.Scenarios, scenario => scenario.Id == "message-lifecycle");
        Assert.DoesNotContain(
            plan.TestProjects.SelectMany(project => project.Tests),
            test => test.Contains("Message_lifecycle", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectPlannedMatrix_preserves_exact_scenario_set()
    {
        var matrix = Matrix();
        var plan = ConformancePlanGenerator.Generate(
            matrix,
            service: "sqs",
            excludedScenarioIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "message-lifecycle"
            });

        var selected = ConformancePlanGenerator.SelectPlannedMatrix(matrix, plan);

        var service = Assert.Single(selected.Services);
        Assert.Equal("sqs", service.Service);
        Assert.DoesNotContain(service.Scenarios, scenario => scenario.Id == "message-lifecycle");
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

    [Fact]
    public void Discovery_validation_requires_exact_xunit_identity_or_theory_case()
    {
        var plan = new ConformanceExecutionPlan
        {
            TestProjects =
            [
                new ConformanceTestProjectPlan
                {
                    Project = "tests/Aws2Azure.UnitTests",
                    Tests =
                    [
                        "Aws2Azure.UnitTests.SampleTests.Exact",
                        "Aws2Azure.UnitTests.SampleTests.Theory",
                        "Aws2Azure.UnitTests.SampleTests.Removed",
                    ],
                },
            ],
        };
        var discovered = new Dictionary<string, IReadOnlyList<string>>
        {
            ["tests/Aws2Azure.UnitTests"] =
            [
                "Aws2Azure.UnitTests.SampleTests.Exact",
                "Aws2Azure.UnitTests.SampleTests.Theory(value: 1)",
                "Aws2Azure.UnitTests.SampleTests.RemovedReplacement",
            ],
        };

        var errors = ConformanceTestDiscoveryValidator.Validate(
            plan,
            discovered);

        var error = Assert.Single(errors);
        Assert.Contains(
            "Aws2Azure.UnitTests.SampleTests.Removed",
            error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_output_parser_ignores_runner_headers()
    {
        var discovered = ConformanceTestDiscoveryValidator.ParseListTestsOutput(
            """
            The following Tests are available:
                Aws2Azure.UnitTests.SampleTests.Fact
                Aws2Azure.IntegrationTests.SampleTests.Theory(value: 1)
            Test Run Successful.
            """);

        Assert.Equal(
            [
                "Aws2Azure.UnitTests.SampleTests.Fact",
                "Aws2Azure.IntegrationTests.SampleTests.Theory(value: 1)",
            ],
            discovered);
    }

    [Fact]
    public void DynamoDb_profiles_have_distinct_pinned_sets_hashes_and_configuration()
    {
        var matrix = ConformanceMatrixLoader.Load(Path.Combine(
            RepositoryRoot(),
            "docs",
            "testing",
            "real-azure-conformance.yaml"));
        var basic = ConformancePlanGenerator.Generate(
            matrix,
            service: "dynamodb",
            profile: "dynamodb-basic-crud");
        var query = ConformancePlanGenerator.Generate(
            matrix,
            service: "dynamodb",
            profile: "dynamodb-query-scan-indexes");
        var transactions = ConformancePlanGenerator.Generate(
            matrix,
            service: "dynamodb",
            profile: "dynamodb-single-partition-transactions");

        Assert.Equal(
            "sha256:e94d1c42a36f8faa74b65ea53cb21773e6df04967565b8dd7468b04d09ba12c7",
            basic.ScenarioSetSha256);
        Assert.Equal(
            "sha256:72a44543a87d9eb1553d598c4b1aa8e9c1034e69115d9553dfeeca45af78dd21",
            query.ScenarioSetSha256);
        Assert.Equal(
            "sha256:3c5f7ee943ffb9fc50c2a3effce767c003f4dd10066c69b3381ef1b7af2acb51",
            transactions.ScenarioSetSha256);
        Assert.Equal("Disabled", basic.Configuration.DynamoDbStoredProcedureMode);
        Assert.Equal("Disabled", query.Configuration.DynamoDbStoredProcedureMode);
        Assert.Equal(
            "Preferred",
            transactions.Configuration.DynamoDbStoredProcedureMode);
        Assert.DoesNotContain(
            basic.Scenarios,
            scenario => scenario.Id.StartsWith(
                "transaction-",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            query.Scenarios,
            scenario => scenario.Id.StartsWith(
                "transaction-",
                StringComparison.Ordinal));
        Assert.All(
            transactions.Scenarios,
            scenario =>
            {
                Assert.True(
                    scenario.Id == "rollback"
                    || scenario.Id.StartsWith(
                        "transaction-",
                        StringComparison.Ordinal),
                    $"Unexpected transaction profile scenario '{scenario.Id}'.");
                Assert.True(scenario.RequiresDynamoDbStoredProcedures);
            });
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
