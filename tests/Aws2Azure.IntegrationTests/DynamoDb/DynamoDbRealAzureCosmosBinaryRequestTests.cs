using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Aws2Azure.IntegrationTests.Fixtures;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Xunit;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Real-Azure nightly acceptance of the opt-in CosmosBinary <b>request</b> path
/// (#336/#337). The CI Cosmos DB Linux emulator neither emits nor reliably
/// accepts CosmosBinary, so a <c>0x80</c> write body can only be proven
/// gateway-accepted, <b>parsed</b> and <b>indexed</b> against live Azure. Two
/// complementary checks (mirroring the response-side file):
///
/// <list type="number">
///   <item><see cref="RealAzure_proxy_PutItem_binary_body_is_parsed_and_indexed"/>
///   — drives PutItem/GetItem through the proxy (the fixture runs with
///   <c>cosmosBinaryRequests: true</c>), asserts the write went out as binary via
///   the <c>aws2azure_dynamodb_write_body_total{format="binary"}</c> counter, that
///   GetItem round-trips (parsed, not stored opaquely), and that a raw Cosmos
///   query over a non-key attribute returns the document (the gateway indexed
///   fields it could only have obtained by parsing the binary body).</item>
///   <item><see cref="RealAzure_production_binary_body_is_accepted_parsed_and_indexed"/>
///   — the spike §4 probe, productionised: POSTs a document encoded by the
///   <b>production</b> <see cref="InferredAttributeStorage.WriteCosmosDocumentBinary(string,string,JsonElement)"/>
///   raw (no negotiation header / Content-Type), asserts <c>201 Created</c>, then
///   reads it back as text and queries an indexed field — pinning that the exact
///   bytes the proxy emits are parsed + indexed by real Azure.</item>
/// </list>
///
/// Skips when <c>AZURE_COSMOS_ENDPOINT/KEY/DATABASE</c> are absent (the raw
/// read-back / query needs the master key, so it also skips under
/// Workload-Identity-only runs).
/// </summary>
[Trait("Category", "RealAzure")]
[Collection(RealAzureCollection.Name)]
public sealed class DynamoDbRealAzureCosmosBinaryRequestTests
{
    private const string BinaryHeader = "x-ms-cosmos-supported-serialization-formats";
    private const string BinaryValue = "CosmosBinary";

    private readonly RealAzureProxyFixture _fx;

    public DynamoDbRealAzureCosmosBinaryRequestTests(RealAzureProxyFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task RealAzure_proxy_PutItem_binary_body_is_parsed_and_indexed()
    {
        Skip.IfNot(
            _fx.CosmosConfigured && !string.IsNullOrEmpty(_fx.CosmosMasterKey),
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set (master key required for the indexed query) — skipping real-Azure binary write acceptance.");

        var table = "binreq" + Guid.NewGuid().ToString("N")[..12];
        var marker = Guid.NewGuid().ToString("N");
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

            // Representative attribute surface so the gateway must parse numbers,
            // strings, bool/null, binary blob, nested map and list out of the
            // 0x80 body. 's' carries a unique marker we later query on.
            var item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new AttributeValue { S = "item-1" },
                ["s"] = new AttributeValue { S = marker },
                ["uni"] = new AttributeValue { S = "héllo ✓" },
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

            // Snapshot the binary write counter so we can prove THIS PutItem went
            // out as a 0x80 body (the round-trip assertions below are identical
            // for text and binary, so without this the test could pass on text).
            long binaryBefore = await ReadWriteBodyCountAsync("binary").ConfigureAwait(false);

            await client.PutItemAsync(new PutItemRequest { TableName = table, Item = item }).ConfigureAwait(false);

            long binaryAfter = await ReadWriteBodyCountAsync("binary").ConfigureAwait(false);
            Assert.True(
                binaryAfter > binaryBefore,
                $"PutItem did not send a CosmosBinary body: counter format=\"binary\" stayed at {binaryBefore} " +
                "(the fixture enables cosmosBinaryRequests, so the write should be binary).");

            // GetItem through the proxy — the doc must round-trip, proving the
            // gateway stored a real parsed document (not the opaque 0x80 blob).
            var got = await client.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "item-1" } },
                ConsistentRead = true,
            }).ConfigureAwait(false);

            Assert.True(got.IsItemSet);
            Assert.Equal(marker, got.Item["s"].S);
            Assert.Equal("héllo ✓", got.Item["uni"].S);
            Assert.Equal("1234567890123", got.Item["n"].N);
            Assert.Equal("3.14159", got.Item["f"].N);
            Assert.True(got.Item["b"].BOOL);
            Assert.True(got.Item["z"].NULL);
            Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], got.Item["bin"].B.ToArray());
            Assert.Equal("7", got.Item["m"].M["a"].N);
            Assert.Equal("x", got.Item["m"].M["b"].S);
            Assert.Equal(3, got.Item["l"].L.Count);

            // Indexed query straight against Cosmos over the non-key 's' attribute:
            // the gateway can only answer this if it PARSED the binary body into
            // queryable, indexed fields.
            using var http = new HttpClient { BaseAddress = new Uri(_fx.CosmosEndpoint) };
            int hits = await CountByAttributeAsync(http, _fx.CosmosMasterKey, _fx.CosmosDatabase, table, "s", marker)
                .ConfigureAwait(false);
            Assert.True(hits >= 1, $"indexed query c.s = '{marker}' returned {hits} docs — gateway did not parse/index the binary write.");
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

    [SkippableFact]
    public async Task RealAzure_production_binary_body_is_accepted_parsed_and_indexed()
    {
        Skip.IfNot(
            _fx.CosmosConfigured && !string.IsNullOrEmpty(_fx.CosmosMasterKey),
            "AZURE_COSMOS_ENDPOINT/KEY/DATABASE not set (master key required) — skipping real-Azure production-binary probe.");

        using var http = new HttpClient { BaseAddress = new Uri(_fx.CosmosEndpoint) };
        string db = _fx.CosmosDatabase;
        string key = _fx.CosmosMasterKey;
        string coll = "binwr" + Guid.NewGuid().ToString("N")[..11];
        const string docId = "probe-1";
        var marker = Guid.NewGuid().ToString("N");

        await CreateContainerAsync(http, key, db, coll).ConfigureAwait(false);
        try
        {
            // Encode with the PRODUCTION writer so we validate the exact bytes the
            // proxy emits. The DDB attribute map mirrors a real PutItem body.
            byte[] body = ProductionBinaryDocument(docId, docId,
                "{\"s\":{\"S\":\"" + marker + "\"},\"n\":{\"N\":\"42\"},\"b\":{\"BOOL\":true}}");
            Assert.Equal(0x80, body[0]);

            // Raw upsert with NO Content-Type and NO negotiation header — the
            // gateway auto-detects the 0x80 marker (spike §4).
            await UpsertRawAsync(http, key, db, coll, docId, body).ConfigureAwait(false);

            // Read it back WITHOUT the binary header → ordinary text JSON whose
            // fields are present (parsed, not stored opaquely).
            byte[] textBody = await ReadDocumentBytesAsync(http, key, db, coll, docId).ConfigureAwait(false);
            Assert.Equal((byte)'{', textBody[0]);
            using var doc = JsonDocument.Parse(textBody);
            Assert.Equal(marker, doc.RootElement.GetProperty("s").GetString());
            Assert.Equal(42, doc.RootElement.GetProperty("n").GetInt32());
            Assert.True(doc.RootElement.GetProperty("b").GetBoolean());

            // Indexed query over the parsed 's' field.
            int hits = await CountByAttributeAsync(http, key, db, coll, "s", marker).ConfigureAwait(false);
            Assert.True(hits >= 1, $"indexed query c.s = '{marker}' returned {hits} docs — gateway did not parse/index the production binary body.");
        }
        finally
        {
            await DeleteContainerAsync(http, key, db, coll).ConfigureAwait(false);
        }
    }

    private static byte[] ProductionBinaryDocument(string id, string pk, string ddbItemJson)
    {
        using var jd = JsonDocument.Parse(ddbItemJson);
        var bw = new ArrayBufferWriter<byte>();
        InferredAttributeStorage.WriteCosmosDocumentBinary(bw, id, pk, jd.RootElement);
        return bw.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Scrapes the proxy's Prometheus endpoint for the cumulative
    /// <c>aws2azure_dynamodb_write_body_total{format=...}</c> count.
    /// </summary>
    private async Task<long> ReadWriteBodyCountAsync(string format)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = await http.GetStringAsync(_fx.MetricsUrl).ConfigureAwait(false);

        long total = 0;
        using var reader = new System.IO.StringReader(body);
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.Length == 0 || line[0] == '#'
                || !line.StartsWith("aws2azure_dynamodb_write_body_total", StringComparison.Ordinal)
                || line.IndexOf("format=\"" + format + "\"", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            int sp = line.LastIndexOf(' ');
            if (sp >= 0
                && double.TryParse(line.AsSpan(sp + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                total += (long)value;
            }
        }

        return total;
    }

    // ---- raw Cosmos REST helpers (master-key signed) ----

    private static async Task<int> CountByAttributeAsync(
        HttpClient http, string key, string db, string coll, string attr, string value)
    {
        string resourceId = $"dbs/{db}/colls/{coll}";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{resourceId}/docs")
        {
            Content = new StringContent(
                "{\"query\":\"SELECT c.id FROM c WHERE c." + attr + " = @v\"," +
                "\"parameters\":[{\"name\":\"@v\",\"value\":\"" + value + "\"}]}",
                Encoding.UTF8),
        };
        // Cosmos query content-type is application/query+json (no charset).
        req.Content.Headers.Remove("Content-Type");
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/query+json");
        Sign(req, key, "post", "docs", resourceId);
        req.Headers.TryAddWithoutValidation("x-ms-documentdb-isquery", "true");
        req.Headers.TryAddWithoutValidation("x-ms-documentdb-query-enablecrosspartition", "true");

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"query failed: {(int)resp.StatusCode} {resp.StatusCode}\n{respBody}");
        }

        using var doc = JsonDocument.Parse(respBody);
        return doc.RootElement.TryGetProperty("Documents", out var docs) && docs.ValueKind == JsonValueKind.Array
            ? docs.GetArrayLength()
            : 0;
    }

    private static async Task CreateContainerAsync(HttpClient http, string key, string db, string coll)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"dbs/{db}/colls")
        {
            Content = JsonContent(
                "{\"id\":\"" + coll + "\",\"partitionKey\":{\"paths\":[\"/" + InferredAttributeStorage.PkProperty + "\"],\"kind\":\"Hash\"}}"),
        };
        Sign(req, key, "post", "colls", $"dbs/{db}");
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        await EnsureCreatedAsync(resp, "create container").ConfigureAwait(false);
    }

    private static async Task UpsertRawAsync(
        HttpClient http, string key, string db, string coll, string pk, byte[] body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"dbs/{db}/colls/{coll}/docs")
        {
            Content = new ByteArrayContent(body),
        };
        // Deliberately leave Content-Type unset to prove the gateway auto-detects
        // the 0x80 marker (spike §4).
        req.Content.Headers.Remove("Content-Type");
        Sign(req, key, "post", "docs", $"dbs/{db}/colls/{coll}");
        req.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", $"[\"{pk}\"]");
        req.Headers.TryAddWithoutValidation("x-ms-documentdb-is-upsert", "true");
        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        await EnsureCreatedAsync(resp, "binary upsert").ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadDocumentBytesAsync(
        HttpClient http, string key, string db, string coll, string docId)
    {
        string resourceId = $"dbs/{db}/colls/{coll}/docs/{docId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, resourceId);
        Sign(req, key, "get", "docs", resourceId);
        req.Headers.TryAddWithoutValidation("x-ms-documentdb-partitionkey", $"[\"{docId}\"]");
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
