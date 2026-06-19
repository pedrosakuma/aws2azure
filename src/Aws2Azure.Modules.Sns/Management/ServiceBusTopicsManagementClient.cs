using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Observability;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Sns.Management;

public interface IServiceBusTopicsManagementClient
{
    ValueTask CreateTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken);

    ValueTask DeleteTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken);

    ValueTask<ServiceBusTopicPage> ListTopicsAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        int skip,
        int top,
        CancellationToken cancellationToken);

    ValueTask<ServiceBusTopicDescription?> GetTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken);

    ValueTask CreateSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        string userMetadata,
        CancellationToken cancellationToken);

    ValueTask DeleteSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken);

    ValueTask<ServiceBusSubscriptionPage> ListSubscriptionsAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        int skip,
        int top,
        CancellationToken cancellationToken);

    ValueTask<ServiceBusSubscriptionDescription?> GetSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken);

    ValueTask UpdateSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        ServiceBusSubscriptionDescription description,
        CancellationToken cancellationToken);
}

public sealed class ServiceBusTopicsManagementClient : IServiceBusTopicsManagementClient
{
    public const string ApiVersion = "2021-05";
    public const string LongIdleIso8601 = "P10675199DT2H48M5.4775807S";
    public const string DefaultLockDurationIso8601 = "PT30S";
    public const int DefaultMaxDeliveryCount = 10;

    private readonly AzureHttpClient _httpClient;
    private readonly IServiceBusTopicsAuthenticator _authenticator;
    private readonly ILogger<ServiceBusTopicsManagementClient> _logger;

    public ServiceBusTopicsManagementClient(
        AzureHttpClient httpClient,
        IServiceBusTopicsAuthenticator authenticator,
        ILogger<ServiceBusTopicsManagementClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _authenticator = authenticator;
        _logger = logger;
    }

    public async ValueTask CreateTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, topicName);
        ServiceBusTopicsManagementClientLog.CreatingTopic(_logger, namespaceFqdn, topicName);

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        request.Content = new StringContent(ServiceBusAtomXml.BuildTopicDescriptionEntry(), Encoding.UTF8, "application/atom+xml");
        request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(CreateTopicAsync), namespaceFqdn, topicName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    public async ValueTask DeleteTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        ServiceBusTopicsManagementClientLog.DeletingTopic(_logger, namespaceFqdn, topicName);

        // Probe-before-delete: the SB emulator returns HTTP 400 (not 404) for DELETE on a missing
        // entity with no distinguishing body, so we cannot rely on the DELETE status code alone for
        // idempotency. A preceding GET disambiguates cleanly against both real SB and the emulator.
        var existing = await GetTopicAsync(credentials, namespaceFqdn, topicName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, topicName);
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(DeleteTopicAsync), namespaceFqdn, topicName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    public async ValueTask<ServiceBusTopicPage> ListTopicsAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        var requestUri = BuildListTopicsUri(credentials, namespaceFqdn, skip, top);
        ServiceBusTopicsManagementClientLog.ListingTopics(_logger, namespaceFqdn, skip, top);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(ListTopicsAsync), namespaceFqdn, "$Resources/topics", (int)response.StatusCode);
            throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var topicNames = await ServiceBusAtomXml.ParseTopicNamesAsync(content, cancellationToken).ConfigureAwait(false);
        return new ServiceBusTopicPage(topicNames);
    }

    public async ValueTask<ServiceBusTopicDescription?> GetTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, topicName);
        ServiceBusTopicsManagementClientLog.GettingTopic(_logger, namespaceFqdn, topicName);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(GetTopicAsync), namespaceFqdn, topicName, (int)response.StatusCode);
            throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entry = await ServiceBusAtomXml.ParseFirstEntryAsync(content, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        return new ServiceBusTopicDescription(
            entry.Title ?? topicName,
            entry.SubscriptionCount ?? 0,
            entry.RequiresDuplicateDetection ?? false);
    }

    public async ValueTask CreateSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        string userMetadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        ArgumentNullException.ThrowIfNull(userMetadata);

        var requestUri = BuildSubscriptionUri(credentials, namespaceFqdn, topicName, subscriptionName);
        ServiceBusTopicsManagementClientLog.CreatingSubscription(_logger, namespaceFqdn, topicName, subscriptionName);

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        var requestBody = ServiceBusAtomXml.BuildSubscriptionDescriptionEntry(userMetadata);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/atom+xml");
        request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        var respBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(CreateSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, respBody);
    }

    public async ValueTask DeleteSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        ServiceBusTopicsManagementClientLog.DeletingSubscription(_logger, namespaceFqdn, topicName, subscriptionName);

        // Probe-before-delete: same emulator quirk as DeleteTopicAsync (400 on missing entity).
        var existing = await GetSubscriptionAsync(credentials, namespaceFqdn, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var requestUri = BuildSubscriptionUri(credentials, namespaceFqdn, topicName, subscriptionName);
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(DeleteSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    public async ValueTask<ServiceBusSubscriptionPage> ListSubscriptionsAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        int skip,
        int top,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        var requestUri = BuildListSubscriptionsUri(credentials, namespaceFqdn, topicName, skip, top);
        ServiceBusTopicsManagementClientLog.ListingSubscriptions(_logger, namespaceFqdn, topicName, skip, top);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(ListSubscriptionsAsync), namespaceFqdn, topicName + "/subscriptions", (int)response.StatusCode);
            throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var subscriptions = await ServiceBusAtomXml.ParseSubscriptionDescriptionsAsync(content, cancellationToken).ConfigureAwait(false);
        return new ServiceBusSubscriptionPage(subscriptions);
    }

    public async ValueTask<ServiceBusSubscriptionDescription?> GetSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        var requestUri = BuildSubscriptionUri(credentials, namespaceFqdn, topicName, subscriptionName);
        ServiceBusTopicsManagementClientLog.GettingSubscription(_logger, namespaceFqdn, topicName, subscriptionName);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(GetSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName, (int)response.StatusCode);
            throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entry = await ServiceBusAtomXml.ParseFirstEntryAsync(content, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        return new ServiceBusSubscriptionDescription(
            entry.Title ?? subscriptionName,
            entry.UserMetadata,
            entry.LockDuration ?? DefaultLockDurationIso8601,
            entry.MaxDeliveryCount ?? DefaultMaxDeliveryCount,
            entry.AutoDeleteOnIdle ?? LongIdleIso8601);
    }

    public async ValueTask UpdateSubscriptionAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        ServiceBusSubscriptionDescription description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(description.SubscriptionName);

        var requestUri = BuildSubscriptionUri(credentials, namespaceFqdn, topicName, description.SubscriptionName);
        ServiceBusTopicsManagementClientLog.UpdatingSubscription(_logger, namespaceFqdn, topicName, description.SubscriptionName);

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
        request.Content = new StringContent(ServiceBusAtomXml.BuildSubscriptionDescriptionEntry(description), Encoding.UTF8, "application/atom+xml");
        request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
        await _authenticator.AuthenticateAsync(request, credentials, cancellationToken).ConfigureAwait(false);

        using var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ServiceBusTopicsManagementClientLog.TopicRequestFailed(_logger, nameof(UpdateSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + description.SubscriptionName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    private static Uri BuildTopicUri(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName)
    {
        var root = ResolveManagementRoot(credentials, namespaceFqdn);
        return new Uri($"{root}/{Uri.EscapeDataString(topicName)}?api-version={ApiVersion}", UriKind.Absolute);
    }

    private static Uri BuildListTopicsUri(ServiceBusTopicsCredentials credentials, string namespaceFqdn, int skip, int top)
    {
        var root = ResolveManagementRoot(credentials, namespaceFqdn);
        return new Uri($"{root}/$Resources/topics?api-version={ApiVersion}&$skip={skip}&$top={top}", UriKind.Absolute);
    }

    private static Uri BuildSubscriptionUri(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName)
    {
        var root = ResolveManagementRoot(credentials, namespaceFqdn);
        return new Uri($"{root}/{Uri.EscapeDataString(topicName)}/subscriptions/{Uri.EscapeDataString(subscriptionName)}?api-version={ApiVersion}", UriKind.Absolute);
    }

    private static Uri BuildListSubscriptionsUri(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, int skip, int top)
    {
        var root = ResolveManagementRoot(credentials, namespaceFqdn);
        return new Uri($"{root}/{Uri.EscapeDataString(topicName)}/subscriptions?api-version={ApiVersion}&$skip={skip}&$top={top}", UriKind.Absolute);
    }

    private static string ResolveManagementRoot(ServiceBusTopicsCredentials credentials, string namespaceFqdn)
    {
        if (!string.IsNullOrWhiteSpace(credentials.ManagementEndpoint)
            && Uri.TryCreate(credentials.ManagementEndpoint, UriKind.Absolute, out var managementUri))
        {
            return managementUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri)
            && (string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return endpointUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return $"https://{namespaceFqdn.TrimEnd('/')}";
    }
}

public sealed record ServiceBusTopicPage(IReadOnlyList<string> TopicNames);
public sealed record ServiceBusTopicDescription(string TopicName, int SubscriptionCount, bool RequiresDuplicateDetection);
public sealed record ServiceBusSubscriptionPage(IReadOnlyList<ServiceBusSubscriptionDescription> Subscriptions);
public sealed record ServiceBusSubscriptionDescription(
    string SubscriptionName,
    string? UserMetadata,
    string LockDuration,
    int MaxDeliveryCount,
    string AutoDeleteOnIdle);

public sealed class ServiceBusTopicsManagementException : Exception
{
    public ServiceBusTopicsManagementException(HttpStatusCode statusCode, string? responseBody)
        : base($"Service Bus Topics management API returned {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }
}

internal static partial class ServiceBusTopicsManagementClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Creating Service Bus topic for namespace '{NamespaceFqdn}' and entity '{TopicName}'")]
    public static partial void CreatingTopic(ILogger logger, string namespaceFqdn, string topicName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Deleting Service Bus topic for namespace '{NamespaceFqdn}' and entity '{TopicName}'")]
    public static partial void DeletingTopic(ILogger logger, string namespaceFqdn, string topicName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Listing Service Bus topics for namespace '{NamespaceFqdn}' with skip={Skip} top={Top}")]
    public static partial void ListingTopics(ILogger logger, string namespaceFqdn, int skip, int top);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Service Bus Topics request '{Operation}' for namespace '{NamespaceFqdn}' and entity '{EntityName}' failed with HTTP {StatusCode}")]
    public static partial void TopicRequestFailed(ILogger logger, string operation, string namespaceFqdn, string entityName, int statusCode);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "Creating Service Bus subscription for namespace '{NamespaceFqdn}', topic '{TopicName}', and subscription '{SubscriptionName}'")]
    public static partial void CreatingSubscription(ILogger logger, string namespaceFqdn, string topicName, string subscriptionName);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Deleting Service Bus subscription for namespace '{NamespaceFqdn}', topic '{TopicName}', and subscription '{SubscriptionName}'")]
    public static partial void DeletingSubscription(ILogger logger, string namespaceFqdn, string topicName, string subscriptionName);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "Listing Service Bus subscriptions for namespace '{NamespaceFqdn}', topic '{TopicName}', skip={Skip}, top={Top}")]
    public static partial void ListingSubscriptions(ILogger logger, string namespaceFqdn, string topicName, int skip, int top);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug,
        Message = "Getting Service Bus subscription for namespace '{NamespaceFqdn}', topic '{TopicName}', and subscription '{SubscriptionName}'")]
    public static partial void GettingSubscription(ILogger logger, string namespaceFqdn, string topicName, string subscriptionName);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
        Message = "Getting Service Bus topic for namespace '{NamespaceFqdn}' and entity '{TopicName}'")]
    public static partial void GettingTopic(ILogger logger, string namespaceFqdn, string topicName);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug,
        Message = "Updating Service Bus subscription for namespace '{NamespaceFqdn}', topic '{TopicName}', and subscription '{SubscriptionName}'")]
    public static partial void UpdatingSubscription(ILogger logger, string namespaceFqdn, string topicName, string subscriptionName);
}
