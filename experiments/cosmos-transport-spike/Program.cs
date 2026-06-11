using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Cosmos;

// =============================================================================
// Cosmos transport spike (issue #265)
//
// Measures the latency / throughput delta between:
//   1. SDK  Direct  (ConnectionMode.Direct  -> TCP/rntbd to the replica)
//   2. SDK  Gateway (ConnectionMode.Gateway -> HTTPS REST to the gateway)
//   3. Raw  REST    (HttpClient + master-key HMAC, mirroring the proxy's
//                    hand-rolled Aws2Azure.Modules.DynamoDb CosmosClient)
//
// Lanes 1 vs 2 isolate exactly the transport (same SDK, serializer, auth) =>
// the ceiling of what a Direct/rntbd implementation could buy us. Lane 3
// confirms our production REST path tracks the SDK gateway lane.
//
// This is a throwaway experiment. It is NOT in aws2azure.slnx and pulls in the
// Azure SDK only as a measuring stick. Run it by hand; see README.md.
// =============================================================================

static string Env(string key, string @default) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : @default;

static int EnvInt(string key, int @default) =>
    int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : @default;

var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT");
var key = Environment.GetEnvironmentVariable("COSMOS_KEY");
if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine(
        "Set COSMOS_ENDPOINT and COSMOS_KEY (master key) for a REAL Azure Cosmos account.\n" +
        "The local emulator does NOT do Direct mode faithfully, so Direct numbers there are meaningless.\n" +
        "See README.md for the full list of SPIKE_* knobs.");
    return 1;
}

var dbName = Env("COSMOS_DB", "spikeDb");
var containerName = Env("COSMOS_CONTAINER", "spikeItems");
var ru = EnvInt("SPIKE_RU", 4000);
var seedCount = EnvInt("SPIKE_ITEMS", 500);
var iterations = EnvInt("SPIKE_ITERATIONS", 1000);
var concurrency = EnvInt("SPIKE_CONCURRENCY", 32);
var durationSec = EnvInt("SPIKE_DURATION_SEC", 10);
var preferredRegion = Environment.GetEnvironmentVariable("SPIKE_PREFERRED_REGION");
var payloadBytes = EnvInt("SPIKE_PAYLOAD_BYTES", 256);
var lanes = Env("SPIKE_LANES", "direct,gateway,raw")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => s.ToLowerInvariant())
    .ToHashSet();

Console.WriteLine($"""
    Cosmos transport spike (#265)
      endpoint        : {endpoint}
      database        : {dbName}
      container       : {containerName} (partition key /pk, {ru} RU manual)
      seed items      : {seedCount}  (payload ~{payloadBytes} B)
      latency run     : {iterations} sequential point reads / lane
      throughput run  : {concurrency} workers x {durationSec}s / lane
      lanes           : {string.Join(", ", lanes)}
      preferred region: {preferredRegion ?? "(account default)"}
    """);

// --- Provision + seed via a gateway client (one-off, not measured) ----------
var ids = new List<(string id, string pk)>(seedCount);
var payload = new string('x', Math.Max(0, payloadBytes));

using (var bootstrap = new CosmosClient(endpoint, key, new CosmosClientOptions
{
    ConnectionMode = ConnectionMode.Gateway,
}))
{
    var db = (await bootstrap.CreateDatabaseIfNotExistsAsync(dbName)).Database;
    var container = (await db.CreateContainerIfNotExistsAsync(
        new ContainerProperties(containerName, "/pk"), throughput: ru)).Container;

    Console.WriteLine($"Seeding {seedCount} items...");
    for (var i = 0; i < seedCount; i++)
    {
        var id = Guid.NewGuid().ToString("N");
        ids.Add((id, id));
        await UpsertWithRetryAsync(container, new SpikeItem { id = id, pk = id, payload = payload });
    }
    Console.WriteLine("Seed complete.\n");
}

var results = new List<LaneResult>();

// --- Lane 1: SDK Direct ------------------------------------------------------
if (lanes.Contains("direct"))
{
    using var direct = MakeSdkClient(endpoint!, key!, ConnectionMode.Direct, preferredRegion);
    var c = direct.GetContainer(dbName, containerName);
    results.Add(await RunSdkPointReadAsync("SDK Direct", c, ids, iterations));
    results.Add(await RunSdkQueryAsync("SDK Direct", c, ids, Math.Min(iterations, 500)));
    results.Add(await RunSdkThroughputAsync("SDK Direct", c, ids, concurrency, durationSec));
}

// --- Lane 2: SDK Gateway -----------------------------------------------------
if (lanes.Contains("gateway"))
{
    using var gateway = MakeSdkClient(endpoint!, key!, ConnectionMode.Gateway, preferredRegion);
    var c = gateway.GetContainer(dbName, containerName);
    results.Add(await RunSdkPointReadAsync("SDK Gateway", c, ids, iterations));
    results.Add(await RunSdkQueryAsync("SDK Gateway", c, ids, Math.Min(iterations, 500)));
    results.Add(await RunSdkThroughputAsync("SDK Gateway", c, ids, concurrency, durationSec));
}

// --- Lane 3: Raw REST (mirrors the proxy's production CosmosClient) ----------
if (lanes.Contains("raw"))
{
    using var rawHttp = new HttpClient { BaseAddress = new Uri(endpoint!.TrimEnd('/') + "/") };
    var raw = new RawRestReader(rawHttp, dbName, containerName, key!);
    results.Add(await RunRawPointReadAsync("Raw REST", raw, ids, iterations));
    results.Add(await RunRawThroughputAsync("Raw REST", raw, ids, concurrency, durationSec));
}

// --- Report ------------------------------------------------------------------
PrintTable(results);
PrintDirectVsGateway(results);
return 0;

// ============================ helpers ========================================

static CosmosClient MakeSdkClient(string endpoint, string key, ConnectionMode mode, string? region)
{
    var opts = new CosmosClientOptions { ConnectionMode = mode };
    if (!string.IsNullOrWhiteSpace(region))
    {
        opts.ApplicationPreferredRegions = new List<string> { region };
    }
    return new CosmosClient(endpoint, key, opts);
}

static async Task UpsertWithRetryAsync(Container c, SpikeItem item)
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            await c.UpsertItemAsync(item, new PartitionKey(item.pk));
            return;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 10)
        {
            await Task.Delay(ex.RetryAfter ?? TimeSpan.FromMilliseconds(50));
        }
    }
}

static async Task<LaneResult> RunSdkPointReadAsync(string lane, Container c, List<(string id, string pk)> ids, int iterations)
{
    var rng = new Random(1);
    // warmup
    for (var i = 0; i < Math.Min(50, ids.Count); i++)
    {
        var (id, pk) = ids[rng.Next(ids.Count)];
        await c.ReadItemAsync<SpikeItem>(id, new PartitionKey(pk));
    }

    var lat = new double[iterations];
    double ruTotal = 0;
    var sw = new Stopwatch();
    for (var i = 0; i < iterations; i++)
    {
        var (id, pk) = ids[rng.Next(ids.Count)];
        sw.Restart();
        var resp = await c.ReadItemAsync<SpikeItem>(id, new PartitionKey(pk));
        sw.Stop();
        lat[i] = sw.Elapsed.TotalMilliseconds;
        ruTotal += resp.RequestCharge;
    }
    return LaneResult.FromLatencies(lane, "point-read (seq)", lat, ruTotal / iterations);
}

static async Task<LaneResult> RunSdkQueryAsync(string lane, Container c, List<(string id, string pk)> ids, int iterations)
{
    var rng = new Random(2);
    var lat = new double[iterations];
    double ruTotal = 0;
    var sw = new Stopwatch();
    for (var i = 0; i < iterations; i++)
    {
        var (_, pk) = ids[rng.Next(ids.Count)];
        var q = new QueryDefinition("SELECT * FROM c WHERE c.pk = @pk").WithParameter("@pk", pk);
        sw.Restart();
        using var it = c.GetItemQueryIterator<SpikeItem>(q, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(pk),
        });
        while (it.HasMoreResults)
        {
            var page = await it.ReadNextAsync();
            ruTotal += page.RequestCharge;
        }
        sw.Stop();
        lat[i] = sw.Elapsed.TotalMilliseconds;
    }
    return LaneResult.FromLatencies(lane, "query 1-partition (seq)", lat, ruTotal / iterations);
}

static async Task<LaneResult> RunSdkThroughputAsync(string lane, Container c, List<(string id, string pk)> ids, int concurrency, int durationSec)
{
    var deadline = Stopwatch.GetTimestamp() + (long)(durationSec * Stopwatch.Frequency);
    long ops = 0;
    var perWorker = new ConcurrentBag<double>();
    var workers = new Task[concurrency];
    var runStart = Stopwatch.GetTimestamp();
    for (var w = 0; w < concurrency; w++)
    {
        var seed = w + 100;
        workers[w] = Task.Run(async () =>
        {
            var rng = new Random(seed);
            var sw = new Stopwatch();
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var (id, pk) = ids[rng.Next(ids.Count)];
                sw.Restart();
                await c.ReadItemAsync<SpikeItem>(id, new PartitionKey(pk));
                sw.Stop();
                perWorker.Add(sw.Elapsed.TotalMilliseconds);
                Interlocked.Increment(ref ops);
            }
        });
    }
    await Task.WhenAll(workers);
    // Divide by ACTUAL elapsed, not the nominal window: the last in-flight op
    // per worker overshoots the deadline, and slower lanes overshoot more, so
    // dividing by durationSec would inflate (and unfairly favour) slow lanes.
    var elapsedSec = Stopwatch.GetElapsedTime(runStart).TotalSeconds;
    var lat = perWorker.ToArray();
    return LaneResult.FromThroughput(lane, $"point-read ({concurrency}x)", lat, ops / elapsedSec);
}

static async Task<LaneResult> RunRawPointReadAsync(string lane, RawRestReader raw, List<(string id, string pk)> ids, int iterations)
{
    var rng = new Random(3);
    for (var i = 0; i < Math.Min(50, ids.Count); i++)
    {
        var (id, pk) = ids[rng.Next(ids.Count)];
        await raw.ReadAsync(id, pk, default);
    }

    var lat = new double[iterations];
    double ruTotal = 0;
    var sw = new Stopwatch();
    for (var i = 0; i < iterations; i++)
    {
        var (id, pk) = ids[rng.Next(ids.Count)];
        sw.Restart();
        var ruCharge = await raw.ReadAsync(id, pk, default);
        sw.Stop();
        lat[i] = sw.Elapsed.TotalMilliseconds;
        ruTotal += ruCharge;
    }
    return LaneResult.FromLatencies(lane, "point-read (seq)", lat, ruTotal / iterations);
}

static async Task<LaneResult> RunRawThroughputAsync(string lane, RawRestReader raw, List<(string id, string pk)> ids, int concurrency, int durationSec)
{
    var deadline = Stopwatch.GetTimestamp() + (long)(durationSec * Stopwatch.Frequency);
    long ops = 0;
    var perWorker = new ConcurrentBag<double>();
    var workers = new Task[concurrency];
    var runStart = Stopwatch.GetTimestamp();
    for (var w = 0; w < concurrency; w++)
    {
        var seed = w + 200;
        workers[w] = Task.Run(async () =>
        {
            var rng = new Random(seed);
            var sw = new Stopwatch();
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var (id, pk) = ids[rng.Next(ids.Count)];
                sw.Restart();
                await raw.ReadAsync(id, pk, default);
                sw.Stop();
                perWorker.Add(sw.Elapsed.TotalMilliseconds);
                Interlocked.Increment(ref ops);
            }
        });
    }
    await Task.WhenAll(workers);
    var elapsedSec = Stopwatch.GetElapsedTime(runStart).TotalSeconds;
    var lat = perWorker.ToArray();
    return LaneResult.FromThroughput(lane, $"point-read ({concurrency}x)", lat, ops / elapsedSec);
}

static void PrintTable(List<LaneResult> results)
{
    Console.WriteLine();
    Console.WriteLine("{0,-12} {1,-26} {2,8} {3,8} {4,8} {5,8} {6,10} {7,8}",
        "lane", "operation", "p50ms", "p95ms", "p99ms", "avgms", "ops/sec", "avgRU");
    Console.WriteLine(new string('-', 96));
    foreach (var r in results)
    {
        Console.WriteLine("{0,-12} {1,-26} {2,8:F2} {3,8:F2} {4,8:F2} {5,8:F2} {6,10} {7,8}",
            r.Lane, r.Operation, r.P50, r.P95, r.P99, r.Avg,
            r.ThroughputPerSec is { } t ? t.ToString("F0") : "-",
            r.AvgRu is { } ru ? ru.ToString("F2") : "-");
    }
}

static void PrintDirectVsGateway(List<LaneResult> results)
{
    Console.WriteLine();
    Console.WriteLine("Direct vs Gateway delta (negative p99 / positive throughput = Direct wins):");
    foreach (var op in results.Select(r => r.Operation).Distinct())
    {
        var d = results.FirstOrDefault(r => r.Lane == "SDK Direct" && r.Operation == op);
        var g = results.FirstOrDefault(r => r.Lane == "SDK Gateway" && r.Operation == op);
        if (d is null || g is null)
        {
            continue;
        }
        var p99Delta = g.P99 > 0 ? (d.P99 - g.P99) / g.P99 * 100 : 0;
        var p50Delta = g.P50 > 0 ? (d.P50 - g.P50) / g.P50 * 100 : 0;
        var tput = (d.ThroughputPerSec is { } dt && g.ThroughputPerSec is { } gt && gt > 0)
            ? $", throughput {(dt - gt) / gt * 100:+0.0;-0.0}%"
            : "";
        Console.WriteLine($"  {op,-26} p50 {p50Delta,+6:F1}%  p99 {p99Delta,+6:F1}%{tput}");
    }
}

// ============================ types ==========================================

// Lowercase members so the default (Newtonsoft) Cosmos serializer emits "id"
// (required by Cosmos) / "pk" / "payload" without attribute plumbing.
internal sealed class SpikeItem
{
    public string id { get; set; } = "";
    public string pk { get; set; } = "";
    public string payload { get; set; } = "";
}

internal sealed record LaneResult(
    string Lane, string Operation, double P50, double P95, double P99, double Avg,
    double? ThroughputPerSec, double? AvgRu)
{
    public static LaneResult FromLatencies(string lane, string op, double[] lat, double avgRu) =>
        new(lane, op, Pct(lat, 50), Pct(lat, 95), Pct(lat, 99), Mean(lat), null, avgRu);

    public static LaneResult FromThroughput(string lane, string op, double[] lat, double perSec) =>
        new(lane, op, Pct(lat, 50), Pct(lat, 95), Pct(lat, 99), Mean(lat), perSec, null);

    private static double Mean(double[] v) => v.Length == 0 ? 0 : v.Average();

    private static double Pct(double[] v, double p)
    {
        if (v.Length == 0)
        {
            return 0;
        }
        var sorted = (double[])v.Clone();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Hand-rolled REST point-reader that mirrors the proxy's production
/// <c>Aws2Azure.Modules.DynamoDb.Internal.CosmosClient</c>: master-key HMAC
/// signing, <c>x-ms-version: 2018-12-31</c>, <c>x-ms-date</c>, and the
/// <c>x-ms-documentdb-partitionkey</c> header. Returns the RU charge.
/// </summary>
internal sealed class RawRestReader(HttpClient http, string db, string coll, string masterKey)
{
    private const string ApiVersion = "2018-12-31";
    private readonly byte[] _keyBytes = Convert.FromBase64String(masterKey);

    public async Task<double> ReadAsync(string id, string pk, CancellationToken ct)
    {
        var resourceLink = $"dbs/{db}/colls/{coll}/docs/{id}";

        for (var attempt = 0; ; attempt++)
        {
            // An HttpRequestMessage can only be sent once, so rebuild (and
            // re-sign with a fresh date) on every retry attempt.
            var date = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
            using var req = new HttpRequestMessage(HttpMethod.Get, resourceLink);
            req.Headers.TryAddWithoutValidation("authorization", BuildAuth("get", "docs", resourceLink, date));
            req.Headers.TryAddWithoutValidation("x-ms-date", date);
            req.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);
            req.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", $"[\"{pk}\"]");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 10)
            {
                var retryMs = resp.Headers.TryGetValues("x-ms-retry-after-ms", out var vals)
                    && double.TryParse(vals.FirstOrDefault(), out var ms) ? ms : 50;
                await Task.Delay(TimeSpan.FromMilliseconds(retryMs), ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            await resp.Content.ReadAsByteArrayAsync(ct);
            return resp.Headers.TryGetValues("x-ms-request-charge", out var ru)
                && double.TryParse(ru.FirstOrDefault(), out var charge) ? charge : 0;
        }
    }

    private string BuildAuth(string verb, string resourceType, string resourceLink, string date)
    {
        var sts = string.Concat(verb, "\n", resourceType, "\n", resourceLink, "\n", date, "\n", "\n");
        var sig = Convert.ToBase64String(HMACSHA256.HashData(_keyBytes, Encoding.UTF8.GetBytes(sts)));
        return Uri.EscapeDataString($"type=master&ver=1.0&sig={sig}");
    }
}
