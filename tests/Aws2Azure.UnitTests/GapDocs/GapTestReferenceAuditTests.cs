using System;
using System.IO;
using Aws2Azure.GapDocs;

namespace Aws2Azure.UnitTests.GapDocs;

public sealed class GapTestReferenceAuditTests
{
    [Fact]
    public void FindMissingReferences_accepts_structured_conformance_tags()
    {
        var doc = Operation("s3", "GetObject");
        doc.BehaviorDifferences.Add(
            "HostId is omitted from the error envelope. [conformance:missing-field:HostId]");

        var findings = GapTestReferenceAudit.FindMissingReferences([doc]);

        Assert.Empty(findings);
    }

    [Fact]
    public void FindMissingReferences_accepts_fully_qualified_test_names_for_nonimplemented_subfeatures()
    {
        var doc = Operation("sns", "Subscribe");
        doc.SubFeatures.Add(new SubFeature
        {
            Name = "Endpoint validation",
            Status = "partial",
            Disposition = "feasible_backlog",
            TrackingIssue = "#899",
            Notes =
                "Verified by Aws2Azure.IntegrationTests.Sns.SnsRealAzureConformanceTests.Subscribe_endpoint_validation_matches_real_azure."
        });

        var findings = GapTestReferenceAudit.FindMissingReferences([doc]);

        Assert.Empty(findings);
    }

    [Fact]
    public void FindMissingReferences_flags_prose_only_divergences()
    {
        var doc = Operation("sqs", "ChangeMessageVisibility");
        doc.BehaviorDifferences.Add(
            "Service Bus renew-lock semantics ignore the requested VisibilityTimeout.");
        doc.SubFeatures.Add(new SubFeature
        {
            Name = "Arbitrary new visibility duration",
            Status = "unsupported",
            Disposition = "by_design",
            Notes = "Queue LockDuration always wins."
        });
        doc.SubFeatures.Add(new SubFeature
        {
            Name = "Happy path",
            Status = "implemented",
            Notes = "No audit needed for implemented sub-features."
        });

        var findings = GapTestReferenceAudit.FindMissingReferences([doc]);

        Assert.Collection(
            findings,
            finding =>
            {
                Assert.Equal("behavior_differences[0]", finding.EntryPath);
                Assert.Contains("renew-lock semantics", finding.Summary, StringComparison.Ordinal);
            },
            finding =>
            {
                Assert.Equal("sub_features[0] 'Arbitrary new visibility duration'", finding.EntryPath);
                Assert.Equal("unsupported: Arbitrary new visibility duration", finding.Summary);
            });
    }

    [Theory]
    [InlineData("[conformance:field-value:NextToken]", true)]
    [InlineData("See Aws2Azure.IntegrationTests.Sqs.SqsRealAzureConformanceTests.Queue_metadata_round_trip.", true)]
    [InlineData("Validated by scenario batch-visibility-timeout.", false)]
    [InlineData("", false)]
    public void HasDiscoverableTestReference_matches_current_heuristics(string text, bool expected)
    {
        Assert.Equal(expected, GapTestReferenceAudit.HasDiscoverableTestReference(text));
    }

    private static OperationDoc Operation(string service, string operation) => new()
    {
        Service = service,
        Operation = operation,
        AzureEquivalent = "Azure",
        Status = "implemented",
        SourceFile = Path.Combine("repo", "docs", "gaps", service, operation + ".yaml")
    };
}
