using System.Net;
using System.Xml;
using System.Xml.Linq;
using Aws2Azure.Core.Azure;

namespace Aws2Azure.IntegrationTests.OperationalQualification;

internal sealed class DiagnosticBlobInventory
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly SharedKeyAuthenticator _auth;

    internal DiagnosticBlobInventory(HttpClient http, Uri endpoint, string account, string key)
    {
        if (endpoint != new Uri($"https://{account}.blob.core.windows.net/"))
            throw new InvalidDataException("Diagnostic requires the exact dedicated public Azure Blob endpoint.");
        _http = http;
        _endpoint = endpoint;
        _auth = new SharedKeyAuthenticator(account, key);
    }

    internal async Task VerifySettingsAsync(CancellationToken token)
    {
        var root = await ReadAsync("?restype=service&comp=properties", token).ConfigureAwait(false);
        if (root.Name != "StorageServiceProperties"
            || root.Element("DeleteRetentionPolicy")?.Element("Enabled")?.Value != "false"
            || root.Element("ContainerDeleteRetentionPolicy")?.Element("Enabled")?.Value != "false"
            || root.Element("IsVersioningEnabled")?.Value == "true")
            throw new InvalidDataException("Diagnostic requires blob/container soft delete and versioning disabled.");
    }

    internal async Task<long> CountAsync(CancellationToken token)
    {
        string? marker = null;
        var markers = new HashSet<string>(StringComparer.Ordinal);
        long count = 0;
        for (var page = 0; page < 1000; page++)
        {
            token.ThrowIfCancellationRequested();
            var query = "?comp=list&maxresults=25"
                + (marker is null ? "" : "&marker=" + Uri.EscapeDataString(marker));
            var root = await ReadAsync(query, token).ConfigureAwait(false);
            var containers = root.Elements("Containers").ToArray();
            var next = root.Elements("NextMarker").ToArray();
            if (root.Name != "EnumerationResults" || containers.Length != 1 || next.Length > 1
                || containers[0].Elements().Any(element => element.Name != "Container"
                    || string.IsNullOrEmpty(element.Element("Name")?.Value)))
                throw new InvalidDataException("Invalid Blob inventory page.");
            count = checked(count + containers[0].Elements("Container").LongCount());
            marker = next.SingleOrDefault()?.Value;
            if (string.IsNullOrEmpty(marker))
                return count;
            if (marker.Length > 4096 || !markers.Add(marker))
                throw new InvalidDataException("Invalid Blob inventory continuation.");
        }
        throw new InvalidDataException("Blob inventory exceeded its page limit.");
    }

    private async Task<XElement> ReadAsync(string query, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, query));
        await _auth.AuthenticateAsync(request, token).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Blob inventory request failed.", null, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024,
        });
        return XElement.Load(reader);
    }
}
