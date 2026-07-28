using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class GapDocsValidationTests
{
    [Fact]
    public void Validate_rejects_missing_disposition_on_partial_operation()
    {
        var doc = Operation("s3", "GetBucketAcl", "partial");

        var errors = Validator.Validate([doc], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));

        Assert.Contains(errors, error => error.Contains("operation 'GetBucketAcl' with status 'partial' must declare disposition", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_feasible_backlog_without_tracking_issue()
    {
        var doc = Operation("s3", "GetBucketAcl", "partial");
        doc.Disposition = "feasible_backlog";
        doc.SubFeatures.Add(new SubFeature
        {
            Name = "grants",
            Status = "unsupported",
            Disposition = "feasible_backlog"
        });

        var errors = Validator.Validate([doc], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));

        Assert.Contains(errors, error => error.Contains("operation 'GetBucketAcl' with disposition 'feasible_backlog' must declare tracking_issue as '#<number>'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("sub_features[0] 'grants' with disposition 'feasible_backlog' must declare tracking_issue as '#<number>'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_accepts_valid_disposition_combinations()
    {
        var doc = Operation("s3", "GetBucketAcl", "partial");
        doc.Disposition = "by_design";
        doc.SubFeatures.Add(new SubFeature
        {
            Name = "pagination",
            Status = "partial",
            Disposition = "feasible_backlog",
            TrackingIssue = "#690"
        });
        var design = new ServiceDesignDoc
        {
            Service = "s3",
            SourceFile = Path.Combine("repo", "docs", "gaps", "s3", "_design.yaml"),
            DesignGaps =
            [
                new DesignGap
                {
                    Area = "Bucket sub-resource configs are not translated",
                    Status = "unsupported",
                    Disposition = "by_design",
                    Summary = "Account-scoped or management-plane configuration remains outside the data path."
                }
            ]
        };

        var operationErrors = Validator.Validate([doc], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));
        var designErrors = Validator.ValidateDesign([design], [doc]);

        Assert.Empty(operationErrors);
        Assert.Empty(designErrors);
    }

    [Fact]
    public void Validate_rejects_disposition_on_implemented_operation()
    {
        var doc = Operation("s3", "GetBucketAcl", "implemented");
        doc.Disposition = "feasible_backlog";
        doc.TrackingIssue = "#690";

        var errors = Validator.Validate([doc], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));

        Assert.Contains(errors, error => error.Contains("operation 'GetBucketAcl' with status 'implemented' must not declare disposition", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("operation 'GetBucketAcl' with status 'implemented' must not declare tracking_issue", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDesign_rejects_non_by_design_disposition_on_by_design_gap()
    {
        var operation = Operation("s3", "GetBucketAcl", "implemented");
        var design = new ServiceDesignDoc
        {
            Service = "s3",
            SourceFile = Path.Combine("repo", "docs", "gaps", "s3", "_design.yaml"),
            DesignGaps =
            [
                new DesignGap
                {
                    Area = "Known gap",
                    Status = "by_design",
                    Disposition = "feasible_backlog",
                    TrackingIssue = "#690",
                    Summary = "Known limitation."
                }
            ]
        };

        var errors = Validator.ValidateDesign([design], [operation]);

        Assert.Contains(errors, error => error.Contains("may only declare disposition 'by_design'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("must not declare tracking_issue", StringComparison.Ordinal));
    }

    private static OperationDoc Operation(string service, string name, string status) => new()
    {
        Service = service,
        Operation = name,
        AzureEquivalent = "Azure",
        Status = status,
        SourceFile = Path.Combine("repo", "docs", "gaps", service, name + ".yaml")
    };
}
