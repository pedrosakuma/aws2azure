using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Cosmos;

// =============================================================================
// Cosmos transport spike (issue #265)
//
// Measures the latency / throughput delta between three point-read transports:
//   1. SDK  Direct  (ConnectionMode.Direct  -> TCP/rntbd to the replica)
//   2. SDK  Gateway (ConnectionMode.Gateway -> HTTPS REST to the gateway)
//   3. Raw  REST    (HttpClient + master-key HMAC, mirroring the proxy's
//                    hand-rolled Aws2Azure.Modules.DynamoDb CosmosClient)
//
// Methodology (so we measure the *transport method*, not incidental client
// defaults that happen to ship with each lane):
//   * Connection parametrization is EQUALIZED across the HTTP lanes. The SDK
//     gateway lane's GatewayModeMaxConnectionLimit (default 50!) and the raw
//     lane's MaxConnectionsPerServer are both pinned to SPIKE_CONN_LIMIT
//     (default max(64, concurrency)) so neither HTTP lane is artificially
//     capped below the offered concurrency. Set SPIKE_CONN_LIMIT lower to
//     deliberately study the connection-cap effect.
//   * Latency is measured INTERLEAVED: every iteration hits all enabled lanes
//     against the same document id in randomized lane order, so all lanes see
//     the same network window sample-by-sample. This removes the cross-lane
//     jitter that made the old run-one-lane-then-the-next metric noisy.
//   * Each measurement runs SPIKE_REPS times; the table reports the aggregate
//     percentiles plus the stddev of the per-rep p50 (latency) / per-rep ops/s
//     (throughput) so the noise floor is explicit.
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
var warmup = EnvInt("SPIKE_WARMUP", 100);
var reps = Math.Max(1, EnvInt("SPIKE_REPS", 3));
var tputReps = Math.Max(1, EnvInt("SPIKE_TPUT_REPS", 1));
var connLimit = EnvInt("SPIKE_CONN_LIMIT", Math.Max(64, concurrency));
var rawHttpVersion = Env("SPIKE_RAW_HTTP_VERSION", "1.1");
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
      latency run     : {iterations} interleaved point reads x {reps} rep(s) / lane (warmup {warmup})
      throughput run  : {concurrency} workers x {durationSec}s x {tputReps} rep(s) / lane
      conn limit      : {connLimit} (gateway GatewayModeMaxConnectionLimit + raw MaxConnectionsPerServer)
      raw http version: {rawHttpVersion}
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

// --- Build lane registries ---------------------------------------------------
var disposables = new List<IDisposable>();
var pointReadLanes = new List<(string Name, ReadDelegate Read)>();
var queryLanes = new List<(string Name, ReadDelegate Read)>();
var throughputLanes = new List<(string Name, ReadDelegate Read)>();

if (lanes.Contains("direct"))
{
    var client = MakeSdkClient(endpoint!, key!, ConnectionMode.Direct, preferredRegion, connLimit);
    disposables.Add(client);
    var c = client.GetContainer(dbName, containerName);
    pointReadLanes.Add(("SDK Direct", SdkRead(c)));
    queryLanes.Add(("SDK Direct", SdkQuery(c)));
    throughputLanes.Add(("SDK Direct", SdkRead(c)));
}

if (lanes.Contains("gateway"))
{
    var client = MakeSdkClient(endpoint!, key!, ConnectionMode.Gateway, preferredRegion, connLimit);
    disposables.Add(client);
    var c = client.GetContainer(dbName, containerName);
    pointReadLanes.Add(("SDK Gateway", SdkRead(c)));
    queryLanes.Add(("SDK Gateway", SdkQuery(c)));
    throughputLanes.Add(("SDK Gateway", SdkRead(c)));
}

if (lanes.Contains("raw"))
{
    // SAME SocketsHttpHandler config as the proxy's AzureHttpClient.BuildDefaultHandler,
    // with MaxConnectionsPerServer pinned to the shared SPIKE_CONN_LIMIT so the raw and
    // SDK-gateway HTTP lanes are compared at identical connection parametrization.
    var rawHandler = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        MaxConnectionsPerServer = connLimit,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        EnableMultipleHttp2Connections = true,
    };
    var rawHttp = new HttpClient(rawHandler) { BaseAddress = new Uri(endpoint!.TrimEnd('/') + "/") };
    disposables.Add(rawHttp);
    disposables.Add(rawHandler);
    var reader = new RawRestReader(rawHttp, dbName, containerName, key!, Version.Parse(rawHttpVersion));
    ReadDelegate rawRead = (id, pk, ct) => reader.ReadAsync(id, pk, ct);
    pointReadLanes.Add(("Raw REST", rawRead));
    throughputLanes.Add(("Raw REST", rawRead));
}

// --- Measure -----------------------------------------------------------------
var results = new List<LaneResult>();
try
{
    if (pointReadLanes.Count > 0)
    {
        results.AddRange(await RunInterleavedLatencyAsync(
            "point-read (seq)", pointReadLanes, ids, iterations, warmup, reps));
    }
    if (queryLanes.Count > 0)
    {
        results.AddRange(await RunInterleavedLatencyAsync(
            "query 1-partition (seq)", queryLanes, ids, Math.Min(iterations, 500), warmup, reps));
    }
    foreach (var lane in throughputLanes)
    {
        results.Add(await RunThroughputAsync(lane.Name, lane.Read, ids, concurrency, durationSec, tputReps, warmup));
    }
}
finally
{
    foreach (var d in disposables)
    {
        d.Dispose();
    }
}

// --- Report ------------------------------------------------------------------
PrintTable(results);
PrintDeltasVsGateway(results);
return 0;

// ============================ helpers ========================================

static CosmosClient MakeSdkClient(string endpoint, string key, ConnectionMode mode, string? region, int gatewayConnLimit)
{
    var opts = new CosmosClientOptions { ConnectionMode = mode };
    if (mode == ConnectionMode.Gateway)
    {
        // Default is 50; raise it so the gateway lane is not capped below the
        // offered concurrency. This is the knob that otherwise makes "gateway"
        // look slow and conflates client config with the transport method.
        opts.GatewayModeMaxConnectionLimit = gatewayConnLimit;
    }
    if (!string.IsNullOrWhiteSpace(region))
    {
        opts.ApplicationPreferredRegions = new List<string> { region };
    }
    return new CosmosClient(endpoint, key, opts);
}

static ReadDelegate SdkRead(Container c) => async (id, pk, ct) =>
{
    var resp = await c.ReadItemAsync<SpikeItem>(id, new PartitionKey(pk), cancellationToken: ct);
    return resp.RequestCharge;
};

static ReadDelegate SdkQuery(Container c) => async (id, pk, ct) =>
{
    var q = new QueryDefinition("SELECT * FROM c WHERE c.pk = @pk").WithParameter("@pk", pk);
    double ruTotal = 0;
    using var it = c.GetItemQueryIterator<SpikeItem>(q, requestOptions: new QueryRequestOptions
    {
        PartitionKey = new PartitionKey(pk),
    });
    while (it.HasMoreResults)
    {
        var page = await it.ReadNextAsync(ct);
        ruTotal += page.RequestCharge;
    }
    return ruTotal;
};

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

static void Shuffle(int[] a, Random r)
{
    for (var i = a.Length - 1; i > 0; i--)
    {
        var j = r.Next(i + 1);
        (a[i], a[j]) = (a[j], a[i]);
    }
}

static double StdDev(IReadOnlyList<double> v)
{
    if (v.Count < 2)
    {
        return 0;
    }
    var mean = v.Average();
    var sumSq = 0.0;
    foreach (var x in v)
    {
        var d = x - mean;
        sumSq += d * d;
    }
    return Math.Sqrt(sumSq / (v.Count - 1));
}

static async Task<List<LaneResult>> RunInterleavedLatencyAsync(
    string operation,
    List<(string Name, ReadDelegate Read)> lanes,
    List<(string id, string pk)> ids,
    int iterations, int warmup, int reps)
{
    var agg = lanes.ToDictionary(l => l.Name, _ => new List<double>(reps * iterations));
    var ruSum = lanes.ToDictionary(l => l.Name, _ => 0.0);
    var ruCount = lanes.ToDictionary(l => l.Name, _ => 0L);
    var p50PerRep = lanes.ToDictionary(l => l.Name, _ => new List<double>(reps));

    for (var rep = 0; rep < reps; rep++)
    {
        var wrng = new Random(500 + rep);
        foreach (var lane in lanes)
        {
            for (var w = 0; w < warmup; w++)
            {
                var (id, pk) = ids[wrng.Next(ids.Count)];
                await lane.Read(id, pk, default);
            }
        }

        var repLat = lanes.ToDictionary(l => l.Name, _ => new double[iterations]);
        var rng = new Random(1000 + rep);
        var order = Enumerable.Range(0, lanes.Count).ToArray();
        var sw = new Stopwatch();
        for (var i = 0; i < iterations; i++)
        {
            var (id, pk) = ids[rng.Next(ids.Count)];
            Shuffle(order, rng);
            foreach (var idx in order)
            {
                var lane = lanes[idx];
                sw.Restart();
                var charge = await lane.Read(id, pk, default);
                sw.Stop();
                repLat[lane.Name][i] = sw.Elapsed.TotalMilliseconds;
                ruSum[lane.Name] += charge;
                ruCount[lane.Name]++;
            }
        }

        foreach (var lane in lanes)
        {
            agg[lane.Name].AddRange(repLat[lane.Name]);
            p50PerRep[lane.Name].Add(LaneResult.Percentile(repLat[lane.Name], 50));
        }
    }

    var results = new List<LaneResult>();
    foreach (var lane in lanes)
    {
        var arr = agg[lane.Name].ToArray();
        var avgRu = ruCount[lane.Name] > 0 ? ruSum[lane.Name] / ruCount[lane.Name] : 0;
        results.Add(LaneResult.FromLatencies(lane.Name, operation, arr, avgRu, reps, StdDev(p50PerRep[lane.Name])));
    }
    return results;
}

static async Task<LaneResult> RunThroughputAsync(
    string lane, ReadDelegate read, List<(string id, string pk)> ids,
    int concurrency, int durationSec, int reps, int warmup)
{
    // brief warmup (open connections / JIT) before the first measured rep
    {
        var wrng = new Random(700);
        for (var w = 0; w < warmup; w++)
        {
            var (id, pk) = ids[wrng.Next(ids.Count)];
            await read(id, pk, default);
        }
    }

    var perSecReps = new List<double>(reps);
    double[] lastLat = Array.Empty<double>();
    for (var rep = 0; rep < reps; rep++)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(durationSec * Stopwatch.Frequency);
        long ops = 0;
        var perWorker = new ConcurrentBag<double>();
        var workers = new Task[concurrency];
        var runStart = Stopwatch.GetTimestamp();
        for (var w = 0; w < concurrency; w++)
        {
            var seed = w + 100 + rep * 1000;
            workers[w] = Task.Run(async () =>
            {
                var rng = new Random(seed);
                var sw = new Stopwatch();
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    var (id, pk) = ids[rng.Next(ids.Count)];
                    sw.Restart();
                    await read(id, pk, default);
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
        perSecReps.Add(ops / elapsedSec);
        lastLat = perWorker.ToArray();
    }

    return LaneResult.FromThroughput(
        lane, $"point-read ({concurrency}x)", lastLat, perSecReps.Average(), reps, StdDev(perSecReps));
}

static void PrintTable(List<LaneResult> results)
{
    Console.WriteLine();
    Console.WriteLine("{0,-12} {1,-26} {2,8} {3,8} {4,8} {5,8} {6,12} {7,8} {8,5} {9,11}",
        "lane", "operation", "p50ms", "p95ms", "p99ms", "avgms", "ops/sec", "avgRU", "reps", "stddev");
    Console.WriteLine(new string('-', 118));
    foreach (var r in results)
    {
        var stddev = r.ThroughputPerSec is not null
            ? (r.ThroughputStdDev is { } ts ? ts.ToString("F0") + " op/s" : "-")
            : (r.P50StdDev is { } ps ? ps.ToString("F2") + " ms" : "-");
        Console.WriteLine("{0,-12} {1,-26} {2,8:F2} {3,8:F2} {4,8:F2} {5,8:F2} {6,12} {7,8} {8,5} {9,11}",
            r.Lane, r.Operation, r.P50, r.P95, r.P99, r.Avg,
            r.ThroughputPerSec is { } t ? t.ToString("F0") : "-",
            r.AvgRu is { } ru ? ru.ToString("F2") : "-",
            r.Reps, stddev);
    }
}

static void PrintDeltasVsGateway(List<LaneResult> results)
{
    Console.WriteLine();
    Console.WriteLine("Deltas vs SDK Gateway baseline (negative p-latency / positive throughput = faster than gateway):");
    foreach (var op in results.Select(r => r.Operation).Distinct())
    {
        var g = results.FirstOrDefault(r => r.Lane == "SDK Gateway" && r.Operation == op);
        if (g is null)
        {
            continue;
        }
        foreach (var other in results.Where(r => r.Operation == op && r.Lane != "SDK Gateway"))
        {
            var p50Delta = g.P50 > 0 ? (other.P50 - g.P50) / g.P50 * 100 : 0;
            var p99Delta = g.P99 > 0 ? (other.P99 - g.P99) / g.P99 * 100 : 0;
            var tput = (other.ThroughputPerSec is { } ot && g.ThroughputPerSec is { } gt && gt > 0)
                ? $", throughput {(ot - gt) / gt * 100:+0.0;-0.0}%"
                : "";
            Console.WriteLine($"  {other.Lane,-12} {op,-26} p50 {p50Delta,+6:F1}%  p99 {p99Delta,+6:F1}%{tput}");
        }
    }
}

// ============================ types ==========================================

internal delegate Task<double> ReadDelegate(string id, string pk, CancellationToken ct);

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
    double? ThroughputPerSec, double? AvgRu, int Reps, double? P50StdDev, double? ThroughputStdDev)
{
    public static LaneResult FromLatencies(string lane, string op, double[] lat, double avgRu, int reps, double p50StdDev) =>
        new(lane, op, Percentile(lat, 50), Percentile(lat, 95), Percentile(lat, 99), Mean(lat), null, avgRu, reps, p50StdDev, null);

    public static LaneResult FromThroughput(string lane, string op, double[] lat, double perSec, int reps, double tputStdDev) =>
        new(lane, op, Percentile(lat, 50), Percentile(lat, 95), Percentile(lat, 99), Mean(lat), perSec, null, reps, null, tputStdDev);

    private static double Mean(double[] v) => v.Length == 0 ? 0 : v.Average();

    public static double Percentile(double[] v, double p)
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
/// <c>x-ms-documentdb-partitionkey</c> header. The HTTP version is configurable
/// (production uses the framework default 1.1) so the negotiated protocol can be
/// compared against the SDK gateway lane. Returns the RU charge.
/// </summary>
internal sealed class RawRestReader(HttpClient http, string db, string coll, string masterKey, Version httpVersion)
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
            using var req = new HttpRequestMessage(HttpMethod.Get, resourceLink)
            {
                Version = httpVersion,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
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
