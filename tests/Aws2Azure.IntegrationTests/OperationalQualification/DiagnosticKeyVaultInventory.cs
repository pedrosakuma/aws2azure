using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed class DiagnosticKeyVaultInventory(
    HttpClient http, Uri vault, Func<CancellationToken, ValueTask<string>> accessToken)
{
    internal async Task<VaultInventory> ReadAsync(CancellationToken token) => new(
        await CountAsync("secrets", token).ConfigureAwait(false),
        await CountAsync("deletedsecrets", token).ConfigureAwait(false));

    private async Task<long> CountAsync(string collection, CancellationToken token)
    {
        var next = new Uri(vault, $"{collection}?api-version=7.6&maxresults=25");
        long count = 0;
        var visited = new HashSet<Uri>();
        while (true)
        {
            if (next.Scheme != Uri.UriSchemeHttps || next.Authority != vault.Authority
                || next.AbsolutePath != "/" + collection || next.UserInfo.Length != 0
                || next.Fragment.Length != 0 || !visited.Add(next) || visited.Count > 1000)
                throw new InvalidDataException("Invalid or unbounded Key Vault inventory pagination.");
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", await accessToken(token).ConfigureAwait(false));
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("Key Vault inventory request failed.", null, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = document.RootElement;
            if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid Key Vault inventory page.");
            count = checked(count + values.GetArrayLength());
            if (!root.TryGetProperty("nextLink", out var link) || link.ValueKind == JsonValueKind.Null)
                return count;
            if (link.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(link.GetString(), UriKind.Absolute, out next))
                throw new InvalidDataException("Invalid Key Vault inventory continuation.");
        }
    }

    internal async Task<double[]> ProbeAsync(CancellationToken token)
    {
        var samples = new double[12];
        for (var i = 0; i < samples.Length; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var started = Stopwatch.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(vault, "secrets?api-version=7.6"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
                throw new HttpRequestException("Anonymous Key Vault probe did not return 401.", null, response.StatusCode);
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return samples;
    }
}
