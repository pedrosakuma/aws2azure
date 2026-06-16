using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Observability;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Kinesis.EventHubsRest;

public interface IEventHubsManagementClient
{
    ValueTask<EventHubDescription> GetEventHubAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string eventHubName,
        CancellationToken cancellationToken);
}

public sealed class EventHubsManagementClient : IEventHubsManagementClient
{
    private readonly AzureHttpClient _httpClient;
    private readonly IEventHubsAuthenticator _authenticator;
    private readonly ILogger<EventHubsManagementClient> _logger;

    public EventHubsManagementClient(
        AzureHttpClient httpClient,
        IEventHubsAuthenticator authenticator,
        ILogger<EventHubsManagementClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _authenticator = authenticator;
        _logger = logger;
    }

    public async ValueTask<EventHubDescription> GetEventHubAsync(
        EventHubsCredentials credentials,
        string namespaceFqdn,
        string eventHubName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventHubName);

        if (TryResolveConfiguredDescription(credentials, eventHubName, out var configured))
        {
            return configured;
        }

        var requestUri = BuildRequestUri(credentials, namespaceFqdn, eventHubName);
        EventHubsManagementClientLog.FetchingEventHub(_logger, namespaceFqdn, eventHubName);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        request.Headers.TryAddWithoutValidation("Content-Type", "application/atom+xml;type=entry;charset=utf-8");
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            EventHubsManagementClientLog.EventHubRequestFailed(_logger, namespaceFqdn, eventHubName, (int)response.StatusCode);
            throw new EventHubsManagementException(response.StatusCode, errorBody);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return await ParseEventHubAsync(content, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<EventHubDescription> ParseEventHubAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var fragment = ExtractEventHubDescriptionFragment(content);
        var partitionIds = ExtractRepeatedValues(fragment, "string");
        var partitionCount = ParseInt(ExtractScalarValue(fragment, "PartitionCount"), "PartitionCount");
        var retentionDays = ParseInt(ExtractScalarValue(fragment, "MessageRetentionInDays"), "MessageRetentionInDays");
        var createdAt = ParseDateTimeOffset(ExtractScalarValue(fragment, "CreatedAt"), "CreatedAt");

        cancellationToken.ThrowIfCancellationRequested();

        if (partitionIds.Count == 0)
        {
            partitionIds.Capacity = partitionCount;
            for (var i = 0; i < partitionCount; i++)
            {
                partitionIds.Add(i.ToString(CultureInfo.InvariantCulture));
            }
        }

        return new EventHubDescription(partitionIds.Count, partitionIds.ToArray(), retentionDays, createdAt);
    }

    private static bool TryResolveConfiguredDescription(EventHubsCredentials credentials, string eventHubName, out EventHubDescription description)
    {
        description = default!;
        if (credentials.Streams is null)
        {
            return false;
        }

        foreach (var (streamName, settings) in credentials.Streams)
        {
            if (settings?.PartitionCount is not int partitionCount || partitionCount <= 0)
            {
                continue;
            }

            var resolvedEventHubName = !string.IsNullOrWhiteSpace(settings.EventHubName)
                ? settings.EventHubName!
                : streamName;
            if (!string.Equals(resolvedEventHubName, eventHubName, StringComparison.Ordinal))
            {
                continue;
            }

            var partitionIds = new string[partitionCount];
            for (var i = 0; i < partitionIds.Length; i++)
            {
                partitionIds[i] = i.ToString(CultureInfo.InvariantCulture);
            }

            description = new EventHubDescription(partitionCount, partitionIds, 1, DateTimeOffset.UnixEpoch);
            return true;
        }

        return false;
    }

    private static string ExtractEventHubDescriptionFragment(string content)
    {
        const string startMarker = "<EventHubDescription";
        const string endMarker = "</EventHubDescription>";

        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end < start)
        {
            throw new InvalidDataException("Event Hubs management payload did not include EventHubDescription.");
        }

        return content[start..(end + endMarker.Length)];
    }

    private static string ExtractScalarValue(string fragment, string elementName)
    {
        var open = "<" + elementName + ">";
        var close = "</" + elementName + ">";
        var start = fragment.IndexOf(open, StringComparison.Ordinal);
        var end = fragment.IndexOf(close, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end < start)
        {
            throw new InvalidDataException($"Event Hubs management payload did not include {elementName}.");
        }

        return fragment[(start + open.Length)..end];
    }

    private static List<string> ExtractRepeatedValues(string fragment, string elementName)
    {
        var values = new List<string>();
        var open = "<" + elementName + ">";
        var close = "</" + elementName + ">";
        var searchStart = 0;

        while (true)
        {
            var start = fragment.IndexOf(open, searchStart, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = fragment.IndexOf(close, start + open.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            values.Add(fragment[(start + open.Length)..end]);
            searchStart = end + close.Length;
        }

        return values;
    }

    private static Uri BuildRequestUri(EventHubsCredentials credentials, string namespaceFqdn, string eventHubName)
    {
        var scheme = ResolveManagementScheme(credentials);
        return new Uri($"{scheme}://{namespaceFqdn.TrimEnd('/')}/{Uri.EscapeDataString(eventHubName)}?api-version=2014-01", UriKind.Absolute);
    }

    private static string ResolveManagementScheme(EventHubsCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri)
            && string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UriSchemeHttp;
        }

        return Uri.UriSchemeHttps;
    }

    private static int ParseInt(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidDataException($"Event Hubs management payload field '{fieldName}' was not a valid integer.");
        }

        return parsed;
    }

    private static DateTimeOffset ParseDateTimeOffset(string value, string fieldName)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw new InvalidDataException($"Event Hubs management payload field '{fieldName}' was not a valid timestamp.");
        }

        return parsed;
    }
}

public sealed record EventHubDescription(
    int PartitionCount,
    IReadOnlyList<string> PartitionIds,
    int MessageRetentionDays,
    DateTimeOffset CreatedAt);

public sealed class EventHubsManagementException : Exception
{
    public EventHubsManagementException(HttpStatusCode statusCode, string? responseBody)
        : base($"Event Hubs management API returned {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }
}

internal static partial class EventHubsManagementClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Fetching Event Hub metadata for namespace '{NamespaceFqdn}' and entity '{EventHubName}'")]
    public static partial void FetchingEventHub(ILogger logger, string namespaceFqdn, string eventHubName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Event Hub metadata request for namespace '{NamespaceFqdn}' and entity '{EventHubName}' failed with HTTP {StatusCode}")]
    public static partial void EventHubRequestFailed(ILogger logger, string namespaceFqdn, string eventHubName, int statusCode);
}
