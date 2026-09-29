using System.Text.Json;
using System.Text.Json.Serialization;
using Aws2Azure.TestSupport.OperationalQualification;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal static class DynamoDbCrossover
{
    internal const int Concurrency = 8;
    internal static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MeasurementDuration = TimeSpan.FromMinutes(5);
    private static readonly SealedRuntimeRole[] Roles =
    [
        SealedRuntimeRole.Prior, SealedRuntimeRole.Candidate,
        SealedRuntimeRole.Candidate, SealedRuntimeRole.Prior,
        SealedRuntimeRole.Prior, SealedRuntimeRole.Prior,
    ];

    internal static async Task RunAsync(
        DynamoDbCrossoverReport report,
        Func<SealedRuntimeRole, CancellationToken, Task> restart,
        Func<int, SealedRuntimeRole, bool, TimeSpan, CancellationToken, Task<OperationTimingReport>> measure,
        Func<CancellationToken, Task<double[]>> probe,
        Func<RcCohortBinding> binding,
        Func<DynamoDbCrossoverReport, Task> publish,
        CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            if (report.CandidateDigest == report.PriorDigest)
                throw new InvalidDataException("Crossover requires distinct sealed candidate and prior runtimes.");
            for (var index = 0; index < Roles.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var role = Roles[index];
                var slot = new DynamoDbCrossoverSlot
                {
                    Index = index,
                    Comparison = index < 4 ? "ABBA" : "AA",
                    Role = role == SealedRuntimeRole.Candidate ? "candidate" : "prior",
                    RuntimeDigest = role == SealedRuntimeRole.Candidate
                        ? report.CandidateDigest : report.PriorDigest,
                };
                report.Slots.Add(slot);
                RequireBinding();
                await restart(role, token).ConfigureAwait(false);
                RequireBinding();
                slot.NetworkBeforeMilliseconds = await probe(token).ConfigureAwait(false);
                slot.Warmup = await measure(index, role, true, WarmupDuration, token).ConfigureAwait(false);
                RequireBinding();
                slot.Measurement = await measure(index, role, false, MeasurementDuration, token)
                    .ConfigureAwait(false);
                RequireBinding();
                slot.NetworkAfterMilliseconds = await probe(token).ConfigureAwait(false);
                slot.Completed = true;
                await publish(report).ConfigureAwait(false);
            }
            report.Completed = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            report.Failure = RealAzureWorkloadFirstFailure.FromException(exception, false);
            throw;
        }
        finally
        {
            report.EndedAtUtc = DateTimeOffset.UtcNow;
            try
            {
                await publish(report).ConfigureAwait(false);
            }
            catch (Exception reportingFailure) when (failure is not null)
            {
                throw new AggregateException("Crossover and diagnostic publication failed.", failure, reportingFailure);
            }
        }

        void RequireBinding()
        {
            if (binding() != report.Binding)
                throw new InvalidDataException("Crossover backend, configuration or AWS binding changed.");
        }
    }
}

internal sealed class DynamoDbCrossoverReport
{
    public int SchemaVersion => 1;
    public string ArtifactKind => "dynamodb_controlled_crossover_diagnostics";
    public bool Promotable => false;
    public string Profile => "dynamodb-basic-crud";
    public int Concurrency => DynamoDbCrossover.Concurrency;
    public string NetworkProbeScope => "unauthenticated_cosmos_response_headers_including_connection_setup_not_backend_latency";
    public string RunId { get; set; } = string.Empty;
    public string RunAttempt { get; set; } = string.Empty;
    public string HarnessSourceSha { get; set; } = string.Empty;
    public string CandidateDigest { get; set; } = string.Empty;
    public string PriorDigest { get; set; } = string.Empty;
    public string CandidateIdentityDigest { get; set; } = string.Empty;
    public string PriorIdentityDigest { get; set; } = string.Empty;
    public string BackendRegion { get; set; } = string.Empty;
    public RcCohortBinding Binding { get; set; } = new("", "", "");
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset EndedAtUtc { get; set; }
    public bool Completed { get; set; }
    public RealAzureWorkloadFirstFailure? Failure { get; set; }
    public List<DynamoDbCrossoverSlot> Slots { get; set; } = [];
}

internal sealed class DynamoDbCrossoverSlot
{
    public int Index { get; set; }
    public string Comparison { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string RuntimeDigest { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public double[] NetworkBeforeMilliseconds { get; set; } = [];
    public double[] NetworkAfterMilliseconds { get; set; } = [];
    public OperationTimingReport? Warmup { get; set; }
    public OperationTimingReport? Measurement { get; set; }
}

[JsonSerializable(typeof(DynamoDbCrossoverReport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class DynamoDbCrossoverJsonContext : JsonSerializerContext;
