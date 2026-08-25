using Aws2Azure.Conformance.DynamoDb;
using Aws2Azure.Conformance.Evidence;

namespace Aws2Azure.UnitTests.DynamoDb;

public sealed class DynamoDbRealAzureEvidenceSelectionTests
{
    [Fact]
    public void Disabled_mode_marks_transact_case_skipped_without_omitting_it()
    {
        var cases = DynamoDbRealAzureEvidenceCaseSelector.SelectCases("Disabled");

        var transactCase = Assert.Single(cases, c => c.Case.Name == "transact-get-write-items-roundtrip");

        Assert.True(transactCase.ShouldSkip);
        Assert.Contains("requires stored procedures", transactCase.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AWS2AZURE_DDB_STORED_PROCEDURE_MODE=Disabled", transactCase.SkipReason, StringComparison.Ordinal);
        Assert.Equal(
            DynamoDbErrorMatrix.Cases.Count + DynamoDbHappyPathMatrix.Cases.Count,
            cases.Count);
    }

    [Fact]
    public void Preferred_mode_keeps_transact_case_runnable()
    {
        var cases = DynamoDbRealAzureEvidenceCaseSelector.SelectCases("Preferred");

        var transactCase = Assert.Single(cases, c => c.Case.Name == "transact-get-write-items-roundtrip");

        Assert.False(transactCase.ShouldSkip);
        Assert.Null(transactCase.SkipReason);
    }

    [Fact]
    public void Invalid_mode_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DynamoDbRealAzureEvidenceCaseSelector.SelectCases("Required"));

        Assert.Contains("Disabled or Preferred", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveSkipped_writes_explicit_skip_marker_for_dynamodb_case()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            nameof(DynamoDbRealAzureEvidenceSelectionTests),
            Guid.NewGuid().ToString("N"));

        try
        {
            var store = new ConformanceEvidenceStore(root);
            var metadata = new ConformanceEvidenceMetadata(
                ConformanceEvidenceMetadata.SourceRealAzureProxy,
                "dynamodb",
                "transact-get-write-items-roundtrip",
                "dynamodb:CreateTable/TransactWriteItems/TransactGetItems/DeleteTable",
                ConformanceEvidenceStore.SkippedStepName,
                DateTimeOffset.UtcNow,
                SkippedReason: "requires stored procedures, disabled for this profile");

            store.SaveSkipped(metadata);

            var path = store.PathFor(
                "dynamodb",
                "transact-get-write-items-roundtrip",
                ConformanceEvidenceStore.SkippedStepName);
            var text = File.ReadAllText(path);

            Assert.Contains("# service: dynamodb", text, StringComparison.Ordinal);
            Assert.Contains("# case: transact-get-write-items-roundtrip", text, StringComparison.Ordinal);
            Assert.Contains("# skipped: requires stored procedures, disabled for this profile", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
