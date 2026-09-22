using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;

namespace Aws2Azure.IntegrationTests.Sqs;

[Trait("Category", "RealAzure")]
[Trait("Category", "SqsRcObservation")]
[Collection(RealAzureCollection.Name)]
public sealed class SqsRealAzureRcObservationTests(RealAzureProxyFixture fixture)
{
    [SkippableFact]
    public async Task Sealed_standard_messaging_cohort_observes_and_restores_exact_prior()
    {
        Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
            "AWS2AZURE_RC_OBSERVATION_COHORT_CAPTURE_PATH")), "No RC cohort capture requested.");
        Assert.True(fixture.ServiceBusConfigured && fixture.SealedRollbackConfigured,
            "RC observation requires the real Service Bus backend and both verified sealed runtimes.");
        var canary = new SqsRcCanary(() => fixture.CreateSqsClient());
        using var client = fixture.CreateSqsClient();
        var iterations = new CompletedIterationCounter();
        await RcCrudCohort.RunFromEnvironmentAsync(
            "sqs-standard-messaging", "sqs", "servicebus", "ReceiveMessage",
            SqsRealAzureLoadQualificationTests.Operations,
            new(fixture.CandidateRuntimeIdentityDigest, fixture.CandidateRuntimeIdentity.Runtime.AggregateDigest,
                fixture.PriorRuntimeIdentityDigest, fixture.PriorRuntimeIdentity.Runtime.AggregateDigest,
                fixture.GetServiceUrl("sqs"), () => fixture.IsProxyRunning,
                () =>
                {
                    fixture.VerifyConfigurationUnchanged();
                    return new(fixture.ServiceBusBackendIdentityDigest, fixture.ProxyConfigDigest, fixture.AwsBindingDigest);
                },
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await fixture.StopForRuntimeSwitchAsync().ConfigureAwait(false);
                    await fixture.StartRuntimeAsync(SealedRuntimeRole.Prior).ConfigureAwait(false);
                }),
            canary.PrepareAsync, canary.VerifyRestoredAsync, canary.CleanupAsync,
            (worker, tracker, duration, stopwatch, token) =>
                SqsRealAzureLoadQualificationTests.RunWorkerAsync(
                    client, tracker, iterations, worker, duration, stopwatch, token, strictObservation: true))
            .ConfigureAwait(false);
    }
}
