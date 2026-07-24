using Aws2Azure.IntegrationTests.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbTransactions")]
[Trait("Category", "OperationalQualification")]
[Collection(DynamoDbRealAzureLoadCollection.Name)]
public sealed class DynamoDbRealAzureTransactionQualificationTests(
    DynamoDbRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Adjacent_runtime_transaction_rollback_is_atomic_and_compatible()
    {
        Skip.IfNot(
            fixture.CosmosConfigured,
            "Real Azure Cosmos DB is not configured.");

        var profile = Environment.GetEnvironmentVariable(
            "AWS2AZURE_QUALIFICATION_PROFILE");
        var runtimeMode = Environment.GetEnvironmentVariable(
            "AWS2AZURE_SEALED_RUNTIME_MODE");
        if (string.Equals(
                profile,
                "dynamodb-single-partition-transactions",
                StringComparison.Ordinal)
            && !string.Equals(
                runtimeMode,
                "source_validation",
                StringComparison.Ordinal))
        {
            Assert.Equal("rollback", runtimeMode);
            Assert.True(
                fixture.SealedRollbackConfigured,
                "The sealed transaction profile must configure exact candidate and prior runtimes.");
        }
        else
        {
            Skip.IfNot(
                fixture.SealedRollbackConfigured,
                "Exact candidate and prior sealed runtimes are required.");
        }

        var result =
            await RealAzureRollbackQualification.VerifyDynamoDbTransactionsAsync(
                fixture);

        Assert.Equal("rollback", result.Proof.ScenarioId);
        Assert.Equal("dynamodb", result.Proof.Service);
        Assert.Equal("TransactGetItems", result.Proof.Operation);
    }
}
