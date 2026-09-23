using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

[Trait("Category", "RealAzure")]
[Trait("Category", "DynamoDbRcObservation")]
[Collection(DynamoDbRealAzureLoadCollection.Name)]
public sealed class DynamoDbRealAzureRcObservationTests(DynamoDbRealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Sealed_crud_cohort_observes_and_restores_exact_prior()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            "AWS2AZURE_RC_OBSERVATION_COHORT_CAPTURE_PATH")), "No RC cohort capture requested.");
        Assert.True(fixture.CosmosConfigured && fixture.SealedRollbackConfigured,
            "RC observation requires the real Cosmos backend and both verified sealed runtimes.");
        var canary = new DynamoDbRcCanary(() => fixture.CreateDynamoDbClient());
        using var client = fixture.CreateDynamoDbClient();
        var iterations = new CompletedIterationCounter();
        await RcCrudCohort.RunFromEnvironmentAsync(
            "dynamodb-basic-crud", "dynamodb", "cosmos", "GetItem",
            DynamoDbRealAzureLoadQualificationTests.Operations,
            new(fixture.CandidateRuntimeIdentityDigest, fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
                fixture.PriorRuntimeIdentityDigest, fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
                fixture.ProxyServiceUrl, () => fixture.IsProxyRunning,
                () =>
                {
                    fixture.VerifyConfigurationUnchanged();
                    return new(fixture.BackendIdentityDigest, fixture.ProxyConfigDigest, fixture.AwsBindingDigest);
                },
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                    await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
                }),
            canary.PrepareAsync, canary.VerifyRestoredAsync, canary.CleanupAsync,
            (worker, tracker, duration, stopwatch, token) =>
                DynamoDbRealAzureLoadQualificationTests.RunWorkerAsync(
                    client, tracker, iterations, worker, duration, stopwatch, token, strictObservation: true),
            operationSchedule: DynamoDbRealAzureLoadQualificationTests.LifecycleOperationSchedule)
            .ConfigureAwait(false);
    }
}
