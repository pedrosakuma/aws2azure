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
    public void Render_writes_generated_workload_assessment()
    {
        var operation = Operation("s3", "PutObject", "implemented");
        operation.VerifiedRealAzure = "2026-07-15";
        var design = Design("s3", "Known gap");
        design.WorkloadPatterns.Add(new WorkloadPattern
        {
            Name = "Basic writes",
            Compatibility = "supported",
            Summary = "Writes an object.",
            Guidance = "Suitable for staging.",
            Operations = ["PutObject"]
        });
        design.WorkloadPatterns.Add(new WorkloadPattern
        {
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
            MarkdownRenderer.Render([operation], [design], output);

            var markdown = File.ReadAllText(Path.Combine(output, "workload-compatibility.md"));
            Assert.Contains("| Basic writes | ✅ supported | 1 implemented | 1/1 |", markdown, StringComparison.Ordinal);
            Assert.Contains("(design-gaps.md#s3-known-gap)", markdown, StringComparison.Ordinal);
            Assert.Contains("A module being available", markdown, StringComparison.Ordinal);
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
