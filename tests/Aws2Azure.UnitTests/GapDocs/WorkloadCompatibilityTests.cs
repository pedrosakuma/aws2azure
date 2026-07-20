using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class WorkloadCompatibilityTests
{
    [Fact]
    public void ValidateDesign_rejects_unknown_workload_references()
    {
        var operation = Operation("s3", "PutObject", "implemented");
        var design = Design("s3", "Known gap");
        design.WorkloadPatterns.Add(new WorkloadPattern
        {
            Id = "invalid_references",
            Name = "Invalid",
            Compatibility = "conditional",
            Summary = "Invalid references.",
            Guidance = "Do not use.",
            Operations = ["MissingOperation"],
            DesignGaps = ["Missing gap"]
        });

        var errors = Validator.ValidateDesign([design], [operation]);

        Assert.Contains(errors, e => e.Contains("unknown operation 'MissingOperation'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown design gap 'Missing gap'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDesign_rejects_supported_pattern_with_partial_operation()
    {
        var operation = Operation("s3", "PutObject", "partial");
        var design = Design("s3", "Known gap");
        design.WorkloadPatterns.Add(new WorkloadPattern
        {
            Id = "object_writes",
            Name = "Object writes",
            Compatibility = "supported",
            Summary = "Writes objects.",
            Guidance = "Use PutObject.",
            Operations = ["PutObject"]
        });

        var errors = Validator.ValidateDesign([design], [operation]);

        Assert.Contains(errors, e => e.Contains("cannot be supported because operation 'PutObject' is 'partial'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDesign_rejects_duplicate_pattern_references()
    {
        var operation = Operation("s3", "PutObject", "implemented");
        var design = Design("s3", "Known gap");
        design.WorkloadPatterns.Add(new WorkloadPattern
        {
            Id = "duplicate_references",
            Name = "Duplicate references",
            Compatibility = "conditional",
            Summary = "Repeats references.",
            Guidance = "Fix the source YAML.",
            Operations = ["PutObject", "putobject"],
            DesignGaps = ["Known gap", "known gap"]
        });

        var errors = Validator.ValidateDesign([design], [operation]);

        Assert.Contains(errors, e => e.Contains("repeats operation 'putobject'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("repeats design gap 'known gap'", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDesign_rejects_invalid_and_globally_duplicate_pattern_ids()
    {
        var operation = Operation("s3", "PutObject", "implemented");
        var first = Design("s3", "Known gap");
        first.WorkloadPatterns.Add(new WorkloadPattern
        {
            Id = "Invalid-Id",
            Name = "First",
            Compatibility = "conditional",
            Summary = "First pattern.",
            Guidance = "Review it.",
            Operations = ["PutObject"]
        });
        var second = Design("s3", "Another gap");
        second.Service = "other";
        second.SourceFile = Path.Combine("repo", "docs", "gaps", "other", "_design.yaml");
        second.WorkloadPatterns.Add(new WorkloadPattern
        {
            Id = "Invalid-Id",
            Name = "Second",
            Compatibility = "conditional",
            Summary = "Second pattern.",
            Guidance = "Review it.",
            Operations = ["PutObject"]
        });

        var errors = Validator.ValidateDesign([first, second], [operation]);

        Assert.Contains(errors, error => error.Contains("must use lowercase letters", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duplicates workload pattern id 'Invalid-Id'", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_writes_generated_workload_assessment()
    {
        var operation = Operation("s3", "PutObject", "implemented");
        operation.VerifiedRealAzure = new RealAzureVerification
        {
            Date = "2026-07-15",
            Evidence = "https://github.com/pedrosakuma/aws2azure/issues/532"
        };
        var design = Design("s3", "Known gap");
        design.WorkloadPatterns.Add(new WorkloadPattern
        {
            Id = "basic_writes",
            Name = "Basic writes",
            Compatibility = "supported",
            Summary = "Writes an object.",
            Guidance = "Suitable for staging.",
            Operations = ["PutObject"]
        });
        design.WorkloadPatterns.Add(new WorkloadPattern
        {
            Id = "conditional_writes",
            Name = "Conditional writes",
            Compatibility = "conditional",
            Summary = "Writes with a caveat.",
            Guidance = "Review the design gap.",
            Operations = ["PutObject"],
            DesignGaps = ["Known gap"]
        });
        var output = Path.Combine(Path.GetTempPath(), $"aws2azure-gapdocs-{Guid.NewGuid():N}");

        try
        {
            MarkdownRenderer.Render([operation], [design], new RealAzureMigrationDoc(), output);

            var markdown = File.ReadAllText(Path.Combine(output, "workload-compatibility.md"));
            Assert.Contains("| Basic writes | ✅ supported | 1 implemented | 1/1 |", markdown, StringComparison.Ordinal);
            Assert.Contains("(design-gaps.md#s3-known-gap)", markdown, StringComparison.Ordinal);
            Assert.Contains("A module being available", markdown, StringComparison.Ordinal);
            Assert.Contains("Operation seals", markdown, StringComparison.Ordinal);
            Assert.Contains("do not certify every sub-feature", markdown, StringComparison.Ordinal);
            var designMarkdown = File.ReadAllText(Path.Combine(output, "design-gaps.md"));
            Assert.Contains("<a id=\"s3-known-gap\"></a>", designMarkdown, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static OperationDoc Operation(string service, string name, string status) => new()
    {
        Service = service,
        Operation = name,
        AzureEquivalent = "Azure",
        Status = status,
        SourceFile = Path.Combine("repo", "docs", "gaps", service, name + ".yaml")
    };

    private static ServiceDesignDoc Design(string service, string gapArea) => new()
    {
        Service = service,
        SourceFile = Path.Combine("repo", "docs", "gaps", service, "_design.yaml"),
        DesignGaps =
        [
            new DesignGap
            {
                Area = gapArea,
                Status = "by_design",
                Summary = "Known limitation."
            }
        ]
    };
}
