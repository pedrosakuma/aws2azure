using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;

namespace Aws2Azure.Modules.Sns.Operations;

internal readonly record struct ServiceBusFifoTopicBackendState(
    bool RequiresDuplicateDetection,
    bool? ContentBasedDeduplication);

internal readonly record struct ServiceBusFifoTopicState(
    bool IsFifoTopic,
    bool RequiresDuplicateDetection,
    bool ContentBasedDeduplication);

internal static class SnsFifoPublishSupport
{
    private static readonly ConcurrentDictionary<string, ServiceBusFifoTopicBackendState> TopicStateCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool HasFifoFields(PublishRequest request)
        => HasFifoFields(request.MessageGroupId, request.MessageDeduplicationId);

    public static bool HasFifoFields(PublishBatchRequestEntry request)
        => HasFifoFields(request.MessageGroupId, request.MessageDeduplicationId);

    public static void InvalidateServiceBusTopicState(ServiceBusTopicsCredentials credentials, string serviceBusTopicName)
    {
        if (!string.IsNullOrWhiteSpace(serviceBusTopicName))
        {
            TopicStateCache.TryRemove(CreateCacheKey(credentials, serviceBusTopicName), out _);
        }
    }

    public static void RecordServiceBusTopicState(
        ServiceBusTopicsCredentials credentials,
        string serviceBusTopicName,
        bool requiresDuplicateDetection,
        bool? contentBasedDeduplication)
    {
        if (string.IsNullOrWhiteSpace(serviceBusTopicName))
        {
            return;
        }

        TopicStateCache[CreateCacheKey(credentials, serviceBusTopicName)] =
            new ServiceBusFifoTopicBackendState(requiresDuplicateDetection, contentBasedDeduplication);
    }

    public static bool TryGetCachedServiceBusTopicState(
        ServiceBusTopicsCredentials credentials,
        string serviceBusTopicName,
        out ServiceBusFifoTopicBackendState backendState)
        => TopicStateCache.TryGetValue(CreateCacheKey(credentials, serviceBusTopicName), out backendState);

    public static async ValueTask<ServiceBusFifoTopicState> GetServiceBusTopicStateAsync(
        string topicArn,
        string snsTopicName,
        string serviceBusTopicName,
        ServiceBusTopicsCredentials credentials,
        IServiceBusTopicsManagementClient managementClient,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateCacheKey(credentials, serviceBusTopicName);
        if (TopicStateCache.TryGetValue(cacheKey, out var cachedState))
        {
            return CreateTopicState(snsTopicName, cachedState);
        }

        var topic = await managementClient.GetTopicAsync(
                credentials,
                SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                serviceBusTopicName,
                cancellationToken)
            .ConfigureAwait(false);

        if (topic is null)
        {
            throw new SnsFifoPublishValidationException(
                ValidationFailureType.NotFound,
                $"Topic does not exist: {topicArn}");
        }

        var metadata = SnsTopicAttributeSupport.ParseMetadata(topic.UserMetadata);
        var backendState = new ServiceBusFifoTopicBackendState(
            topic.RequiresDuplicateDetection,
            metadata.ContentBasedDeduplication);
        TopicStateCache[cacheKey] = backendState;
        return CreateTopicState(snsTopicName, backendState);
    }

    public static bool TryValidateEventGridRequest(
        string topicName,
        bool hasFifoFields,
        out string? error)
    {
        if (topicName.EndsWith(".fifo", StringComparison.Ordinal))
        {
            error = "FIFO topics are supported only on the Service Bus backend; the Event Grid backend cannot honor SNS FIFO semantics.";
            return false;
        }

        if (hasFifoFields)
        {
            error = "MessageGroupId and MessageDeduplicationId are supported only for FIFO topics on the Service Bus backend.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryResolveBrokerMessageId(
        ServiceBusFifoTopicState topicState,
        string? messageGroupId,
        string? messageDeduplicationId,
        string messageBody,
        out string brokerMessageId,
        out string? error)
    {
        if (!topicState.IsFifoTopic)
        {
            if (HasFifoFields(messageGroupId, messageDeduplicationId))
            {
                brokerMessageId = string.Empty;
                error = "MessageGroupId and MessageDeduplicationId are supported only for FIFO topics whose names end with '.fifo'.";
                return false;
            }

            brokerMessageId = string.Empty;
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(messageGroupId))
        {
            brokerMessageId = string.Empty;
            error = "Parameter 'MessageGroupId' is required for FIFO topic publishes.";
            return false;
        }

        if (!topicState.RequiresDuplicateDetection)
        {
            brokerMessageId = string.Empty;
            error = "The FIFO topic is not provisioned with Service Bus duplicate detection, so SNS FIFO deduplication cannot be honoured.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(messageDeduplicationId))
        {
            brokerMessageId = messageDeduplicationId;
            error = null;
            return true;
        }

        if (!topicState.ContentBasedDeduplication)
        {
            brokerMessageId = string.Empty;
            error = "Parameter 'MessageDeduplicationId' is required for FIFO topic publishes when ContentBasedDeduplication is not enabled.";
            return false;
        }

        brokerMessageId = CreateContentBasedDeduplicationId(messageBody);
        error = null;
        return true;
    }

    private static bool HasFifoFields(string? messageGroupId, string? messageDeduplicationId)
        => !string.IsNullOrWhiteSpace(messageGroupId)
            || !string.IsNullOrWhiteSpace(messageDeduplicationId);

    private static ServiceBusFifoTopicState CreateTopicState(
        string snsTopicName,
        ServiceBusFifoTopicBackendState backendState)
    {
        var isFifoTopic = snsTopicName.EndsWith(".fifo", StringComparison.Ordinal);
        var contentBasedDeduplication = backendState.ContentBasedDeduplication
            ?? (isFifoTopic && backendState.RequiresDuplicateDetection);
        return new ServiceBusFifoTopicState(
            isFifoTopic,
            backendState.RequiresDuplicateDetection,
            contentBasedDeduplication);
    }

    private static string CreateCacheKey(ServiceBusTopicsCredentials credentials, string serviceBusTopicName)
        => string.Create(
            SnsTopicSupport.ResolveNamespaceFqdn(credentials).Length + serviceBusTopicName.Length + 1,
            (Namespace: SnsTopicSupport.ResolveNamespaceFqdn(credentials), TopicName: serviceBusTopicName),
            static (buffer, state) =>
            {
                state.Namespace.AsSpan().CopyTo(buffer);
                buffer[state.Namespace.Length] = '|';
                state.TopicName.AsSpan().CopyTo(buffer[(state.Namespace.Length + 1)..]);
            });

    private static string CreateContentBasedDeduplicationId(string messageBody)
    {
        var bytes = Encoding.UTF8.GetBytes(messageBody);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal enum ValidationFailureType
{
    InvalidParameter = 0,
    NotFound,
}

internal sealed class SnsFifoPublishValidationException(ValidationFailureType failureType, string message)
    : Exception(message)
{
    public ValidationFailureType FailureType { get; } = failureType;
}
