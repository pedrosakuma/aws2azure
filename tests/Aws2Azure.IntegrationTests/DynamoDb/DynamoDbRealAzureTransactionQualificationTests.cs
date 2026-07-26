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
    private const string ProfileId =
        "dynamodb-single-partition-transactions";
    private const string BootstrapRuntimeDigest =
        "sha256:8ed5e089baeacb3e703ffae788a148e6de89f355f97c3fd10e3a74536298314b";

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
        Assert.Equal(ProfileId, profile);
        Assert.Equal("rollback", runtimeMode);
        Assert.True(
            fixture.SealedCandidateConfigured,
            "The exact sealed candidate runtime is required.");
        Assert.True(
            fixture.SealedRollbackConfigured,
            "The exact candidate and committed bootstrap prior are required.");
        Assert.Equal("candidate", fixture.CandidateRuntimeIdentity.Role);
        Assert.Equal("candidate", fixture.CandidateRuntimeIdentity.Status);
        Assert.Equal("prior", fixture.PriorRuntimeIdentity.Role);
        Assert.Equal("bootstrap", fixture.PriorRuntimeIdentity.Status);
        Assert.True(
            fixture.PriorRuntimeIdentity.Eligibility.RollbackBaselineEligible);
        Assert.False(
            fixture.PriorRuntimeIdentity.Eligibility.PromotionEligible);
        Assert.Equal(
            BootstrapRuntimeDigest,
            fixture.PriorRuntimeIdentity.Runtime.AggregateDigest);
        Assert.NotEqual(
            fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
            fixture.PriorRuntimeIdentity.Runtime.AggregateDigest);

        var result =
            await RealAzureRollbackQualification.VerifyDynamoDbTransactionsAsync(
                fixture);

        Assert.Equal("rollback", result.Proof.ScenarioId);
        Assert.Equal("dynamodb", result.Proof.Service);
        Assert.Equal("TransactGetItems", result.Proof.Operation);
    }
}
