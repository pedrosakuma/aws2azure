using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Azure nightly validation of the opt-in CosmosBinary response path
/// (#268/#321). The CI Cosmos DB Linux emulator does NOT emit CosmosBinary
/// bodies, so the fused <c>CosmosBinaryReader</c> GetItem path can only be
/// exercised end-to-end against live Azure Cosmos DB. Two checks:
///
/// <list type="number">
///   <item><see cref="RealAzure_point_read_emits_CosmosBinary_body"/> — a raw
///   authenticated point read with
///   <c>x-ms-cosmos-supported-serialization-formats: CosmosBinary</c> returns a
///   body whose first byte is <c>0x80</c> (the CosmosBinary format byte), while
///   the same read without the header returns text (<c>{</c>). This pins the
///   exact behaviour the emulator lacks.</item>
///   <item><see cref="Fused_GetItem_round_trips_against_real_cosmos_with_binary_enabled"/>
///   — drives PutItem/GetItem through the proxy (which the fixture runs with
///   <c>cosmosBinaryResponses: true</c>) across representative attribute types,
///   asserting the fused decode is byte-correct against real binary bodies.</item>
/// </list>
///
/// Skips when <c>AZURE_COSMOS_ENDPOINT/KEY/DATABASE</c> are absent (the raw
/// probe needs the master key, so it also skips under Workload-Identity-only
/// runs).
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureCosmosBinaryTests
{
    private const string BinaryHeader = "x-ms-cosmos-supported-serialization-formats";
    private const string BinaryValue = "CosmosBinary";

    private readonly RealAzureProxyFixture _fx;

    public DynamoDbRealAzureCosmosBinaryTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task RealAzure_point_read_emits_CosmosBinary_body()
    {
        Skip.IfNot(
            _fx.CosmosConfigured && !string.IsNullOrEmpty(_fx.CosmosMasterKey),
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set (master key required) — skipping real-Azure CosmosBinary probe.");

        using var http = new HttpClient { BaseAddress = new Uri(_fx.CosmosEndpoint) };
        string db = _fx.CosmosDatabase;
        string key = _fx.CosmosMasterKey;
        string coll = "binprobe" + Guid.NewGuid().ToString("N")[..10];
        const string docId = "probe-1";

        await CreateContainerAsync(http, key, db, coll).ConfigureAwait(false);
        try
        {
            await CreateDocumentAsync(http, key, db, coll, docId).ConfigureAwait(false);

            // With the CosmosBinary header → body starts with the 0x80 format byte.
            byte[] binaryBody = await ReadDocumentBytesAsync(http, key, db, coll, docId, requestBinary: true)
                .ConfigureAwait(false);
            Assert.NotEmpty(binaryBody);
            Assert.True(
                binaryBody[0] == 0x80,
                $"expected CosmosBinary body (first byte 0x80) from real Azure, got 0x{binaryBody[0]:X2} " +
                $"('{(char)binaryBody[0]}'). Real Azure should honor the {BinaryHeader} header.");

            // Without the header → ordinary text JSON (first byte '{').
            byte[] textBody = await ReadDocumentBytesAsync(http, key, db, coll, docId, requestBinary: false)
                .ConfigureAwait(false);
            Assert.NotEmpty(textBody);
            Assert.True(
                textBody[0] == (byte)'{',
                $"expected text JSON body (first byte '{{') without the binary header, got 0x{textBody[0]:X2}.");
        }
        finally
        {
            await DeleteContainerAsync(http, key, db, coll).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task Fused_GetItem_round_trips_against_real_cosmos_with_binary_enabled()
    {
        Skip.IfNot(_fx.CosmosConfigured,
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set — skipping real-Azure fused GetItem.");

        var table = "fus" + Guid.NewGuid().ToString("N")[..12];
        using var client = _fx.CreateDynamoDbClient();
        var tableCreated = false;

        try
        {
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table,
                AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            }).ConfigureAwait(false);
            tableCreated = true;
            await WaitForTableActiveAsync(client, table).ConfigureAwait(false);

            // Representative attribute surface so the fused decode walks numbers,
            // strings, bool/null, binary blob, nested map and list markers.
            var item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new AttributeValue { S = "item-1" },
                ["s"] = new AttributeValue { S = "héllo ✓" },
                ["n"] = new AttributeValue { N = "1234567890123" },
                ["f"] = new AttributeValue { N = "3.14159" },
                ["b"] = new AttributeValue { BOOL = true },
                ["z"] = new AttributeValue { NULL = true },
                ["bin"] = new AttributeValue { B = new System.IO.MemoryStream([0xDE, 0xAD, 0xBE, 0xEF]) },
                ["m"] = new AttributeValue
                {
                    M = new Dictionary<string, AttributeValue>
                    {
                        ["a"] = new AttributeValue { N = "7" },
                        ["b"] = new AttributeValue { S = "x" },
                    },
                },
                ["l"] = new AttributeValue
                {
                    L =
                    [
                        new AttributeValue { N = "1" },
                        new AttributeValue { S = "two" },
                        new AttributeValue { NULL = true },
                    ],
                },
            };

            await client.PutItemAsync(new PutItemRequest { TableName = table, Item = item }).ConfigureAwait(false);

            // Snapshot the production decode-path counter before the GetItem so we
            // can prove THIS request took the fused CosmosBinary path (not text /
            // fallback). The round-trip value assertions below are identical for
            // every path, so without this the test could pass on the text path.
            long fusedBefore = await ReadDecodePathCountAsync("fused").ConfigureAwait(false);

            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            Assert.True(got.IsItemSet);
            Assert.Equal("héllo ✓", got.Item["s"].S);
            Assert.Equal("1234567890123", got.Item["n"].N);
            Assert.Equal("3.14159", got.Item["f"].N);
            Assert.True(got.Item["b"].BOOL);
            Assert.True(got.Item["z"].NULL);
            Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], got.Item["bin"].B.ToArray());
            Assert.Equal("7", got.Item["m"].M["a"].N);
            Assert.Equal("x", got.Item["m"].M["b"].S);
            Assert.Equal(3, got.Item["l"].L.Count);
            Assert.Equal("1", got.Item["l"].L[0].N);
            Assert.Equal("two", got.Item["l"].L[1].S);
            Assert.True(got.Item["l"].L[2].NULL);

            long fusedAfter = await ReadDecodePathCountAsync("fused").ConfigureAwait(false);
            Assert.True(
                fusedAfter > fusedBefore,
                $"GetItem did not take the fused CosmosBinary path: " +
                $"decode-path counter path=\"fused\" stayed at {fusedBefore} " +
                $"(real Azure should emit CosmosBinary with the fast path enabled).");
        }
        finally
        {
            if (tableCreated)
            {
                try { await client.DeleteTableAsync(table).ConfigureAwait(false); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    // ---- raw Cosmos REST helpers (master-key signed) ----

    /// <summary>
    /// Scrapes the proxy's Prometheus endpoint and returns the cumulative
    /// <c>aws2azure_dynamodb_getitem_decode_path_total</c> count for the given
    /// <c>path</c> label (0 if the series is absent yet). Used to assert the
    /// fused CosmosBinary path actually ran for a GetItem.
    /// </summary>
    private async Task<long> ReadDecodePathCountAsync(string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = await http.GetStringAsync(_fx.MetricsUrl).ConfigureAwait(false);

        long total = 0;
        bool found = false;
        using var reader = new System.IO.StringReader(body);
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // Match: aws2azure_dynamodb_getitem_decode_path_total{path="fused",...} <value>
            if (!line.StartsWith("aws2azure_dynamodb_getitem_decode_path_total", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.IndexOf("path=\"" + path + "\"", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            int sp = line.LastIndexOf(' ');
            if (sp >= 0
                && double.TryParse(
                    line.AsSpan(sp + 1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value))
            {
                total += (long)value;
                found = true;
            }
        }

        return found ? total : 0;
    }

    private static async Task CreateContainerAsync(HttpClient http, string key, string db, string coll)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"dbs/{db}/colls")
        {
            Content = JsonContent("{\"id\":\"" + coll + "\",\"partitionKey\":{\"paths\":[\"/pk\"],\"kind\":\"Hash\"}}"),
        };
        Sign(req, key, "post", "colls", $"dbs/{db}");
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        await EnsureCreatedAsync(resp, "create container").ConfigureAwait(false);
    }

    private static async Task CreateDocumentAsync(HttpClient http, string key, string db, string coll, string docId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"dbs/{db}/colls/{coll}/docs")
        {
            Content = JsonContent("{\"id\":\"" + docId + "\",\"pk\":\"" + docId + "\",\"v\":42,\"s\":\"probe\"}"),
        };
        Sign(req, key, "post", "docs", $"dbs/{db}/colls/{coll}");
        req.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", $"[\"{docId}\"]");
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        await EnsureCreatedAsync(resp, "create document").ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadDocumentBytesAsync(
        HttpClient http, string key, string db, string coll, string docId, bool requestBinary)
    {
        string resourceId = $"dbs/{db}/colls/{coll}/docs/{docId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, resourceId);
        Sign(req, key, "get", "docs", resourceId);
        req.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", $"[\"{docId}\"]");
        if (requestBinary)
        {
            req.Headers.TryAddWithoutValidation(BinaryHeader, BinaryValue);
        }

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"point read failed: {(int)resp.StatusCode} {resp.StatusCode}\n{body}");
        }
        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static async Task DeleteContainerAsync(HttpClient http, string key, string db, string coll)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"dbs/{db}/colls/{coll}");
            Sign(req, key, "delete", "colls", $"dbs/{db}/colls/{coll}");
            using var _ = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task EnsureCreatedAsync(HttpResponseMessage resp, string what)
    {
        if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            return;
        }
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new InvalidOperationException($"{what} failed: {(int)resp.StatusCode} {resp.StatusCode}\n{body}");
    }

    // Cosmos master-key auth: HMAC-SHA256 over
    // "verb\nresourceType\nresourceId\nx-ms-date\n\n" (verb/resourceType lower).
    private static void Sign(HttpRequestMessage req, string masterKey,
        string verbLower, string resourceType, string resourceId)
    {
        string date = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        string payload = $"{verbLower}\n{resourceType}\n{resourceId}\n{date}\n\n";
        byte[] key = Convert.FromBase64String(masterKey);
        string sig = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        string auth = Uri.EscapeDataString($"type=master&ver=1.0&sig={sig}");

        req.Headers.TryAddWithoutValidation("authorization", auth);
        req.Headers.TryAddWithoutValidation("x-ms-date", date);
        req.Headers.TryAddWithoutValidation("x-ms-version", "2018-12-31");
    }

    private static async Task WaitForTableActiveAsync(AmazonDynamoDBClient client, string table)
    {
        for (var i = 0; i < 60; i++)
        {
            try
            {
                var d = await client.DescribeTableAsync(table).ConfigureAwait(false);
                if (d.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }
            }
            catch (ResourceNotFoundException)
            {
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"table {table} did not become ACTIVE in time.");
    }
}
