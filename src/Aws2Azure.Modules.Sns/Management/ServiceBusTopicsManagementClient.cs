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
        ServiceBusTopicDescription description,
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

    ValueTask UpdateTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        ServiceBusTopicDescription description,
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

    ValueTask PutSubscriptionRuleAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        ServiceBusSubscriptionRuleDescription description,
        bool updateExisting,
        CancellationToken cancellationToken);

    ValueTask DeleteSubscriptionRuleAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        string ruleName,
        CancellationToken cancellationToken);
}

public sealed class ServiceBusTopicsManagementClient : IServiceBusTopicsManagementClient
{
    public const string ApiVersion = "2021-05";
    public const string LongIdleIso8601 = "P10675199DT2H48M5.4775807S";
    public const string DefaultLockDurationIso8601 = "PT30S";
    public const int DefaultMaxDeliveryCount = 10;
    private const int AuthorizationRetryAttempts = 3;

    private readonly AzureHttpClient _httpClient;
    private readonly IServiceBusTopicsAuthenticator _authenticator;
    private readonly ILogger<ServiceBusTopicsManagementClient> _logger;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _delayAsync;

    public ServiceBusTopicsManagementClient(
        AzureHttpClient httpClient,
        IServiceBusTopicsAuthenticator authenticator,
        ILogger<ServiceBusTopicsManagementClient> logger,
        Func<TimeSpan, CancellationToken, ValueTask>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _authenticator = authenticator;
        _logger = logger;
        _delayAsync = delayAsync ?? DelayAsyncDefault;
    }

    public async ValueTask CreateTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        ServiceBusTopicDescription description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(description.TopicName);

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, description.TopicName);
        SnsLog.CreatingTopic(_logger, namespaceFqdn, description.TopicName);

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(CreateTopicAsync),
                namespaceFqdn,
                description.TopicName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    request.Content = new StringContent(ServiceBusAtomXml.BuildTopicDescriptionEntry(description), Encoding.UTF8, "application/atom+xml");
                    request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SnsLog.TopicRequestFailed(_logger, nameof(CreateTopicAsync), namespaceFqdn, description.TopicName, (int)response.StatusCode);
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

        SnsLog.DeletingTopic(_logger, namespaceFqdn, topicName);

        // Probe-before-delete: the SB emulator returns HTTP 400 (not 404) for DELETE on a missing
        // entity with no distinguishing body, so we cannot rely on the DELETE status code alone for
        // idempotency. A preceding GET disambiguates cleanly against both real SB and the emulator.
        var existing = await GetTopicAsync(credentials, namespaceFqdn, topicName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, topicName);
        using var response = await SendWithAuthorizationRetryAsync(
                nameof(DeleteTopicAsync),
                namespaceFqdn,
                topicName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SnsLog.TopicRequestFailed(_logger, nameof(DeleteTopicAsync), namespaceFqdn, topicName, (int)response.StatusCode);
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
        SnsLog.ListingTopics(_logger, namespaceFqdn, skip, top);

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(ListTopicsAsync),
                namespaceFqdn,
                "$Resources/topics",
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            SnsLog.TopicRequestFailed(_logger, nameof(ListTopicsAsync), namespaceFqdn, "$Resources/topics", (int)response.StatusCode);
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
        SnsLog.GettingTopic(_logger, namespaceFqdn, topicName);

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(GetTopicAsync),
                namespaceFqdn,
                topicName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            SnsLog.TopicRequestFailed(_logger, nameof(GetTopicAsync), namespaceFqdn, topicName, (int)response.StatusCode);
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
            entry.RequiresDuplicateDetection ?? false,
            entry.UserMetadata,
            entry.TopicProperties);
    }

    public async ValueTask UpdateTopicAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        ServiceBusTopicDescription description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(description.TopicName);

        var requestUri = BuildTopicUri(credentials, namespaceFqdn, description.TopicName);
        SnsLog.UpdatingSubscription(_logger, namespaceFqdn, description.TopicName, "$topic");

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(UpdateTopicAsync),
                namespaceFqdn,
                description.TopicName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    request.Headers.TryAddWithoutValidation("If-Match", "*");
                    request.Content = new StringContent(ServiceBusAtomXml.BuildTopicDescriptionEntry(description), Encoding.UTF8, "application/atom+xml");
                    request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SnsLog.TopicRequestFailed(_logger, nameof(UpdateTopicAsync), namespaceFqdn, description.TopicName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
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
        SnsLog.CreatingSubscription(_logger, namespaceFqdn, topicName, subscriptionName);

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(CreateSubscriptionAsync),
                namespaceFqdn,
                topicName + "/subscriptions/" + subscriptionName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    var requestBody = ServiceBusAtomXml.BuildSubscriptionDescriptionEntry(userMetadata);
                    request.Content = new StringContent(requestBody, Encoding.UTF8, "application/atom+xml");
                    request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        var respBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        SnsLog.TopicRequestFailed(_logger, nameof(CreateSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName, (int)response.StatusCode);
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

        SnsLog.DeletingSubscription(_logger, namespaceFqdn, topicName, subscriptionName);

        // Probe-before-delete: same emulator quirk as DeleteTopicAsync (400 on missing entity).
        var existing = await GetSubscriptionAsync(credentials, namespaceFqdn, topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        var requestUri = BuildSubscriptionUri(credentials, namespaceFqdn, topicName, subscriptionName);
        using var response = await SendWithAuthorizationRetryAsync(
                nameof(DeleteSubscriptionAsync),
                namespaceFqdn,
                topicName + "/subscriptions/" + subscriptionName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SnsLog.TopicRequestFailed(_logger, nameof(DeleteSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName, (int)response.StatusCode);
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
        SnsLog.ListingSubscriptions(_logger, namespaceFqdn, topicName, skip, top);

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(ListSubscriptionsAsync),
                namespaceFqdn,
                topicName + "/subscriptions",
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            SnsLog.TopicRequestFailed(_logger, nameof(ListSubscriptionsAsync), namespaceFqdn, topicName + "/subscriptions", (int)response.StatusCode);
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
        SnsLog.GettingSubscription(_logger, namespaceFqdn, topicName, subscriptionName);

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(GetSubscriptionAsync),
                namespaceFqdn,
                topicName + "/subscriptions/" + subscriptionName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            SnsLog.TopicRequestFailed(_logger, nameof(GetSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName, (int)response.StatusCode);
            throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entry = await ServiceBusAtomXml.ParseFirstEntryAsync(content, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        var etag = TryGetEtag(response) ?? entry.ETag;
        return new ServiceBusSubscriptionDescription(
            entry.Title ?? subscriptionName,
            entry.UserMetadata,
            entry.LockDuration ?? DefaultLockDurationIso8601,
            entry.MaxDeliveryCount ?? DefaultMaxDeliveryCount,
            entry.AutoDeleteOnIdle ?? LongIdleIso8601,
            etag,
            entry.SubscriptionProperties,
            entry.HasDefaultRuleDescription);
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
        SnsLog.UpdatingSubscription(_logger, namespaceFqdn, topicName, description.SubscriptionName);

        using var response = await SendWithAuthorizationRetryAsync(
                nameof(UpdateSubscriptionAsync),
                namespaceFqdn,
                topicName + "/subscriptions/" + description.SubscriptionName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    request.Headers.TryAddWithoutValidation("If-Match", "*");
                    request.Content = new StringContent(ServiceBusAtomXml.BuildSubscriptionDescriptionEntry(description), Encoding.UTF8, "application/atom+xml");
                    request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SnsLog.TopicRequestFailed(_logger, nameof(UpdateSubscriptionAsync), namespaceFqdn, topicName + "/subscriptions/" + description.SubscriptionName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    public async ValueTask PutSubscriptionRuleAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        ServiceBusSubscriptionRuleDescription description,
        bool updateExisting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(description.RuleName);

        var requestUri = BuildSubscriptionRuleUri(credentials, namespaceFqdn, topicName, subscriptionName, description.RuleName);
        using var response = await SendWithAuthorizationRetryAsync(
                nameof(PutSubscriptionRuleAsync),
                namespaceFqdn,
                topicName + "/subscriptions/" + subscriptionName + "/rules/" + description.RuleName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
                    if (updateExisting)
                    {
                        request.Headers.TryAddWithoutValidation("If-Match", "*");
                    }

                    request.Content = new StringContent(ServiceBusAtomXml.BuildSubscriptionRuleDescriptionEntry(description), Encoding.UTF8, "application/atom+xml");
                    request.Content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "entry"));
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SnsLog.TopicRequestFailed(_logger, nameof(PutSubscriptionRuleAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName + "/rules/" + description.RuleName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    public async ValueTask DeleteSubscriptionRuleAsync(
        ServiceBusTopicsCredentials credentials,
        string namespaceFqdn,
        string topicName,
        string subscriptionName,
        string ruleName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);

        var requestUri = BuildSubscriptionRuleUri(credentials, namespaceFqdn, topicName, subscriptionName, ruleName);
        using var response = await SendWithAuthorizationRetryAsync(
                nameof(DeleteSubscriptionRuleAsync),
                namespaceFqdn,
                topicName + "/subscriptions/" + subscriptionName + "/rules/" + ruleName,
                cancellationToken,
                async ct =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
                    await _authenticator.AuthenticateAsync(request, credentials, ct).ConfigureAwait(false);
                    return request;
                })
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SnsLog.TopicRequestFailed(_logger, nameof(DeleteSubscriptionRuleAsync), namespaceFqdn, topicName + "/subscriptions/" + subscriptionName + "/rules/" + ruleName, (int)response.StatusCode);
        throw new ServiceBusTopicsManagementException(response.StatusCode, errorBody);
    }

    private async Task<HttpResponseMessage> SendWithAuthorizationRetryAsync(
        string operationName,
        string namespaceFqdn,
        string entityPath,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<HttpRequestMessage>> createRequestAsync)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = await createRequestAsync(cancellationToken).ConfigureAwait(false);
            var response = await BackendTimingContext.TimeAsync(() => _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken))
                .ConfigureAwait(false);
            if (!ShouldRetryAuthorizationFailure(response.StatusCode, attempt))
            {
                return response;
            }

            response.Dispose();

            // Real Azure can briefly return 401/403 on freshly assigned Service Bus RBAC roles
            // before authorization caches converge, so retry a small bounded number of times.
            await _delayAsync(GetAuthorizationRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetryAuthorizationFailure(HttpStatusCode statusCode, int attempt)
        => attempt < AuthorizationRetryAttempts
            && (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden);

    private static TimeSpan GetAuthorizationRetryDelay(int attempt)
        => TimeSpan.FromSeconds(attempt);

    private static ValueTask DelayAsyncDefault(TimeSpan delay, CancellationToken cancellationToken)
        => new(Task.Delay(delay, cancellationToken));

    private static string? TryGetEtag(HttpResponseMessage response)
    {
        if (response.Headers.ETag is not null)
        {
            return response.Headers.ETag.ToString();
        }

        if (response.Headers.TryGetValues("ETag", out var values))
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
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

    private static Uri BuildSubscriptionRuleUri(ServiceBusTopicsCredentials credentials, string namespaceFqdn, string topicName, string subscriptionName, string ruleName)
    {
        var root = ResolveManagementRoot(credentials, namespaceFqdn);
        return new Uri(
            $"{root}/{Uri.EscapeDataString(topicName)}/subscriptions/{Uri.EscapeDataString(subscriptionName)}/rules/{Uri.EscapeDataString(ruleName)}?api-version={ApiVersion}",
            UriKind.Absolute);
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
public sealed record ServiceBusTopicDescription(
    string TopicName,
    int SubscriptionCount,
    bool RequiresDuplicateDetection,
    string? UserMetadata = null,
    IReadOnlyList<ServiceBusTopicProperty>? Properties = null);
public sealed record ServiceBusSubscriptionPage(IReadOnlyList<ServiceBusSubscriptionDescription> Subscriptions);
public sealed record ServiceBusSubscriptionDescription(
    string SubscriptionName,
    string? UserMetadata,
    string LockDuration,
    int MaxDeliveryCount,
    string AutoDeleteOnIdle,
    string? ETag = null,
    IReadOnlyList<ServiceBusSubscriptionProperty>? Properties = null,
    bool HasDefaultRuleDescription = false);

public sealed record ServiceBusTopicProperty(string LocalName, string Xml);
public sealed record ServiceBusSubscriptionProperty(string LocalName, string Xml);
public sealed record ServiceBusSubscriptionRuleDescription(string RuleName, string? SqlExpression);

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
