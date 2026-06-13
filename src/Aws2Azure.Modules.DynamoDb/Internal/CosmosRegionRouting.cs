using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

internal sealed class CosmosAccountInfo
{
    public CosmosAccountInfo(
        Uri accountEndpoint,
        CosmosConsistencyLevel defaultConsistency,
        bool enableMultipleWriteLocations,
        CosmosAccountLocation[] readableLocations,
        CosmosAccountLocation[] writableLocations)
    {
        AccountEndpoint = accountEndpoint;
        DefaultConsistency = defaultConsistency;
        EnableMultipleWriteLocations = enableMultipleWriteLocations;
        ReadableLocations = readableLocations;
        WritableLocations = writableLocations;
    }

    public Uri AccountEndpoint { get; }
    public CosmosConsistencyLevel DefaultConsistency { get; }
    public bool EnableMultipleWriteLocations { get; }
    public CosmosAccountLocation[] ReadableLocations { get; }
    public CosmosAccountLocation[] WritableLocations { get; }

    public static CosmosAccountInfo Fallback(Uri accountEndpoint)
    {
        var fallback = new[]
        {
            new CosmosAccountLocation(string.Empty, accountEndpoint),
        };
        return new CosmosAccountInfo(
            accountEndpoint,
            CosmosConsistencyLevel.Unknown,
            enableMultipleWriteLocations: false,
            readableLocations: fallback,
            writableLocations: fallback);
    }
}

internal readonly record struct CosmosAccountLocation(string Name, Uri Endpoint);

internal static class CosmosAccountInfoParser
{
    public static CosmosAccountInfo Parse(ReadOnlySpan<byte> accountJson, Uri configuredEndpoint)
    {
        try
        {
            using var doc = JsonDocument.Parse(accountJson.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return CosmosAccountInfo.Fallback(configuredEndpoint);
            }

            var consistency = ParseDefaultConsistency(doc.RootElement);
            var multiWrite = false;
            if (doc.RootElement.TryGetProperty("enableMultipleWriteLocations", out var multiWriteEl)
                && (multiWriteEl.ValueKind == JsonValueKind.True || multiWriteEl.ValueKind == JsonValueKind.False))
            {
                multiWrite = multiWriteEl.GetBoolean();
            }

            var readable = ParseLocations(doc.RootElement, "readableLocations", configuredEndpoint);
            var writable = ParseLocations(doc.RootElement, "writableLocations", configuredEndpoint);

            return new CosmosAccountInfo(configuredEndpoint, consistency, multiWrite, readable, writable);
        }
        catch (JsonException)
        {
            return CosmosAccountInfo.Fallback(configuredEndpoint);
        }
    }

    private static CosmosConsistencyLevel ParseDefaultConsistency(JsonElement root)
    {
        if (!root.TryGetProperty("userConsistencyPolicy", out var policy)
            || policy.ValueKind != JsonValueKind.Object)
        {
            return CosmosConsistencyLevel.Unknown;
        }

        if (!policy.TryGetProperty("defaultConsistencyLevel", out var level)
            || level.ValueKind != JsonValueKind.String)
        {
            return CosmosConsistencyLevel.Unknown;
        }

        return CosmosConsistency.FromName(level.GetString());
    }

    private static CosmosAccountLocation[] ParseLocations(JsonElement root, string propertyName, Uri configuredEndpoint)
    {
        if (!root.TryGetProperty(propertyName, out var locationsEl)
            || locationsEl.ValueKind != JsonValueKind.Array)
        {
            return
            [
                new CosmosAccountLocation(string.Empty, configuredEndpoint),
            ];
        }

        var locations = new List<CosmosAccountLocation>();
        foreach (var locationEl in locationsEl.EnumerateArray())
        {
            if (locationEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!locationEl.TryGetProperty("databaseAccountEndpoint", out var endpointEl)
                || endpointEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var endpoint = endpointEl.GetString();
            if (string.IsNullOrWhiteSpace(endpoint)
                || !Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var endpointUri))
            {
                continue;
            }

            var name = string.Empty;
            if (locationEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                name = nameEl.GetString() ?? string.Empty;
            }

            locations.Add(new CosmosAccountLocation(name, endpointUri));
        }

        if (locations.Count == 0)
        {
            locations.Add(new CosmosAccountLocation(string.Empty, configuredEndpoint));
        }

        return locations.ToArray();
    }
}

internal static class CosmosRegionRouting
{
    public static bool IsReadOperation(
        HttpMethod method,
        IReadOnlyList<KeyValuePair<string, string>>? extraHeaders)
    {
        if (method == HttpMethod.Get || method == HttpMethod.Head)
        {
            return true;
        }

        if (method != HttpMethod.Post || extraHeaders is null)
        {
            return false;
        }

        for (int i = 0; i < extraHeaders.Count; i++)
        {
            var header = extraHeaders[i];
            if (header.Key.Equals("x-ms-documentdb-isquery", StringComparison.OrdinalIgnoreCase)
                && header.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static Uri[] BuildCandidateEndpoints(
        CosmosAccountInfo account,
        IReadOnlyList<string>? preferredRegions,
        bool isRead,
        Func<Uri, bool>? isEndpointAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(account);

        var candidates = new List<Uri>(4);
        if (isRead)
        {
            AddPreferred(candidates, account.ReadableLocations, preferredRegions, isEndpointAvailable);
            AddAll(candidates, account.ReadableLocations, isEndpointAvailable);
        }
        else if (account.EnableMultipleWriteLocations)
        {
            AddPreferred(candidates, account.WritableLocations, preferredRegions, isEndpointAvailable);
            AddAll(candidates, account.WritableLocations, isEndpointAvailable);
        }
        else
        {
            AddFirst(candidates, account.WritableLocations, isEndpointAvailable);
        }

        AddEndpoint(candidates, account.AccountEndpoint, isEndpointAvailable);
        return candidates.ToArray();
    }

    /// <summary>
    /// Decides whether a Cosmos response should trigger a cross-region failover
    /// retry. Reads and writes are treated differently on purpose:
    /// <list type="bullet">
    ///   <item><b>Reads</b> may safely fail over on any transient impairment
    ///   (503 / 408) — re-issuing a read against another region has no
    ///   side effects.</item>
    ///   <item><b>Writes</b> are <em>ambiguous</em> on 503 / 408: the first
    ///   write may have committed before the failure surfaced, so transparently
    ///   replaying it against another region risks a duplicate mutation (e.g.
    ///   a non-idempotent <c>UpdateItem</c>). Writes therefore only fail over on
    ///   a guaranteed <em>pre-commit</em> rejection — 403 substatus 3
    ///   (write-region-not-writable) — where Cosmos is certain the write did
    ///   not happen. Every other write failure is returned to the caller so the
    ///   AWS SDK owns the (idempotent-from-its-view) retry.</item>
    /// </list>
    /// </summary>
    public static bool IsFailoverStatus(HttpResponseMessage response, bool isWrite)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (isWrite)
        {
            return response.StatusCode == HttpStatusCode.Forbidden
                && HasSubStatus(response, "3");
        }

        return response.StatusCode == HttpStatusCode.ServiceUnavailable
            || response.StatusCode == HttpStatusCode.RequestTimeout;
    }

    public static string BuildLocationSummary(CosmosAccountLocation[] locations)
    {
        if (locations.Length == 0)
        {
            return "(none)";
        }

        var parts = new string[locations.Length];
        for (int i = 0; i < locations.Length; i++)
        {
            var name = string.IsNullOrWhiteSpace(locations[i].Name) ? "(unnamed)" : locations[i].Name;
            parts[i] = name + "=" + locations[i].Endpoint.AbsoluteUri;
        }
        return string.Join(", ", parts);
    }

    private static void AddPreferred(
        List<Uri> candidates,
        CosmosAccountLocation[] locations,
        IReadOnlyList<string>? preferredRegions,
        Func<Uri, bool>? isEndpointAvailable)
    {
        if (preferredRegions is null || preferredRegions.Count == 0)
        {
            return;
        }

        for (int p = 0; p < preferredRegions.Count; p++)
        {
            var preferred = preferredRegions[p];
            if (string.IsNullOrWhiteSpace(preferred))
            {
                continue;
            }

            for (int i = 0; i < locations.Length; i++)
            {
                if (locations[i].Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                {
                    AddEndpoint(candidates, locations[i].Endpoint, isEndpointAvailable);
                    break;
                }
            }
        }
    }

    private static void AddAll(
        List<Uri> candidates,
        CosmosAccountLocation[] locations,
        Func<Uri, bool>? isEndpointAvailable)
    {
        for (int i = 0; i < locations.Length; i++)
        {
            AddEndpoint(candidates, locations[i].Endpoint, isEndpointAvailable);
        }
    }

    private static void AddFirst(
        List<Uri> candidates,
        CosmosAccountLocation[] locations,
        Func<Uri, bool>? isEndpointAvailable)
    {
        if (locations.Length == 0)
        {
            return;
        }

        AddEndpoint(candidates, locations[0].Endpoint, isEndpointAvailable);
    }

    private static void AddEndpoint(List<Uri> candidates, Uri endpoint, Func<Uri, bool>? isEndpointAvailable)
    {
        if (isEndpointAvailable is not null && !isEndpointAvailable(endpoint))
        {
            return;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].AbsoluteUri.Equals(endpoint.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(endpoint);
    }

    private static bool HasSubStatus(HttpResponseMessage response, string expected)
    {
        if (!response.Headers.TryGetValues("x-ms-substatus", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is not null && value.Trim().Equals(expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
