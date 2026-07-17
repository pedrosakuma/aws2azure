using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Xunit;

namespace Aws2Azure.IntegrationTests.SecretsManager;

[Trait("Category", "RealAzure")]
[Trait("Category", "SecretsManagerLoadQualification")]
[Collection(SecretsManagerRealAzureCollection.Name)]
public sealed class SecretsManagerRealAzureLoadQualificationTests(
    SecretsManagerRealAzureProxyFixture fixture)
{
    private static readonly string[] Operations =
    [
        "CreateSecret",
        "DescribeSecret",
        "GetSecretValue",
        "PutSecretValue",
        "UpdateSecret",
        "ListSecrets",
        "DeleteSecret",
    ];

    [SkippableFact]
    public async Task Production_shaped_lifecycle_writes_immutable_load_evidence()
    {
        var outputPath = Environment.GetEnvironmentVariable("AWS2AZURE_LOAD_EVIDENCE_PATH");
        Skip.If(string.IsNullOrWhiteSpace(outputPath),
            "AWS2AZURE_LOAD_EVIDENCE_PATH is not set.");
        Skip.If(!fixture.Configured,
            fixture.SkipReason ?? "Real Azure Key Vault is not configured.");

        var concurrency = ReadPositiveInt("AWS2AZURE_LOAD_CONCURRENCY", 8);
        var requestedDuration = TimeSpan.FromSeconds(
            ReadPositiveInt("AWS2AZURE_LOAD_DURATION_SECONDS", 300));
        var tracker = new LoadTracker(Operations);
        var vaultUrl = RequiredEnvironment("AZURE_KEYVAULT_URL");
        var windowStart = DateTimeOffset.UtcNow;
        var networkBefore = await ProbeNetworkAsync(vaultUrl, 12).ConfigureAwait(false);
        var loadStart = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var client = fixture.CreateSecretsManagerClient();
        using var timeout = new CancellationTokenSource(requestedDuration + TimeSpan.FromMinutes(5));

        var workers = Enumerable.Range(0, concurrency)
            .Select(worker => RunWorkerAsync(
                client,
                tracker,
                worker,
                requestedDuration,
                stopwatch,
                timeout.Token))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        stopwatch.Stop();
        var loadEnd = DateTimeOffset.UtcNow;
        var networkAfter = await ProbeNetworkAsync(vaultUrl, 12).ConfigureAwait(false);
        var windowEnd = DateTimeOffset.UtcNow;

        var operationMix = tracker.Snapshot();
        var totalCompletions = operationMix.Sum(item => item.Completions);
        var totalFailures = operationMix.Sum(item => item.Failures);
        var representative = tracker.Snapshot("GetSecretValue");
        var representativeAttempts = representative.Completions + representative.Failures;
        var representativeLatencies = tracker.Latencies("GetSecretValue");
        var networkLatencies = networkBefore.Concat(networkAfter).ToArray();
        var evidence = new SecretsManagerLoadEvidence
        {
            SchemaVersion = 1,
            Profile = new LoadProfile
            {
                Id = "secretsmanager-basic-lifecycle",
                Version = 1,
                Services =
                [
                    new LoadProfileService
                    {
                        Service = "secretsmanager",
                        Operations = Operations.ToList(),
                    }
                ],
            },
            Candidate = new LoadCandidate
            {
                GitSha = RequiredEnvironment("AWS2AZURE_LOAD_GIT_SHA"),
                ArtifactDigest = RequiredEnvironment("AWS2AZURE_LOAD_ARTIFACT_DIGEST"),
                ConfigDigest = RequiredEnvironment("AWS2AZURE_LOAD_CONFIG_DIGEST"),
            },
            Provenance = new LoadProvenance
            {
                RunId = RequiredEnvironment("GITHUB_RUN_ID"),
                RunUrl = RequiredEnvironment("AWS2AZURE_LOAD_RUN_URL"),
                RunAttempt = ReadPositiveInt("GITHUB_RUN_ATTEMPT", 1),
                GeneratedAtUtc = windowEnd,
                WindowStartUtc = windowStart,
                WindowEndUtc = windowEnd,
                Region = RequiredEnvironment("AZURE_LOCATION"),
                BackendDescription = RequiredEnvironment("AWS2AZURE_LOAD_BACKEND_DESCRIPTION"),
            },
            LoadShape = new LoadShape
            {
                Concurrency = concurrency,
                RequestedDurationSeconds = requestedDuration.TotalSeconds,
            },
            OperationMix = operationMix,
            Scenarios =
            [
                Scenario(
                    "representative-load",
                    "GetSecretValue",
                    "real_azure",
                    representative.Completions,
                    representative.Failures,
                    0,
                    stopwatch.Elapsed.TotalSeconds,
                    loadEnd),
                Scenario("throttling", "GetSecretValue", "deterministic", 0, 0, 1, 0, loadEnd),
                Scenario("timeout", "GetSecretValue", "deterministic", 0, 0, 1, 0, loadEnd),
                Scenario("service-unavailable", "GetSecretValue", "deterministic", 0, 0, 1, 0, loadEnd),
                Scenario("cancellation", "GetSecretValue", "deterministic", 0, 0, 1, 0, loadEnd),
                Scenario("restart", "CreateSecret", "real_azure", 0, 0, 1, 0, loadEnd),
                Scenario("credential-rotation", "GetSecretValue", "real_azure", 0, 0, 1, 0, loadEnd),
                Scenario("rollback", "GetSecretValue", "real_azure", 0, 0, 1, 0, loadEnd),
            ],
            Signals =
            [
                Signal(
                    "representative-load-throughput",
                    "throughput_per_sec",
                    representative.Completions / stopwatch.Elapsed.TotalSeconds,
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-p95",
                    "p95_ms",
                    Percentile(representativeLatencies, 0.95),
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-p99",
                    "p99_ms",
                    Percentile(representativeLatencies, 0.99),
                    representativeAttempts,
                    loadEnd),
                Signal(
                    "representative-load-network-p95",
                    "p95_ms",
                    Percentile(networkLatencies, 0.95),
                    networkLatencies.LongLength,
                    windowEnd),
                Signal(
                    "representative-load-throttle-rate",
                    "throttle_rate",
                    representativeAttempts == 0
                        ? 0
                        : (double)tracker.Throttles("GetSecretValue") / representativeAttempts,
                    representativeAttempts,
                    loadEnd),
            ],
        };

        var fullOutputPath = Path.IsPathRooted(outputPath)
            ? outputPath!
            : Path.Combine(FindRepoRoot(), outputPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllTextAsync(
            fullOutputPath,
            JsonSerializer.Serialize(
                evidence,
                SecretsManagerLoadEvidenceJsonContext.Default.SecretsManagerLoadEvidence),
            timeout.Token).ConfigureAwait(false);

        Assert.True(totalCompletions > 0, "The production-shaped load completed no operations.");
        Assert.True(
            totalFailures == 0,
            $"{totalFailures} of {totalCompletions + totalFailures} operations failed." +
            $"{Environment.NewLine}{string.Join(
                ", ",
                operationMix
                    .Where(item => item.Failures > 0)
                    .Select(item => $"{item.Operation}={item.Failures}"))}" +
            $"{Environment.NewLine}{fixture.ProxyOutput}");
        Assert.All(operationMix, item => Assert.True(
            item.Completions > 0,
            $"{item.Operation} completed no requests."));
    }

    private static async Task RunWorkerAsync(
        IAmazonSecretsManager client,
        LoadTracker tracker,
        int worker,
        TimeSpan duration,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var iteration = 0;
        while (stopwatch.Elapsed < duration)
        {
            var name = $"a2a-load-{worker:x2}-{iteration++:x6}-{Guid.NewGuid():N}"[..48];
            var created = false;
            try
            {
                await MeasureAsync(tracker, "CreateSecret", async () =>
                {
                    await client.CreateSecretAsync(new CreateSecretRequest
                    {
                        Name = name,
                        SecretString = "value-1",
                        Description = "aws2azure production-shaped load qualification",
                    }, cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
                created = true;

                await MeasureAsync(tracker, "DescribeSecret", async () =>
                {
                    var response = await client.DescribeSecretAsync(
                        new DescribeSecretRequest { SecretId = name },
                        cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(response.Name, name, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("DescribeSecret returned the wrong secret.");
                    }
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "GetSecretValue", async () =>
                {
                    var response = await client.GetSecretValueAsync(
                        new GetSecretValueRequest { SecretId = name },
                        cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(response.SecretString, "value-1", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("GetSecretValue returned the wrong value.");
                    }
                }).ConfigureAwait(false);

                string putVersion = string.Empty;
                await MeasureAsync(tracker, "PutSecretValue", async () =>
                {
                    var response = await client.PutSecretValueAsync(new PutSecretValueRequest
                    {
                        SecretId = name,
                        SecretString = "value-2",
                    }, cancellationToken).ConfigureAwait(false);
                    putVersion = response.VersionId;
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "GetSecretValue", async () =>
                {
                    var response = await client.GetSecretValueAsync(new GetSecretValueRequest
                    {
                        SecretId = name,
                        VersionId = putVersion,
                    }, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(response.SecretString, "value-2", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Versioned GetSecretValue returned the wrong value.");
                    }
                }).ConfigureAwait(false);

                string updateVersion = string.Empty;
                await MeasureAsync(tracker, "UpdateSecret", async () =>
                {
                    var response = await client.UpdateSecretAsync(new UpdateSecretRequest
                    {
                        SecretId = name,
                        SecretString = "value-3",
                        Description = "aws2azure production-shaped load qualification updated",
                    }, cancellationToken).ConfigureAwait(false);
                    updateVersion = response.VersionId;
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "GetSecretValue", async () =>
                {
                    var response = await client.GetSecretValueAsync(new GetSecretValueRequest
                    {
                        SecretId = name,
                        VersionId = updateVersion,
                    }, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(response.SecretString, "value-3", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Updated GetSecretValue returned the wrong value.");
                    }
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "ListSecrets", async () =>
                {
                    var response = await client.ListSecretsAsync(
                        new ListSecretsRequest(),
                        cancellationToken).ConfigureAwait(false);
                    if (!response.SecretList.Any(item =>
                            string.Equals(item.Name, name, StringComparison.Ordinal)))
                    {
                        throw new InvalidDataException("ListSecrets omitted the active secret.");
                    }
                }).ConfigureAwait(false);

                await MeasureAsync(tracker, "DeleteSecret", async () =>
                {
                    await client.DeleteSecretAsync(new DeleteSecretRequest
                    {
                        SecretId = name,
                        ForceDeleteWithoutRecovery = true,
                    }, cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
                created = false;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (created)
                {
                    try
                    {
                        await client.DeleteSecretAsync(new DeleteSecretRequest
                        {
                            SecretId = name,
                            ForceDeleteWithoutRecovery = true,
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    private static async Task MeasureAsync(
        LoadTracker tracker,
        string operation,
        Func<Task> action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action().ConfigureAwait(false);
            tracker.RecordSuccess(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            tracker.RecordFailure(
                operation,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                IsThrottle(exception));
            throw;
        }
    }

    private static bool IsThrottle(Exception exception)
    {
        return exception is AmazonSecretsManagerException aws
               && (aws.StatusCode == HttpStatusCode.TooManyRequests
                   || string.Equals(aws.ErrorCode, "ThrottlingException", StringComparison.Ordinal));
    }

    private static async Task<double[]> ProbeNetworkAsync(string vaultUrl, int samples)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        var target = new Uri(new Uri(vaultUrl), "secrets?api-version=7.4");
        var latencies = new double[samples];
        for (var index = 0; index < samples; index++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(
                target,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return latencies;
    }

    private static LoadScenario Scenario(
        string id,
        string operation,
        string evidenceSource,
        long completions,
        long failures,
        long skipped,
        double durationSeconds,
        DateTimeOffset capturedAtUtc) => new()
    {
        Id = id,
        Service = "secretsmanager",
        Operation = operation,
        EvidenceSource = evidenceSource,
        Completions = completions,
        Failures = failures,
        Skipped = skipped,
        DurationSeconds = durationSeconds,
        CapturedAtUtc = capturedAtUtc,
    };

    private static LoadSignal Signal(
        string id,
        string metric,
        double measuredValue,
        long samples,
        DateTimeOffset capturedAtUtc) => new()
    {
        Id = id,
        ScenarioId = "representative-load",
        Metric = metric,
        MeasuredValue = measuredValue,
        Samples = samples,
        CapturedAtUtc = capturedAtUtc,
    };

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static int ReadPositiveInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new InvalidDataException($"{name} must be a positive integer.");
        }
        return parsed;
    }

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{name} is required for load evidence.");
        }
        return value;
    }

    private static string FindRepoRoot()
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
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class LoadTracker
    {
        private readonly IReadOnlyDictionary<string, OperationTracker> _operations;

        public LoadTracker(IEnumerable<string> operations)
        {
            _operations = operations.ToDictionary(
                operation => operation,
                _ => new OperationTracker(),
                StringComparer.Ordinal);
        }

        public void RecordSuccess(string operation, double elapsedMilliseconds)
        {
            _operations[operation].RecordSuccess(elapsedMilliseconds);
        }

        public void RecordFailure(string operation, double elapsedMilliseconds, bool throttled)
        {
            _operations[operation].RecordFailure(elapsedMilliseconds, throttled);
        }

        public List<LoadOperationMeasurement> Snapshot()
        {
            return _operations
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Value.Snapshot(item.Key))
                .ToList();
        }

        public LoadOperationMeasurement Snapshot(string operation)
        {
            return _operations[operation].Snapshot(operation);
        }

        public double[] Latencies(string operation)
        {
            return _operations[operation].Latencies.ToArray();
        }

        public long Throttles(string operation)
        {
            return _operations[operation].Throttles;
        }
    }

    private sealed class OperationTracker
    {
        private readonly ConcurrentQueue<double> _latencies = new();
        private long _completions;
        private long _failures;
        private long _throttles;

        public IEnumerable<double> Latencies => _latencies;
        public long Throttles => Interlocked.Read(ref _throttles);

        public void RecordSuccess(double elapsedMilliseconds)
        {
            _latencies.Enqueue(elapsedMilliseconds);
            Interlocked.Increment(ref _completions);
        }

        public void RecordFailure(double elapsedMilliseconds, bool throttled)
        {
            _latencies.Enqueue(elapsedMilliseconds);
            Interlocked.Increment(ref _failures);
            if (throttled)
            {
                Interlocked.Increment(ref _throttles);
            }
        }

        public LoadOperationMeasurement Snapshot(string operation)
        {
            var latencies = _latencies.ToArray();
            return new LoadOperationMeasurement
            {
                Service = "secretsmanager",
                Operation = operation,
                Completions = Interlocked.Read(ref _completions),
                Failures = Interlocked.Read(ref _failures),
                P95Milliseconds = Percentile(latencies, 0.95),
                P99Milliseconds = Percentile(latencies, 0.99),
            };
        }
    }
}

internal sealed class SecretsManagerLoadEvidence
{
    public int SchemaVersion { get; set; }
    public LoadProfile Profile { get; set; } = new();
    public LoadCandidate Candidate { get; set; } = new();
    public LoadProvenance Provenance { get; set; } = new();
    public LoadShape LoadShape { get; set; } = new();
    public List<LoadOperationMeasurement> OperationMix { get; set; } = new();
    public List<LoadScenario> Scenarios { get; set; } = new();
    public List<LoadSignal> Signals { get; set; } = new();
}

internal sealed class LoadProfile
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public List<LoadProfileService> Services { get; set; } = new();
}

internal sealed class LoadProfileService
{
    public string Service { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
}

internal sealed class LoadCandidate
{
    public string GitSha { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public string ConfigDigest { get; set; } = string.Empty;
}

internal sealed class LoadProvenance
{
    public string RunId { get; set; } = string.Empty;
    public string RunUrl { get; set; } = string.Empty;
    public int RunAttempt { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset WindowStartUtc { get; set; }
    public DateTimeOffset WindowEndUtc { get; set; }
    public string Region { get; set; } = string.Empty;
    public string BackendDescription { get; set; } = string.Empty;
}

internal sealed class LoadShape
{
    public int Concurrency { get; set; }
    public double RequestedDurationSeconds { get; set; }
}

internal sealed class LoadOperationMeasurement
{
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public long Completions { get; set; }
    public long Failures { get; set; }
    public double P95Milliseconds { get; set; }
    public double P99Milliseconds { get; set; }
}

internal sealed class LoadScenario
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string EvidenceSource { get; set; } = string.Empty;
    public long Completions { get; set; }
    public long Failures { get; set; }
    public long Skipped { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

internal sealed class LoadSignal
{
    public string Id { get; set; } = string.Empty;
    public string ScenarioId { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public double MeasuredValue { get; set; }
    public long Samples { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

[JsonSerializable(typeof(SecretsManagerLoadEvidence))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
internal sealed partial class SecretsManagerLoadEvidenceJsonContext : JsonSerializerContext;
