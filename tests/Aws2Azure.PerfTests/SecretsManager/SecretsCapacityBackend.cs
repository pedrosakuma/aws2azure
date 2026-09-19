using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aws2Azure.PerfTests.SecretsManager;

// Control-plane work only. Never called by the measured action.
internal sealed class SecretsCapacityBackend(HttpClient client) : IDisposable
{
    public int Requests { get; private set; }

    public static async Task<SecretsCapacityBackend> ConnectAsync(CancellationToken cancellationToken)
    {
        var vault = new Uri(Required("AZURE_KEYVAULT_URL"));
        if (vault.Scheme != "https" || !vault.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Capacity execution requires a real public-Azure Key Vault HTTPS endpoint, not an emulator.");
        using var tokenClient = new HttpClient { Timeout = SecretsCapacityPlan.RequestTimeout };
        using var response = await tokenClient.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(Required("AZURE_TENANT_ID"))}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Required("AZURE_CLIENT_ID"),
                ["scope"] = "https://vault.azure.net/.default",
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = (await File.ReadAllTextAsync(Required("AZURE_FEDERATED_TOKEN_FILE"), cancellationToken)).Trim(),
            }), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var http = new HttpClient { BaseAddress = vault, Timeout = SecretsCapacityPlan.RequestTimeout };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", document.RootElement.GetProperty("access_token").GetString());
        return new SecretsCapacityBackend(http);
    }

    public async Task AssertEmptyAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "secrets", "deletedsecrets" })
        {
            using var response = await SendAsync(HttpMethod.Get, path, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.GetProperty("value").GetArrayLength() != 0
                || document.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(next.GetString()))
                throw new InvalidOperationException("Capacity runs require an exclusively leased, empty vault (including deleted secrets). Unrelated inventory will not be deleted.");
        }
    }

    public async Task CleanupAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        foreach (var name in names)
        {
            try
            {
                await CleanupOneAsync(name, cancellationToken);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("Owned-secret cleanup incomplete; retain the resource lease and inspect the cleanup manifest.", failures);
        await AssertEmptyAsync(cancellationToken);
    }

    private async Task CleanupOneAsync(string name, CancellationToken cancellationToken)
    {
        using (var deleted = await SendAsync(HttpMethod.Delete, $"secrets/{name}", cancellationToken))
            if (deleted.StatusCode != HttpStatusCode.NotFound) deleted.EnsureSuccessStatusCode();

        // The process has stopped before this control pass. Retrying purge here
        // cannot race its detached purge task, and is outside every measurement.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var purge = await SendAsync(HttpMethod.Delete, $"deletedsecrets/{name}", cancellationToken);
            if (purge.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Conflict))
                purge.EnsureSuccessStatusCode();
            using var active = await SendAsync(HttpMethod.Get, $"secrets/{name}", cancellationToken);
            using var softDeleted = await SendAsync(HttpMethod.Get, $"deletedsecrets/{name}", cancellationToken);
            if (active.StatusCode == HttpStatusCode.NotFound && softDeleted.StatusCode == HttpStatusCode.NotFound)
                return;
            if (active.StatusCode != HttpStatusCode.NotFound) active.EnsureSuccessStatusCode();
            if (softDeleted.StatusCode != HttpStatusCode.NotFound) softDeleted.EnsureSuccessStatusCode();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException("Owned secret did not converge to absent active and deleted endpoints.");
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        Requests++;
        return SendCoreAsync(method, path, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{path}?api-version=7.6");
        return await client.SendAsync(request, cancellationToken);
    }

    public static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : throw new InvalidOperationException($"{name} is required for an explicitly enabled capacity run.");

    public void Dispose() => client.Dispose();
}
