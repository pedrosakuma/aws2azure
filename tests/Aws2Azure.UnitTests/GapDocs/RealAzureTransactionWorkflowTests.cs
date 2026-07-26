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
    private static readonly string Manifest = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "docs",
        "workloads",
        "dynamodb-single-partition-transactions.yaml"));
    private static readonly string BaselinePath = Path.Combine(
        RepositoryRoot,
        "docs",
        "workloads",
        "approved-runtimes",
        "dynamodb-single-partition-transactions.yaml");
    private static readonly string Baseline = File.ReadAllText(BaselinePath);
    private static readonly string MigrationTestSource = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "tests",
        "Aws2Azure.IntegrationTests",
        "DynamoDb",
        "DynamoDbPersistedFormatMigrationTests.cs"));
    private static readonly string RollbackTestSource = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "tests",
        "Aws2Azure.IntegrationTests",
        "DynamoDb",
        "DynamoDbRealAzureTransactionQualificationTests.cs"));
    private static readonly string RealAzureFixtureSource = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "tests",
        "Aws2Azure.IntegrationTests",
        "Fixtures",
        "RealAzureProxyFixture.cs"));
    private static readonly string DynamoDbFixtureSource = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        "tests",
        "Aws2Azure.IntegrationTests",
        "Fixtures",
        "DynamoDbRealAzureProxyFixture.cs"));
    private static readonly string LoadWorkflow = File.ReadAllText(Path.Combine(
        RepositoryRoot,
        ".github",
        "workflows",
        "workload-load-real-azure.yml"));

    [Fact]
    public void Transaction_profile_resolves_exact_candidate_and_bootstrap_prior()
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
            "persisted_format_qualification=1",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "export-approved-runtime \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--profile \"${{ steps.mode.outputs.profile }}\" \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--role candidate",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--role prior \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "--ledger-json \"$prior_ledger\" \\",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Transaction candidate and bootstrap prior must be distinct at $field.",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "correctness evidence does not make the profile candidate, approved, or GA",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AWS2AZURE_DDB_TRANSACTION_ROLLBACK_BLOCKER",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rollout-only",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "inconclusive",
            Workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Transaction_rollback_correctness_and_exact_body_probe_are_registered()
    {
        Assert.Contains(
            "- id: rollback",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "    - rollback",
            Manifest,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "transaction-adjacent-runtime-rollback",
            Manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "DynamoDbRealAzureTransactionQualificationTests.Adjacent_runtime_transaction_rollback_is_atomic_and_compatible",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "DynamoDbPersistedFormatMigrationTests.Adjacent_runtime_reads_rewrites_and_continuations_are_bidirectional",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "DynamoDbRealAzureTransactionTests.Supported_condition_subset_and_write_kinds_commit_expected_state",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "transaction-sproc-body-verification",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "transaction-region-pinning",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "transaction-preflight-contracts",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "DynamoDbRealAzureTransactionTests.Conflicting_v5_sproc_body_fails_closed_and_is_restored_in_isolated_table",
            Matrix,
            StringComparison.Ordinal);
        Assert.Contains(
            "validate-conformance-discovery",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Collection(DynamoDbRealAzureLoadCollection.Name)]",
            MigrationTestSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Collection(DynamoDbRealAzureLoadCollection.Name)]",
            RollbackTestSource,
            StringComparison.Ordinal);
        Assert.True(
            File.Exists(BaselinePath),
            "The compatible transaction bootstrap must be committed.");
        Assert.Contains("status: bootstrap", Baseline, StringComparison.Ordinal);
        Assert.Contains(
            "rollback_baseline_eligible: true",
            Baseline,
            StringComparison.Ordinal);
        Assert.Contains(
            "promotion_eligible: false",
            Baseline,
            StringComparison.Ordinal);
        Assert.DoesNotContain("qualification:", Baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("revocation:", Baseline, StringComparison.Ordinal);
        Assert.Contains(
            "correctness row alone cannot establish operational qualification",
            Matrix,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Conformance_plan_drives_profile_specific_sproc_configuration()
    {
        Assert.Contains(
            "--profile \"$QUALIFICATION_PROFILE\"",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            ".configuration.dynamo_db_stored_procedure_mode",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "AWS2AZURE_DDB_STORED_PROCEDURE_MODE=$ddb_sproc_mode",
            Workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "AWS2AZURE_DDB_STORED_PROCEDURE_MODE",
            RealAzureFixtureSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "mode == \"Preferred\"",
            RealAzureFixtureSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AWS2AZURE_DDB_STORED_PROCEDURE_MODE",
            DynamoDbFixtureSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "source_validation",
            DynamoDbFixtureSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "dynamodb-single-partition-transactions",
            DynamoDbFixtureSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AWS2AZURE_DDB_STORED_PROCEDURE_MODE: Disabled",
            LoadWorkflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "dynamodb-basic-crud)\n",
            Workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "dynamodb-single-partition-transactions)\n",
            Workflow,
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
