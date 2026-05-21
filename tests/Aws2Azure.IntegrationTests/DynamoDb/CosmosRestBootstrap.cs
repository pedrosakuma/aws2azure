using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
