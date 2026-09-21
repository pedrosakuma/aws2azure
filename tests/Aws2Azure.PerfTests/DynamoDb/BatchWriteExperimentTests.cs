using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.IntegrationTests.DynamoDb;
using Aws2Azure.Modules.DynamoDb.Internal;
using DotNet.Testcontainers.Builders;
using Xunit;
using CosmosClient = Aws2Azure.Modules.DynamoDb.Internal.CosmosClient;

namespace Aws2Azure.PerfTests.DynamoDb;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BatchWriteExperimentCollection
{
    public const string Name = "Isolated BatchWriteItem diagnostic";
}

[Collection(BatchWriteExperimentCollection.Name)]
[Trait("Category", "BatchWriteExperiment")]
public sealed class BatchWriteExperimentTests
{
    private const string Image = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
    private const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    private const string Database = "batch-experiment";
    private const string Table = "experiment";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [SkippableFact]
    public async Task Selected_cell_records_bounded_diagnostic()
    {
        var enabled = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_EXPERIMENT");
        Skip.If(enabled is null or "0", "Dedicated batch experiment opt-in is absent.");
        if (enabled != "1") throw new ArgumentException("AWS2AZURE_BATCH_EXPERIMENT must be 1.");
        if (Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_EXCLUSIVE_HOST") != "1")
            throw new ArgumentException("A host reserved for this experiment must be acknowledged.");
        var plan = BatchWriteExperimentPlan.Parse(Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_CELL"));
        var executionBlock = BatchWriteExperimentPlan.ParseExecutionBlock(Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_BLOCK"));
        var output = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_OUTPUT")
            ?? throw new ArgumentException("AWS2AZURE_BATCH_OUTPUT is required.");
        Directory.CreateDirectory(output);
        var reportPath = Path.Combine(output, $"{Guid.NewGuid():N}.json");
        var root = FindRoot();
        var source = await CommandAsync("git", ["rev-parse", "HEAD"], root);
        var runtimeDirectory = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_RUNTIME")
            ?? throw new ArgumentException("AWS2AZURE_BATCH_RUNTIME must identify a source-pinned published runtime.");
        var runtimeIdentity = BatchWriteRuntime.Verify(runtimeDirectory);

        var image = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_IMAGE") ?? Image;
        var container = new ContainerBuilder(image)
            .WithPortBinding(8081, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("System is now fully ready to accept requests"))
            .Build();
        var setup = Stopwatch.StartNew();
        var accounting = new BatchWriteExperimentAccounting();
        var clock = new Stopwatch();
        var warmupSeconds = 0.0;
        var cleanupSeconds = 0.0;
        var dispatchSeconds = 0.0;
        var cleanupComplete = false;
        var measurementStarted = false;
        string? failure = null;
        string? imageId = null;
        RuntimeMemorySnapshot? memoryBefore = null, memoryAfter = null;
        BatchWriteProcessSample? proxyBefore = null, proxyAfter = null, driverBefore = null, driverAfter = null;
        Dictionary<string, double>? stagesBefore = null, stagesAfter = null;
        BatchWriteResourceWindow? resourceWindow = null;
        var snapshotFailures = new List<string>();
        bool? relayIdleBeforeSnapshot = null;
        var finalSnapshotSeconds = 0.0;
        var incompleteOrFaultedObservation = false;
        Process? profiler = null;
        int? profileTargetProcessId = null;
        string? profileFile = null;
        object? rest = null;
        var relay = new BatchWriteExperimentRelay();
        var proxy = new PerfProxyProcess();
        try
        {
            using var setupDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await container.StartAsync(setupDeadline.Token);
            imageId = await CommandAsync("docker", ["inspect", "--format", "{{.Image}}", container.Id], root);
            var endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(8081)}/";
            using var bootstrap = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            await CosmosRestBootstrap.EnsureDatabaseAsync(bootstrap, endpoint, Key, Database).WaitAsync(setupDeadline.Token);
            await relay.StartAsync(endpoint, setupDeadline.Token);
            var config = $$$$"""
                {
                  "services": { "s3": {"enabled":false}, "sqs": {"enabled":false}, "dynamodb": {"enabled":true} },
                  "bindings": [{
                    "aws": {"accessKeyId":"AKIA-BATCH-EXPERIMENT", "secretAccessKey":"batch-experiment"},
                    "azure": {"dynamodb": {"kind":"cosmos", "target":{"endpoint":"{{{{relay.Endpoint}}}}","databaseName":"{{{{Database}}}}"},
                      "auth":{"mode":"sharedKey","key":"{{{{Key}}}}"}}}
                  }]
                }
                """;
            await proxy.StartAsync(config, TimeSpan.FromMinutes(2),
                Path.Combine(runtimeDirectory, "app", runtimeIdentity.Executable), batchDiagnostics: true);
            using var aws = new AmazonDynamoDBClient("AKIA-BATCH-EXPERIMENT", "batch-experiment", new AmazonDynamoDBConfig
            {
                ServiceURL = proxy.ServiceUrlForHost("dynamodb"), AuthenticationRegion = "us-east-1",
                UseHttp = true, MaxErrorRetry = 0,
            });
            await aws.CreateTableAsync(new CreateTableRequest
            {
                TableName = Table, BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions = [new("pk", ScalarAttributeType.S), new("sk", ScalarAttributeType.S)],
                KeySchema = [new("pk", KeyType.HASH), new("sk", KeyType.RANGE)],
            }, setupDeadline.Token);
            using var transport = new AzureHttpClient();
            var cosmos = new CosmosClient(transport,
                new CosmosCredentials { Endpoint = relay.Endpoint, DatabaseName = Database },
                new MasterKeyCosmosAuthenticator(Key));
            var inventory = Enumerable.Range(0, BatchWriteExperimentPlan.MaxBatches + BatchWriteExperimentPlan.WarmupBatches)
                .Select(plan.Items).ToArray();
            // Seed actual existing items for deletes, never measure idempotent misses as delete capacity.
            foreach (var item in inventory.SelectMany(x => x).Where(x => x.Delete))
                if (!await WriteDirectAsync(cosmos, item with { Delete = false }, setupDeadline.Token))
                    throw new InvalidOperationException("Seed write throttled; experiment inventory is incomplete.");
            setup.Stop();

            async Task<List<WriteRequest>> Submit(List<WriteRequest> pending, CancellationToken ct)
            {
                if (plan.Route == "proxy")
                {
                    var response = await aws.BatchWriteItemAsync(new BatchWriteItemRequest
                    {
                        RequestItems = new() { [Table] = pending },
                    }, ct);
                    if (response.UnprocessedItems is { Count: > 0 } unprocessed)
                    {
                        if (unprocessed.Count != 1 || !unprocessed.TryGetValue(Table, out var remaining))
                            throw new InvalidOperationException("Unexpected table in UnprocessedItems.");
                        return remaining;
                    }
                    return [];
                }
                using var limiter = new SemaphoreSlim(10);
                var results = await Task.WhenAll(pending.Select(async request =>
                {
                    var attributes = request.DeleteRequest?.Key ?? request.PutRequest.Item;
                    var item = new BatchWriteExperimentItem(attributes["pk"].S, attributes["sk"].S, request.DeleteRequest is not null);
                    await limiter.WaitAsync(ct);
                    try { return await WriteDirectAsync(cosmos, item, ct) ? null : request; }
                    finally { limiter.Release(); }
                }));
                return results.Where(x => x is not null).Select(x => x!).ToList();
            }

            var warmup = Stopwatch.StartNew();
            for (var i = 0; i < BatchWriteExperimentPlan.WarmupBatches; i++)
            {
                var warmupAccounting = new BatchWriteExperimentAccounting();
                await BatchWriteExperimentAccounting.DrainAsync(inventory[i].Select(x => x.Write()).ToList(), Submit, warmupAccounting, setupDeadline.Token);
            }
            warmupSeconds = warmup.Elapsed.TotalSeconds;
            var traceTool = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_TRACE_TOOL");
            if (traceTool is not null)
            {
                profileTargetProcessId = proxy.ProcessId;
                profileFile = Path.ChangeExtension(reportPath, ".nettrace");
                var start = new ProcessStartInfo(traceTool)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };
                foreach (var argument in new[] { "collect", "--process-id", proxy.ProcessId.ToString(),
                    "--duration", "00:00:10", "--buffersize", "32", "--output", profileFile,
                    "--providers", "Microsoft-Windows-DotNETRuntime:0x1:5,Microsoft-DotNETCore-SampleProfiler:0x0:4" })
                    start.ArgumentList.Add(argument);
                profiler = Process.Start(start) ?? throw new InvalidOperationException("Profiler did not start.");
                using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    var line = await profiler.StandardOutput.ReadLineAsync(readyDeadline.Token);
                    if (line is null) throw new InvalidOperationException("Profiler exited before recording.");
                    if (line.StartsWith("Trace Duration :", StringComparison.Ordinal)) break;
                }
            }
            using var memory = proxy.CreateMemoryProbe();
            memoryBefore = await memory.SampleAsync(setupDeadline.Token);
            stagesBefore = await BatchWriteStageTelemetry.ScrapeAsync(proxy.ServiceUrl);
            proxyBefore = BatchWriteProcessSample.Capture(proxy.ProcessId);
            driverBefore = BatchWriteProcessSample.Capture(Environment.ProcessId, currentProcess: true);
            relay.Begin();
            measurementStarted = true;
            clock.Start();
            resourceWindow = new(proxy.ProcessId);
            resourceWindow.Start();
            using var measuredDeadline = new CancellationTokenSource(BatchWriteExperimentPlan.DispatchDuration + BatchWriteExperimentPlan.DrainTimeout);
            var reserved = 0;
            var workers = Enumerable.Range(0, plan.Concurrency).Select(async _ =>
            {
                while (clock.Elapsed < BatchWriteExperimentPlan.DispatchDuration)
                {
                    var batch = Interlocked.Increment(ref reserved) - 1;
                    if (batch >= BatchWriteExperimentPlan.MaxBatches) break;
                    accounting.Started();
                    var start = Stopwatch.GetTimestamp();
                    var failed = true;
                    try
                    {
                        await BatchWriteExperimentAccounting.DrainAsync(
                            inventory[batch + BatchWriteExperimentPlan.WarmupBatches].Select(x => x.Write()).ToList(),
                            Submit, accounting, measuredDeadline.Token);
                        failed = false;
                    }
                    finally { accounting.Settled(Stopwatch.GetElapsedTime(start).TotalMilliseconds, failed); }
                }
            }).ToArray();
            await Task.WhenAll(workers);
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name;
            throw;
        }
        finally
        {
            clock.Stop();
            setup.Stop();
            if (measurementStarted)
            {
                var snapshotClock = Stopwatch.StartNew();
                dispatchSeconds = Math.Min(clock.Elapsed.TotalSeconds, BatchWriteExperimentPlan.DispatchDuration.TotalSeconds);
                if (resourceWindow is not null)
                {
                    try { await resourceWindow.StopAsync(); }
                    catch (Exception ex) { snapshotFailures.Add("window:" + BatchWriteExperimentRelay.SafeExceptionType(ex)); }
                }
                relayIdleBeforeSnapshot = await relay.WaitForIdleAsync();
                rest = relay.End(out incompleteOrFaultedObservation);
                if (incompleteOrFaultedObservation && failure is null) failure = "RelayObservationFailure";
                var final = await BatchWriteFinalSnapshots.CaptureAsync(proxy.ProcessId, proxy.ServiceUrl);
                proxyAfter = final.Proxy;
                driverAfter = final.Driver;
                stagesAfter = final.Stages;
                memoryAfter = final.Memory;
                snapshotFailures.AddRange(final.Unavailable);
                finalSnapshotSeconds = snapshotClock.Elapsed.TotalSeconds;
            }
            if (resourceWindow is not null)
            {
                try { await resourceWindow.DisposeAsync(); }
                catch (Exception ex) { snapshotFailures.Add("window-dispose:" + BatchWriteExperimentRelay.SafeExceptionType(ex)); }
            }
            var cleanup = Stopwatch.StartNew();
            try
            {
                try
                {
                    if (profiler is not null)
                    {
                        try
                        {
                            var outputTask = profiler.StandardOutput.ReadToEndAsync();
                            await profiler.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                            await File.WriteAllTextAsync(profileFile + ".log", await outputTask);
                            if (profiler.ExitCode != 0) snapshotFailures.Add("profile:nonzero-exit");
                        }
                        catch
                        {
                            if (!profiler.HasExited) profiler.Kill(entireProcessTree: true);
                            snapshotFailures.Add("profile:incomplete");
                        }
                        finally { profiler.Dispose(); }
                    }
                    await proxy.DisposeAsync();
                }
                finally
                {
                    try { await relay.DisposeAsync(); }
                    finally { await container.DisposeAsync(); }
                }
                cleanupComplete = true;
            }
            finally
            {
                cleanupSeconds = cleanup.Elapsed.TotalSeconds;
                await using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(file, new
                {
                    schemaVersion = 3, reportOnly = true, promotable = false, plan, harnessSource = source,
                    runtimeSlot = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_SLOT"),
                    executionBlock,
                    profileTargetProcessId, profileFile,
                    campaignPurpose = Environment.GetEnvironmentVariable("AWS2AZURE_BATCH_PURPOSE") ?? "standard",
                    runtimeIdentity,
                    capturedAtUtc = DateTimeOffset.UtcNow, imageTag = image, imageId,
                    backend = "exclusive disposable local Cosmos emulator; no Azure capacity claim",
                    host = new { Environment.ProcessorCount, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString() },
                    isolation = "Exclusive-host operator acknowledgement plus nonparallel xUnit collection; not proof of absence of external host processes.",
                    setupSeconds = setup.Elapsed.TotalSeconds, warmupSeconds, dispatchSeconds,
                    settledSeconds = clock.Elapsed.TotalSeconds,
                    drainSeconds = Math.Max(0, clock.Elapsed.TotalSeconds - dispatchSeconds),
                    cleanupSeconds, cleanupComplete, failure,
                    measurement = clock.Elapsed.TotalSeconds > 0 ? accounting.Snapshot(clock.Elapsed.TotalSeconds) : null,
                    rest, proxyMemoryBefore = memoryBefore, proxyMemoryAfter = memoryAfter,
                    relayIdleBeforeSnapshot, snapshotFailures, finalSnapshotSeconds,
                    proxyProcess = BatchWriteProcessSample.Delta(proxyBefore, proxyAfter),
                    driverProcess = BatchWriteProcessSample.Delta(driverBefore, driverAfter),
                    resourcesDuringWindow = resourceWindow?.Snapshot(),
                    proxyAllocatedBytes = memoryAfter?.AllocatedBytesTotal - memoryBefore?.AllocatedBytesTotal,
                    proxyGen2Collections = memoryAfter?.Gen2Collections - memoryBefore?.Gen2Collections,
                    stages = plan.Route == "proxy" ? BatchWriteStageTelemetry.Delta(stagesBefore, stagesAfter) : null,
                    resourceScope = "Process-wide bracketed deltas: proxy PID; driver PID includes SDK/direct transport, relay, xUnit and diagnostics. No backend attribution. Working sets are endpoints; peak is process-lifetime, not window peak.",
                    itemLatencyScope = "Caller-visible item acknowledgement from entry to initial submission loop through the response confirming the item. Includes SDK serialization, all earlier submissions and backoff; items acknowledged together share a timestamp. Not backend individual completion time.",
                    unavailable = new[]
                    {
                        "Stage times are wall time, not CPU attribution; overlapping item sums are not request latency. Missing baseline stages remain null. No percentile subtraction.",
                        "Backend CPU/allocation, true window peak memory, CPU stacks and causal resource-saturation attribution are unavailable; periodic process samples can miss peaks.",
                        "Internal transport retry/backoff duration is not separately instrumented; repeated REST attempts and caller resubmission backoff are observed.",
                        "Direct REST uses the same Cosmos transport/encoder and per-batch limit 10, not an independent implementation or SDK baseline.",
                    },
                }, JsonOptions);
            }
        }
        if (incompleteOrFaultedObservation)
            throw new InvalidOperationException("Relay observation was incomplete or faulted; the diagnostic report is not a clean measurement.");
    }

    private static async Task<bool> WriteDirectAsync(CosmosClient cosmos, BatchWriteExperimentItem item, CancellationToken ct)
    {
        var collection = $"dbs/{Database}/colls/{Table}";
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { item.CosmosPk })),
        };
        HttpResponseMessage response;
        if (item.Delete)
        {
            var document = collection + "/docs/" + item.CosmosId;
            response = await cosmos.SendAsync(HttpMethod.Delete, "docs", document, "/" + document, null, headers, ct);
        }
        else
        {
            headers.Add(new("x-ms-documentdb-is-upsert", "true"));
            response = await cosmos.SendAsync(HttpMethod.Post, "docs", collection, "/" + collection + "/docs",
                item.CosmosDocument(), "application/json", headers, ct);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return false;
            response.EnsureSuccessStatusCode();
            return true;
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "aws2azure.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static async Task<string> CommandAsync(string file, string[] arguments, string directory)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {file}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {await stderr}");
        return (await stdout).Trim();
    }
}
