namespace Aws2Azure.UnitTests.GapDocs;

public sealed class RealAzureTransactionWorkflowTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string Workflow = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        ".github",
        "workflows",
        "integration-real-azure.yml"));
    private static readonly string Matrix = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "testing",
        "real-azure-conformance.yaml"));
    private static readonly string Baseline = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "workloads",
        "approved-runtimes",
        "dynamodb-single-partition-transactions.yaml"));

    [Fact]
    public void Transaction_profile_is_discoverable_and_requires_rollback_mode()
    {
        Assert.Contains(
            "- dynamodb-single-partition-transactions",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ \"$profile\" = dynamodb-single-partition-transactions ]",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "runtime_mode=rollback",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "export-approved-runtime \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--role prior \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--identity-output \"$identity_dir/prior-runtime.json\"",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Transaction_rollback_test_and_bootstrap_baseline_are_registered()
    {
        Assert.Contains(
            "transaction-adjacent-runtime-rollback",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "DynamoDbRealAzureTransactionQualificationTests.Adjacent_runtime_transaction_rollback_is_atomic_and_compatible",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "id: dynamodb-single-partition-transactions",
            Baseline,
            StringComparison.Ordinal);
        Assert.Contains("status: bootstrap", Baseline, StringComparison.Ordinal);
        Assert.Contains(
            "rollback_baseline_eligible: true",
            Baseline,
            StringComparison.Ordinal);
        Assert.Contains(
            "promotion_eligible: false",
            Baseline,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
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

        throw new DirectoryNotFoundException(
            "Could not find the repository root.");
    }
}
