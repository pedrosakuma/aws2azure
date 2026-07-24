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
        Skip.IfNot(
            fixture.SealedRollbackConfigured,
            "Exact candidate and prior sealed runtimes are required.");

        var result =
            await RealAzureRollbackQualification.VerifyDynamoDbTransactionsAsync(
                fixture);

        Assert.Equal("rollback", result.Proof.ScenarioId);
        Assert.Equal("dynamodb", result.Proof.Service);
        Assert.Equal("TransactGetItems", result.Proof.Operation);
    }
}
