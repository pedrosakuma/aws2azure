using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.IntegrationTests.OperationalQualification;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.TestSupport.OperationalQualification;
using Xunit;
using static Aws2Azure.IntegrationTests.OperationalQualification.RealAzureWorkloadLoad;
using DynamoDbResourceNotFoundException =
    Amazon.DynamoDBv2.Model.ResourceNotFoundException;

namespace Aws2Azure.IntegrationTests.DynamoDb;



public sealed partial class DynamoDbRealAzureTransactionLoadQualificationTests
{
    private void ValidateRuntimeIdentities()
    {
        var candidate = fixture.CandidateRuntimeIdentity;
        var prior = fixture.PriorRuntimeIdentity;
        if (candidate.Role != "candidate" || candidate.Status != "candidate"
            || prior.Role != "prior" || prior.Status != "bootstrap"
            || !prior.Eligibility.RollbackBaselineEligible
            || prior.Eligibility.PromotionEligible
            || prior.Runtime.AggregateDigest != BootstrapRuntimeDigest
            || candidate.Runtime.AggregateDigest == prior.Runtime.AggregateDigest)
        {
            throw new InvalidDataException(
                "Transaction load qualification requires the distinct committed rollback-only bootstrap.");
        }
    }

    private RealAzureWorkloadLoadEvidence BuildEvidence(
        int concurrency,
        TimeSpan requestedDuration,
        List<RealAzureWorkloadLoadOperationMeasurement> operationMix,
        List<RealAzureWorkloadLoadScenario> scenarios,
        List<RealAzureWorkloadLoadSignal> signals,
        RealAzureRollbackResult rollback,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd) => new()
    {
        SchemaVersion = 1,
        Profile = new RealAzureWorkloadLoadProfile
        {
            Id = ProfileId,
            Version = 1,
            Services =
            [
                new RealAzureWorkloadLoadProfileService
                {
                    Service = Service,
                    Operations = Operations.ToList(),
                }
            ],
        },
        Candidate = new RealAzureWorkloadLoadCandidate
        {
            GitSha = RequiredEnvironment("AWS2AZURE_LOAD_GIT_SHA"),
            ArtifactDigest = RequiredEnvironment("AWS2AZURE_LOAD_ARTIFACT_DIGEST"),
            ConfigDigest = RequiredEnvironment("AWS2AZURE_LOAD_CONFIG_DIGEST"),
            QualificationMode = "sealed",
            Runtime = fixture.CandidateRuntimeIdentity,
        },
        Provenance = new RealAzureWorkloadLoadProvenance
        {
            RunId = RequiredEnvironment("GITHUB_RUN_ID"),
            RunUrl = RequiredEnvironment("AWS2AZURE_LOAD_RUN_URL"),
            RunAttempt = ReadPositiveInt("GITHUB_RUN_ATTEMPT", 1),
            GeneratedAtUtc = windowEnd,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            Region = RequiredEnvironment("AZURE_LOCATION"),
            BackendDescription = RequiredEnvironment(
                "AWS2AZURE_LOAD_BACKEND_DESCRIPTION"),
            ProducerConfigDigest = RequiredEnvironment(
                "AWS2AZURE_LOAD_PRODUCER_CONFIG_DIGEST"),
        },
        LoadShape = new RealAzureWorkloadLoadShape
        {
            Concurrency = concurrency,
            RequestedDurationSeconds = requestedDuration.TotalSeconds,
        },
        OperationMix = operationMix,
        Scenarios = scenarios,
        Signals = signals,
        RollbackProofs = [rollback.Proof],
    };

    private static async Task PublishAsync(
        RealAzureWorkloadLoadEvidence evidence,
        string fullOutputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var pendingOutputPath = $"{fullOutputPath}.pending";
        File.Delete(pendingOutputPath);
        try
        {
            await File.WriteAllTextAsync(
                pendingOutputPath,
                JsonSerializer.Serialize(
                    evidence,
                    RealAzureWorkloadLoadEvidenceJsonContext.Default
                        .RealAzureWorkloadLoadEvidence),
                cancellationToken).ConfigureAwait(false);
            File.Move(pendingOutputPath, fullOutputPath, true);
        }
        finally
        {
            File.Delete(pendingOutputPath);
        }
    }

    private static async Task RunWorkerAsync(
        IAmazonDynamoDB client,
        string table,
        RealAzureWorkloadLoadTracker tracker,
        CompletedIterationCounter completedIterations,
        int worker,
        TimeSpan iterationInterval,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var iteration = 0;
        var writeSorts = Enumerable.Range(0, 5)
            .Select(index => $"load-{worker:D2}-write-{index:D2}")
            .ToArray();
        var readSorts = writeSorts
            .Concat(Enumerable.Range(0, 5)
                .Select(index => $"load-{worker:D2}-seed-{index:D2}"))
            .ToArray();
        while (stopwatch.Elapsed < duration)
        {
            completedIterations.RecordStarted();
            var version = $"{worker:D2}-{iteration++:D8}";
            try
            {
                await MeasureAsync(
                    tracker,
                    "TransactWriteItems",
                    () => client.TransactWriteItemsAsync(
                        new TransactWriteItemsRequest
                        {
                            TransactItems = writeSorts
                                .Select(sort => Put(table, Partition, sort, version))
                                .ToList(),
                        },
                        cancellationToken),
                    IsThrottle).ConfigureAwait(false);
                await completedIterations.CompleteAfterAsync(() =>
                    MeasureAsync(
                        tracker,
                        "TransactGetItems",
                        async () =>
                        {
                            var response = await client.TransactGetItemsAsync(
                                new TransactGetItemsRequest
                                {
                                    TransactItems = readSorts
                                        .Select(sort => Get(table, Partition, sort))
                                        .ToList(),
                                },
                                cancellationToken).ConfigureAwait(false);
                            if (response.Responses.Count != 10)
                            {
                                throw new InvalidDataException(
                                    "Transaction load read did not return ten items.");
                            }
                            for (var index = 0; index < response.Responses.Count; index++)
                            {
                                var expected = index < 5 ? version : "seed";
                                if (response.Responses[index].Item["version"].S != expected)
                                {
                                    throw new InvalidDataException(
                                        "Transaction load read returned an unexpected version.");
                                }
                            }
                        },
                        IsThrottle)).ConfigureAwait(false);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (stopwatch.Elapsed < duration)
                {
                    await Task.Delay(iterationInterval, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

}
