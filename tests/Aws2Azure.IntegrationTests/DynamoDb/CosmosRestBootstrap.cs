using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Aws2Azure.IntegrationTests.DynamoDb;

/// <summary>
/// Minimal Cosmos REST helper used by the DynamoDB integration fixture to
/// create the per-test logical database before the proxy starts answering
/// CreateTable. Hand-signs the master-key Authorization header — we do not
/// import the proxy's internal CosmosMasterKeyAuth because that type is
/// (correctly) module-internal.
/// </summary>
internal static class CosmosRestBootstrap
{
    public static async Task EnsureDatabaseAsync(
        HttpClient http, string endpoint, string masterKey, string databaseName)
    {
        var baseUri = new Uri(endpoint);
        using (var head = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"dbs/{databaseName}")))
        {
            SignMaster(head, masterKey, "get", "dbs", $"dbs/{databaseName}");
            using var resp = await http.SendAsync(head);
            if (resp.IsSuccessStatusCode) return;
            if ((int)resp.StatusCode != 404)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Cosmos GET /dbs/{databaseName} returned {(int)resp.StatusCode}: {body}");
            }
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "dbs"))
        {
            Content = new StringContent($"{{\"id\":\"{databaseName}\"}}",
                Encoding.UTF8, "application/json"),
        };
        SignMaster(create, masterKey, "post", "dbs", string.Empty);
        using var createResp = await http.SendAsync(create);
        if (!createResp.IsSuccessStatusCode)
        {
            var body = await createResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Cosmos POST /dbs returned {(int)createResp.StatusCode}: {body}");
        }
    }

    public static async Task CreateContainerAsync(
        HttpClient http,
        string endpoint,
        string masterKey,
        string databaseName,
        string containerName,
        string partitionKeyPath)
    {
        var resourceId = $"dbs/{databaseName}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(endpoint), $"{resourceId}/colls"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    id = containerName,
                    partitionKey = new
                    {
                        paths = new[] { partitionKeyPath },
                        kind = "Hash",
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };
        SignMaster(request, masterKey, "post", "colls", resourceId);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Cosmos create container returned {(int)response.StatusCode}: {body}");
        }
    }

    public static async Task CreateDocumentAsync(
        HttpClient http,
        string endpoint,
        string masterKey,
        string databaseName,
        string containerName,
        string partitionKey,
        string document)
    {
        var resourceId = $"dbs/{databaseName}/colls/{containerName}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(endpoint), $"{resourceId}/docs"))
        {
            Content = new StringContent(document, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            "x-ms-documentdb-partitionkey",
            JsonSerializer.Serialize(new[] { partitionKey }));
        SignMaster(request, masterKey, "post", "docs", resourceId);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Cosmos create document returned {(int)response.StatusCode}: {body}");
        }
    }

    public static async Task<string> ReadContainerAsync(
        HttpClient http,
        string endpoint,
        string masterKey,
        string databaseName,
        string containerName)
    {
        var resourceId = $"dbs/{databaseName}/colls/{containerName}";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(endpoint), resourceId));
        SignMaster(request, masterKey, "get", "colls", resourceId);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Cosmos read container returned {(int)response.StatusCode}: {body}");
        }
        return body;
    }

    public static async Task<string> ReadDocumentAsync(
        HttpClient http,
        string endpoint,
        string masterKey,
        string databaseName,
        string containerName,
        string documentId,
        string partitionKey)
    {
        var resourceId =
            $"dbs/{databaseName}/colls/{containerName}/docs/{documentId}";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(endpoint), resourceId));
        request.Headers.TryAddWithoutValidation(
            "x-ms-documentdb-partitionkey",
            JsonSerializer.Serialize(new[] { partitionKey }));
        SignMaster(request, masterKey, "get", "docs", resourceId);
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Cosmos read document returned {(int)response.StatusCode}: {body}");
        }
        return body;
    }

    public static async Task DeleteContainerAsync(
        HttpClient http,
        string endpoint,
        string masterKey,
        string databaseName,
        string containerName)
    {
        var resourceId = $"dbs/{databaseName}/colls/{containerName}";
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(new Uri(endpoint), resourceId));
        SignMaster(request, masterKey, "delete", "colls", resourceId);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Cosmos delete container returned {(int)response.StatusCode}: {body}");
        }
    }

    private static void SignMaster(HttpRequestMessage req, string masterKey,
        string verbLower, string resourceType, string resourceId)
    {
        var date = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        // Spec: verb + "\n" + resourceType + "\n" + resourceId + "\n" + date + "\n" + "" + "\n"
        var payload = $"{verbLower}\n{resourceType}\n{resourceId}\n{date}\n\n";
        var key = Convert.FromBase64String(masterKey);
        var sig = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        var auth = Uri.EscapeDataString($"type=master&ver=1.0&sig={sig}");

        req.Headers.TryAddWithoutValidation("authorization", auth);
        req.Headers.TryAddWithoutValidation("x-ms-date", date);
        req.Headers.TryAddWithoutValidation("x-ms-version", "2018-12-31");
    }
}
