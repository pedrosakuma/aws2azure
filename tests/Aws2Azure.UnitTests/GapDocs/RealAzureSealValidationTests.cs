using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RealAzureSealValidationTests
{
    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public void Validate_accepts_implemented_operation_with_valid_seal()
    {
        var operation = Operation("PutObject", "implemented");
        operation.VerifiedRealAzure = ValidSeal();

        var errors = Validator.Validate([operation], Migration(), Today);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_accepts_valid_seal_without_workflow_run()
    {
        var operation = Operation("PutObject", "partial");
        operation.VerifiedRealAzure = ValidSeal();
        operation.VerifiedRealAzure.WorkflowRun = string.Empty;

        var errors = Validator.Validate([operation], Migration(), Today);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_rejects_malformed_seal_fields()
    {
        var operation = Operation("PutObject", "implemented");
        operation.VerifiedRealAzure = new RealAzureVerification
        {
            Date = "15/07/2026",
            Evidence = "http://example.test/evidence",
            WorkflowRun = "https://github.com/pedrosakuma/aws2azure/actions/runs/not-a-number"
        };

        var errors = Validator.Validate([operation], Migration(), Today);

        Assert.Contains(errors, e => e.Contains("date must use YYYY-MM-DD", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("evidence must be an absolute HTTPS URL", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("workflow_run must be a GitHub Actions URL", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_new_implemented_operation_without_seal()
    {
        var errors = Validator.Validate([Operation("PutObject", "implemented")], Migration(), Today);

        Assert.Contains(errors, e => e.Contains(
            "status 'implemented' requires a valid 'verified_real_azure' seal",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_allows_active_migration_entry()
    {
        var operation = Operation("PutObject", "implemented");

        var errors = Validator.Validate(
            [operation],
            Migration(("s3", "2026-10-31", ["PutObject"])),
            Today);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_rejects_expired_migration_entry()
    {
        var operation = Operation("PutObject", "implemented");

        var errors = Validator.Validate(
            [operation],
            Migration(("s3", "2026-07-14", ["PutObject"])),
            Today);

        Assert.Contains(errors, e => e.Contains("expired on 2026-07-14", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_extending_migration_beyond_fixed_deadline()
    {
        var operation = Operation("PutObject", "implemented");

        var errors = Validator.Validate(
            [operation],
            Migration(("s3", "2026-11-01", ["PutObject"])),
            Today);

        Assert.Contains(errors, e => e.Contains(
            "cannot extend the migration beyond 2026-10-31",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_expanding_migration_with_new_operation()
    {
        var operation = Operation("NewOperation", "implemented");

        var errors = Validator.Validate(
            [operation],
            Migration(("s3", "2026-10-31", ["NewOperation"])),
            Today);

        Assert.Contains(errors, e => e.Contains(
            "migration may only shrink the fixed legacy real-Azure debt baseline",
            StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(
            "status 'implemented' requires a valid 'verified_real_azure' seal",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_duplicate_and_stale_migration_entries()
    {
        var sealedOperation = Operation("PutObject", "implemented");
        sealedOperation.VerifiedRealAzure = ValidSeal();
        var partialOperation = Operation("GetObject", "partial");
        var migration = Migration(
            ("s3", "2026-10-31", ["PutObject", "GetObject"]),
            ("S3", "2026-10-31", ["putobject"]));

        var errors = Validator.Validate([sealedOperation, partialOperation], migration, Today);

        Assert.Contains(errors, e => e.Contains("duplicates service 'S3'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("already has a real-Azure seal", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("status 'partial'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("duplicates migration operation 's3/putobject'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_invalid_migration_metadata_and_unknown_operation()
    {
        var migration = Migration(("s3", "31/10/2026", ["MissingOperation"]));
        migration.Services[0].TrackingIssue = "https://example.test/not-an-issue";

        var errors = Validator.Validate([Operation("PutObject", "partial")], migration, Today);

        Assert.Contains(errors, e => e.Contains("tracking_issue must be a GitHub issue URL", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("expires_on must use YYYY-MM-DD", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("references unknown operation 's3/MissingOperation'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_malformed_sub_feature_seal()
    {
        var operation = Operation("PutObject", "partial");
        operation.SubFeatures.Add(new SubFeature
        {
            Name = "Metadata",
            Status = "implemented",
            VerifiedRealAzure = new RealAzureVerification
            {
                Date = "2026-07-15",
                Evidence = "not-a-url"
            }
        });

        var errors = Validator.Validate([operation], Migration(), Today);

        Assert.Contains(errors, e => e.Contains(
            "sub_features[0].verified_real_azure.evidence",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Render_keeps_migration_debt_visible()
    {
        var operation = Operation("PutObject", "implemented");
        var migration = Migration(("s3", "2026-10-31", ["PutObject"]));
        var output = Path.Combine(Path.GetTempPath(), $"aws2azure-gapdocs-{Guid.NewGuid():N}");

        try
        {
            MarkdownRenderer.Render([operation], [], migration, output);

            var markdown = File.ReadAllText(Path.Combine(output, "divergences.md"));
            Assert.Contains("| s3 | PutObject | [issue](https://github.com/pedrosakuma/aws2azure/issues/532) | 2026-10-31 |", markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static OperationDoc Operation(string name, string status) => new()
    {
        Service = "s3",
        Operation = name,
        AzureEquivalent = "Azure Blob Storage",
        Status = status,
        SourceFile = Path.Combine("repo", "docs", "gaps", "s3", name + ".yaml")
    };

    private static RealAzureVerification ValidSeal() => new()
    {
        Date = "2026-07-15",
        Evidence = "https://github.com/pedrosakuma/aws2azure/issues/532",
        WorkflowRun = "https://github.com/pedrosakuma/aws2azure/actions/runs/123456"
    };

    private static RealAzureMigrationDoc Migration(
        params (string Service, string ExpiresOn, string[] Operations)[] entries) => new()
    {
        SourceFile = Path.Combine("repo", "docs", "gaps", "_real_azure_migration.yaml"),
        Services = entries.Select(entry => new RealAzureMigrationService
        {
            Service = entry.Service,
            TrackingIssue = "https://github.com/pedrosakuma/aws2azure/issues/532",
            ExpiresOn = entry.ExpiresOn,
            Operations = [.. entry.Operations]
        }).ToList()
    };
}
