using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;

namespace Aws2Azure.Modules.Sns.Operations;

internal readonly record struct ServiceBusFifoTopicBackendState(
    bool RequiresDuplicateDetection,
    bool? ContentBasedDeduplication,
    string? FifoThroughputScope);

internal readonly record struct ServiceBusFifoTopicState(
    bool IsFifoTopic,
    bool RequiresDuplicateDetection,
    bool ContentBasedDeduplication,
    string FifoThroughputScope);

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
        bool? contentBasedDeduplication,
        string? fifoThroughputScope = null)
    {
        if (string.IsNullOrWhiteSpace(serviceBusTopicName))
        {
            return;
        }

        TopicStateCache[CreateCacheKey(credentials, serviceBusTopicName)] =
            new ServiceBusFifoTopicBackendState(
                requiresDuplicateDetection,
                contentBasedDeduplication,
                fifoThroughputScope);
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
            metadata.ContentBasedDeduplication,
            metadata.FifoThroughputScope);
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
        out string effectiveDeduplicationId,
        out string? error)
    {
        if (!topicState.IsFifoTopic)
        {
            if (HasFifoFields(messageGroupId, messageDeduplicationId))
            {
                brokerMessageId = string.Empty;
                effectiveDeduplicationId = string.Empty;
                error = "MessageGroupId and MessageDeduplicationId are supported only for FIFO topics whose names end with '.fifo'.";
                return false;
            }

            brokerMessageId = string.Empty;
            effectiveDeduplicationId = string.Empty;
            error = null;
            return true;
        }

        if (string.IsNullOrEmpty(messageGroupId))
        {
            brokerMessageId = string.Empty;
            effectiveDeduplicationId = string.Empty;
            error = "Parameter 'MessageGroupId' is required for FIFO topic publishes.";
            return false;
        }

        if (!TryValidateFifoIdentifier("MessageGroupId", messageGroupId, out error))
        {
            brokerMessageId = string.Empty;
            effectiveDeduplicationId = string.Empty;
            return false;
        }

        if (!topicState.RequiresDuplicateDetection)
        {
            brokerMessageId = string.Empty;
            effectiveDeduplicationId = string.Empty;
            error = "The FIFO topic is not provisioned with Service Bus duplicate detection, so SNS FIFO deduplication cannot be honoured.";
            return false;
        }

        if (!string.IsNullOrEmpty(messageDeduplicationId))
        {
            if (!TryValidateFifoIdentifier("MessageDeduplicationId", messageDeduplicationId, out error))
            {
                brokerMessageId = string.Empty;
                effectiveDeduplicationId = string.Empty;
                return false;
            }

            effectiveDeduplicationId = messageDeduplicationId;
        }
        else
        {
            if (!topicState.ContentBasedDeduplication)
            {
                brokerMessageId = string.Empty;
                effectiveDeduplicationId = string.Empty;
                error = "Parameter 'MessageDeduplicationId' is required for FIFO topic publishes when ContentBasedDeduplication is not enabled.";
                return false;
            }

            effectiveDeduplicationId = CreateContentBasedDeduplicationId(messageBody);
        }

        brokerMessageId = string.Equals(
            topicState.FifoThroughputScope,
            SnsTopicAttributeSupport.MessageGroupFifoThroughputScope,
            StringComparison.Ordinal)
            ? CreateMessageGroupScopedBrokerMessageId(messageGroupId, effectiveDeduplicationId)
            : effectiveDeduplicationId;
        error = null;
        return true;
    }

    private static bool HasFifoFields(string? messageGroupId, string? messageDeduplicationId)
        => !string.IsNullOrEmpty(messageGroupId)
            || !string.IsNullOrEmpty(messageDeduplicationId);

    private static ServiceBusFifoTopicState CreateTopicState(
        string snsTopicName,
        ServiceBusFifoTopicBackendState backendState)
    {
        var isFifoTopic = snsTopicName.EndsWith(".fifo", StringComparison.Ordinal);
        var contentBasedDeduplication = backendState.ContentBasedDeduplication
            ?? (isFifoTopic && backendState.RequiresDuplicateDetection);
        var fifoThroughputScope = SnsTopicAttributeSupport.TryParseFifoThroughputScope(
            backendState.FifoThroughputScope,
            out var parsedScope)
            ? parsedScope
            : SnsTopicAttributeSupport.TopicFifoThroughputScope;
        return new ServiceBusFifoTopicState(
            isFifoTopic,
            backendState.RequiresDuplicateDetection,
            contentBasedDeduplication,
            fifoThroughputScope);
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

    private static string CreateMessageGroupScopedBrokerMessageId(
        string messageGroupId,
        string deduplicationId)
    {
        Span<byte> composite = stackalloc byte[4 + 128 + 128];
        BinaryPrimitives.WriteUInt16BigEndian(composite, (ushort)messageGroupId.Length);
        for (var i = 0; i < messageGroupId.Length; i++)
        {
            composite[2 + i] = (byte)messageGroupId[i];
        }

        var deduplicationLengthOffset = 2 + messageGroupId.Length;
        BinaryPrimitives.WriteUInt16BigEndian(
            composite[deduplicationLengthOffset..],
            (ushort)deduplicationId.Length);
        var deduplicationOffset = deduplicationLengthOffset + 2;
        for (var i = 0; i < deduplicationId.Length; i++)
        {
            composite[deduplicationOffset + i] = (byte)deduplicationId[i];
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(
            composite[..(deduplicationOffset + deduplicationId.Length)],
            hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryValidateFifoIdentifier(
        string parameterName,
        string value,
        out string? error)
    {
        if (value.Length is < 1 or > 128)
        {
            error = $"Parameter '{parameterName}' must contain between 1 and 128 characters.";
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is < '!' or > '~')
            {
                error = $"Parameter '{parameterName}' may contain only ASCII alphanumeric characters and punctuation.";
                return false;
            }
        }

        error = null;
        return true;
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
