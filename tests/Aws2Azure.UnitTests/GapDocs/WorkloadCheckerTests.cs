using System.Text.Json;
using Aws2Azure.GapDocs;
using YamlDotNet.Core;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class WorkloadCheckerTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly IReadOnlyList<OperationDoc> Operations =
        Loader.LoadAll(Path.Combine(RepoRoot, "docs", "gaps"));
    private static readonly IReadOnlyList<ServiceDesignDoc> Designs =
        Loader.LoadDesignDocs(Path.Combine(RepoRoot, "docs", "gaps"));

    [Fact]
    public void Evaluate_marks_cross_partition_transactions_blocked_with_gap_guidance()
    {
        var report = EvaluateFixture("dynamodb-cross-partition.yaml");

        Assert.Equal("blocked", report.Compatibility);
        var finding = Assert.Single(
            report.Findings,
            finding => finding.Id == "cross_partition_transactions");
        Assert.Equal("blocked", finding.Compatibility);
        var gap = Assert.Single(finding.DesignGaps);
        Assert.Equal("Transaction scope is single-partition, single-table", gap.Area);
        Assert.Contains("idempotent application-level compensation", gap.Workaround, StringComparison.Ordinal);
        Assert.EndsWith(
            "design-gaps.md#dynamodb-transaction-scope-is-single-partition-single-table",
            gap.Documentation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_marks_sqs_fifo_conditional_and_requires_amqp()
    {
        var report = EvaluateFixture("sqs-fifo.yaml");

        Assert.Equal("conditional", report.Compatibility);
        var finding = Assert.Single(report.Findings, finding => finding.Id == "sqs_fifo");
        Assert.Equal("conditional", finding.Compatibility);
        Assert.Contains("transport: Amqp", finding.Guidance, StringComparison.Ordinal);
        Assert.Contains(
            finding.DesignGaps,
            gap => gap.Workaround.Contains("Set transport: Amqp", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_marks_s3_policy_and_lifecycle_automation_blocked()
    {
        var report = EvaluateFixture("s3-policy-lifecycle.yaml");

        Assert.Equal("blocked", report.Compatibility);
        Assert.All(report.Findings, finding => Assert.Equal("blocked", finding.Compatibility));
        var requirement = Assert.Single(
            report.Findings,
            finding => finding.Id == "s3_policy_lifecycle_automation");
        Assert.Contains(
            requirement.DesignGaps,
            gap => gap.Area == "Bucket sub-resource configs are not translated"
                   && gap.Workaround.Contains("Azure Blob lifecycle-management", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_marks_implemented_operations_and_supported_requirement_compatible()
    {
        var report = EvaluateFixture("compatible.yaml");

        Assert.Equal("compatible", report.Compatibility);
        Assert.All(report.Findings, finding => Assert.Equal("compatible", finding.Compatibility));
    }

    [Fact]
    public void Renderers_are_deterministic_and_json_is_source_generated_shape()
    {
        var first = EvaluateFixture("dynamodb-cross-partition.yaml");
        var second = EvaluateFixture("dynamodb-cross-partition.yaml");

        var firstJson = WorkloadReportRenderer.RenderJson(first);
        var secondJson = WorkloadReportRenderer.RenderJson(second);
        Assert.Equal(firstJson, secondJson);
        Assert.Equal(
            ["dynamodb:TransactWriteItems", "s3:PutObject", "sqs:SendMessage", "cross_partition_transactions"],
            first.Findings.Select(finding => finding.Id));

        using var json = JsonDocument.Parse(firstJson);
        Assert.Equal(1, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("blocked", json.RootElement.GetProperty("compatibility").GetString());
        Assert.True(json.RootElement.TryGetProperty("findings", out _));

        var markdown = WorkloadReportRenderer.RenderMarkdown(first);
        Assert.Contains("**Overall:** ⛔ blocked", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "docs/site/design-gaps.md#dynamodb-transaction-scope-is-single-partition-single-table",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_unknown_schema_operations_requirements_and_empty_workload()
    {
        var manifest = new WorkloadManifest
        {
            SchemaVersion = 2,
            Operations = ["invalid", "s3:MissingOperation", "S3:MissingOperation"],
            Requirements = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["unknown_requirement"] = false,
                ["sqs_fifo"] = true,
                ["SQS_FIFO"] = true,
            },
        };

        var errors = WorkloadManifestValidator.Validate(manifest, Operations, Designs);

        Assert.Contains(errors, error => error.Contains("unsupported schema_version", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("missing required field 'workload'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("invalid operation 'invalid'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown operation 's3:MissingOperation'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duplicate operation 'S3:MissingOperation'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown requirement 'unknown_requirement'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duplicate requirement 'sqs_fifo'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_rejects_explicitly_null_collections()
    {
        var manifest = new WorkloadManifest
        {
            SchemaVersion = 1,
            Workload = "invalid-null-collections",
            Operations = null!,
            Requirements = null!,
        };

        var errors = WorkloadManifestValidator.Validate(manifest, Operations, Designs);

        Assert.Contains("'operations' must be a list when specified", errors);
        Assert.Contains("'requirements' must be a mapping when specified", errors);
        Assert.Contains("manifest must declare at least one operation or enabled requirement", errors);
    }

    [Fact]
    public void Load_rejects_unknown_manifest_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aws2azure-workload-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(
            path,
            """
            schema_version: 1
            workload: invalid
            operations:
              - s3:PutObject
            typo: true
            """);

        try
        {
            Assert.Throws<YamlException>(() => WorkloadManifestLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_empty_manifest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aws2azure-workload-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, string.Empty);

        try
        {
            var exception = Assert.Throws<InvalidDataException>(
                () => WorkloadManifestLoader.Load(path));
            Assert.Contains("empty document", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_duplicate_requirement_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aws2azure-workload-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(
            path,
            """
            schema_version: 1
            workload: duplicate
            requirements:
              sqs_fifo: true
              sqs_fifo: false
            """);

        try
        {
            Assert.Throws<YamlException>(() => WorkloadManifestLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static WorkloadCompatibilityReport EvaluateFixture(string name)
    {
        var manifest = WorkloadManifestLoader.Load(
            Path.Combine(RepoRoot, "tests", "Aws2Azure.UnitTests", "GapDocs", "Fixtures", name));
        Assert.Empty(WorkloadManifestValidator.Validate(manifest, Operations, Designs));
        return WorkloadCompatibilityEvaluator.Evaluate(manifest, Operations, Designs);
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

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
