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
                    ReadinessChecklistQuestion = "Does your workload depend on bucket lifecycle or notification APIs?",
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
    public void Validate_rejects_colliding_operation_and_sub_feature_documentation_identities()
    {
        var first = Operation("s3", "Get Object", "partial");
        first.Disposition = "by_design";
        first.SubFeatures =
        [
            new SubFeature { Name = "metadata/value", Status = "partial", Disposition = "by_design" },
            new SubFeature { Name = "metadata value", Status = "partial", Disposition = "by_design" }
        ];
        var second = Operation("s3", "Get/Object", "partial");
        second.Disposition = "by_design";

        var errors = Validator.Validate([first, second], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));

        Assert.Contains(errors, error => error.Contains("operation documentation path 'operations/s3/get-object.md' collides", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duplicate documentation anchor 'sub-feature-metadata-value'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_colliding_service_documentation_identities()
    {
        var first = Operation("foo.bar", "GetObject", "partial");
        first.Disposition = "by_design";
        var second = Operation("foobar", "PutObject", "partial");
        second.Disposition = "by_design";

        var errors = Validator.Validate([first, second], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));

        Assert.Contains(errors, error => error.Contains(
            "service 'foobar' documentation identity collides with service 'foo.bar' as 'foobar'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_rejects_service_page_reserved_for_aggregate_output()
    {
        var operation = Operation("coverage", "GetObject", "partial");
        operation.Disposition = "by_design";

        var errors = Validator.Validate([operation], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));

        Assert.Contains(errors, error => error.Contains(
            "service 'coverage' documentation page 'coverage.md' is reserved",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("foo", "service-foo")]
    public void Validate_rejects_operation_anchor_collisions_on_service_page(string service, string operationName)
    {
        var operation = Operation(service, operationName, "partial");
        operation.Disposition = "by_design";

        var errors = Validator.Validate([operation], new RealAzureMigrationDoc(), new DateOnly(2026, 7, 28));

        Assert.Contains(errors, error => error.Contains(
            $"operation '{operationName}' documentation anchor '{DocumentationLinks.Anchor(operationName)}' collides",
            StringComparison.Ordinal));
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

    [Fact]
    public void ValidateDesign_rejects_colliding_design_gap_documentation_paths()
    {
        var operation = Operation("s3", "GetBucketAcl", "implemented");
        var design = new ServiceDesignDoc
        {
            Service = "s3",
            SourceFile = Path.Combine("repo", "docs", "gaps", "s3", "_design.yaml"),
            DesignGaps =
            [
                new DesignGap { Area = "IAM/ACL", Status = "by_design", Summary = "First." },
                new DesignGap { Area = "IAM ACL", Status = "by_design", Summary = "Second." }
            ]
        };

        var errors = Validator.ValidateDesign([design], [operation]);

        Assert.Contains(errors, error => error.Contains(
            "design gap 'IAM ACL' produces duplicate documentation path 'design-gaps/s3/iam-acl.md'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDesign_rejects_aggregate_anchor_collisions_across_services()
    {
        var operations = new[]
        {
            Operation("foo", "GetObject", "implemented"),
            Operation("foo-bar", "PutObject", "implemented")
        };
        var designs = new[]
        {
            Design("foo", new DesignGap { Area = "bar-baz", Status = "by_design", Summary = "First." }),
            Design("foo-bar", new DesignGap { Area = "baz", Status = "by_design", Summary = "Second." })
        };

        var errors = Validator.ValidateDesign(designs, operations);

        Assert.Contains(errors, error => error.Contains(
            "design gap 'baz' documentation anchor 'foo-bar-baz' collides on aggregate page 'design-gaps.md'",
            StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDesign_rejects_workload_service_anchor_reserved_by_static_section()
    {
        var operation = Operation("adoption decision", "GetObject", "implemented");
        var design = Design(
            "adoption decision",
            new DesignGap { Area = "Known gap", Status = "by_design", Summary = "Known." });
        design.WorkloadPatterns =
        [
            new WorkloadPattern
            {
                Id = "basic",
                Name = "Basic",
                Compatibility = "compatible",
                Summary = "Supported.",
                Guidance = "Proceed.",
                Operations = ["GetObject"]
            }
        ];

        var errors = Validator.ValidateDesign([design], [operation]);

        Assert.Contains(errors, error => error.Contains(
            "service 'adoption decision' documentation anchor 'adoption-decision' is reserved",
            StringComparison.Ordinal));
    }

    private static OperationDoc Operation(string service, string name, string status) => new()
    {
        Service = service,
        Operation = name,
        AzureEquivalent = "Azure",
        Status = status,
        SourceFile = Path.Combine("repo", "docs", "gaps", service, name + ".yaml")
    };

    private static ServiceDesignDoc Design(string service, DesignGap gap) => new()
    {
        Service = service,
        SourceFile = Path.Combine("repo", "docs", "gaps", service, "_design.yaml"),
        DesignGaps = [gap]
    };
}
