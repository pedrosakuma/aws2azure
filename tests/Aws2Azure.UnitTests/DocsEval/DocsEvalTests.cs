using System.Text.Json;
using System.Text.Json.Nodes;
using Aws2Azure.DocsEval;

namespace Aws2Azure.UnitTests.DocsEval;

public sealed class DocsEvalTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string DatasetPath = Path.Combine(
        RepoRoot, "tools", "Aws2Azure.DocsEval", "Dataset", "retrieval-eval-dataset.json");

    [Fact]
    public void Live_dataset_evaluates_clean_against_the_current_repository()
    {
        var dataset = Evaluator.LoadDataset(DatasetPath);
        var result = Evaluator.Run(RepoRoot, dataset);

        Assert.True(
            result.IsClean,
            "Violations: " + string.Join(
                "; ",
                result.Violations.Select(v => $"[{v.CaseId}] {v.Message}")
                    .Concat(result.MaturityClaimViolations)));
        Assert.Equal(result.TotalCases, result.PassedCases);
    }

    [Fact]
    public void Live_dataset_covers_all_six_services_and_every_required_category()
    {
        var dataset = Evaluator.LoadDataset(DatasetPath);

        var services = new[] { "s3", "sqs", "sns", "dynamodb", "kinesis", "secretsmanager" };
        foreach (var service in services)
        {
            Assert.Contains(dataset.Cases, c => c.Service == service);
        }

        var categories = new[]
        {
            "adoption_status", "configuration", "operation_gaps", "authentication", "deployment", "rollback",
        };
        foreach (var category in categories)
        {
            Assert.Contains(dataset.Cases, c => c.Category == category);
        }

        Assert.Contains(dataset.Cases, c => c.Adversarial);
    }

    [Fact]
    public void Every_case_cites_a_canonical_source_and_states_precedence()
    {
        var dataset = Evaluator.LoadDataset(DatasetPath);

        Assert.All(dataset.Cases, c =>
        {
            Assert.NotEmpty(c.ExpectedAnswer.CanonicalSources);
            Assert.False(string.IsNullOrWhiteSpace(c.ExpectedAnswer.Precedence));
            Assert.False(string.IsNullOrWhiteSpace(c.Question));
            Assert.NotEmpty(c.Checks);
        });
    }

    [Fact]
    public void Case_ids_are_unique()
    {
        var dataset = Evaluator.LoadDataset(DatasetPath);

        var duplicates = dataset.Cases
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Detects_a_false_ga_claim_against_a_current_candidate_verdict()
    {
        var dataset = new EvalDataset
        {
            SchemaVersion = 1,
            Cases =
            [
                new EvalCase
                {
                    Id = "synthetic-false-ga",
                    Category = "adoption_status",
                    Service = "s3",
                    Question = "Is s3-basic-object-crud GA?",
                    ExpectedAnswer = new ExpectedAnswer
                    {
                        Summary = "Synthetic case asserting an incorrect GA verdict.",
                        CanonicalSources = ["docs/site/workload-ga.json"],
                        Precedence = "docs/site/workload-ga.json is authoritative.",
                    },
                    Checks =
                    [
                        new EvalCheck
                        {
                            Type = "profile_verdict",
                            ProfileId = "s3-basic-object-crud",
                            ExpectedVerdict = "ga",
                        },
                    ],
                },
            ],
        };

        var result = Evaluator.Run(RepoRoot, dataset);

        Assert.False(result.IsClean);
        Assert.Contains(
            result.Violations,
            v => v.CaseId == "synthetic-false-ga" && v.Message.Contains("verdict is 'candidate'", StringComparison.Ordinal));
    }

    [Fact]
    public void Detects_a_fabricated_configuration_field()
    {
        var dataset = new EvalDataset
        {
            SchemaVersion = 1,
            Cases =
            [
                new EvalCase
                {
                    Id = "synthetic-fabricated-field",
                    Category = "configuration",
                    Service = "s3",
                    Question = "Does services.s3.enabled exist?",
                    ExpectedAnswer = new ExpectedAnswer
                    {
                        Summary = "Synthetic case wrongly asserting a fabricated field is absent from a field that is actually present.",
                        CanonicalSources = ["config.schema.json"],
                        Precedence = "config.schema.json is authoritative.",
                    },
                    Checks =
                    [
                        new EvalCheck
                        {
                            Type = "schema_path_exists",
                            SchemaPath = "services.s3.thisFieldDoesNotExist",
                            ExpectedExists = true,
                        },
                    ],
                },
            ],
        };

        var result = Evaluator.Run(RepoRoot, dataset);

        Assert.False(result.IsClean);
        Assert.Contains(
            result.Violations,
            v => v.CaseId == "synthetic-fabricated-field" && v.Message.Contains("missing from config.schema.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_path_resolver_finds_real_nested_fields_and_rejects_fabricated_ones()
    {
        var schema = JsonNode.Parse(
            File.ReadAllText(Path.Combine(RepoRoot, "config.schema.json")))!.AsObject();

        Assert.True(SchemaPathResolver.PathExists(schema, "services.s3.enabled"));
        Assert.True(SchemaPathResolver.PathExists(schema, "bindings.aws.accessKeyId"));
        Assert.True(SchemaPathResolver.PathExists(schema, "bindings.azure.sqs.target.namespace"));
        Assert.True(SchemaPathResolver.PathExists(schema, "bindings.azure.dynamodb.auth"));

        Assert.False(SchemaPathResolver.PathExists(schema, "services.s3.retryPolicy"));
        Assert.False(SchemaPathResolver.PathExists(schema, "bindings.azure.sqs.target.connectionString"));
        Assert.False(SchemaPathResolver.PathExists(schema, "bindings.azure.kinesis.rateLimitPerSecond"));

        Assert.True(SchemaPathResolver.CanonicalValueExists(schema, "sharedKey"));
        Assert.True(SchemaPathResolver.CanonicalValueExists(schema, "sas"));
        Assert.True(SchemaPathResolver.CanonicalValueExists(schema, "managedIdentity"));
        Assert.False(SchemaPathResolver.CanonicalValueExists(schema, "apiKey"));
        Assert.False(SchemaPathResolver.CanonicalValueExists(schema, "oauth2"));
    }

    [Fact]
    public void Maturity_claim_scan_flags_unhedged_uncited_ga_claim_but_not_hedged_or_cited_text()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "claim-without-hedge.md"),
                "The widget-example profile is the widget-example GA profile with no caveats.");
            File.WriteAllText(
                Path.Combine(directory, "claim-with-hedge.md"),
                "The profile is `candidate`. GA still requires a production-shaped SLO campaign.");
            File.WriteAllText(
                Path.Combine(directory, "claim-with-citation.md"),
                "This profile is GA as promoted historically. " +
                "See docs/site/workload-ga.json for the current live verdict, which may differ.");

            var unhedgedViolations = InvokeScan(directory);

            Assert.Contains(unhedgedViolations, v => v.Contains("claim-without-hedge.md", StringComparison.Ordinal));
            Assert.DoesNotContain(unhedgedViolations, v => v.Contains("claim-with-hedge.md", StringComparison.Ordinal));
            Assert.DoesNotContain(unhedgedViolations, v => v.Contains("claim-with-citation.md", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<string> InvokeScan(string directory)
    {
        // Exercise the scan logic directly against a synthetic doc tree by
        // reusing the same file-scan code path via a minimal repo shape: a
        // single directory under one of the scanned roots.
        var repoRoot = Path.Combine(directory, "repo");
        var scannedRoot = Path.Combine(repoRoot, "docs", "workloads");
        Directory.CreateDirectory(scannedRoot);
        foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
        {
            File.Copy(file, Path.Combine(scannedRoot, Path.GetFileName(file)));
        }

        return Aws2Azure.DocsEval.Evaluator.ScanForUncitedMaturityClaims(repoRoot);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aws2azure-docseval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepoRoot()
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
        throw new InvalidOperationException("Could not find repository root.");
    }
}
