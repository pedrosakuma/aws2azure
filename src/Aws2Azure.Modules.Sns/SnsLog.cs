using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Sns;

internal static partial class SnsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "SNS Event Grid backend ignored FIFO fields for topic '{TopicArn}' and generated message id '{MessageId}'.")]
    public static partial void IgnoringFifoFields(ILogger logger, string topicArn, Guid messageId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Azure Event Grid publish to '{Endpoint}' failed with HTTP {StatusCode}. Body: {Body}")]
    public static partial void PublishHttpFailed(ILogger logger, string endpoint, int statusCode, string body);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Azure Event Grid publish to '{Endpoint}' failed at transport layer.")]
    public static partial void PublishTransportFailed(ILogger logger, string endpoint, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Azure Event Grid publish to '{Endpoint}' timed out.")]
    public static partial void PublishTimedOut(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Azure Event Grid publish to '{Endpoint}' failed during authentication.")]
    public static partial void PublishAuthFailed(ILogger logger, string endpoint, Exception exception);

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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Existing Service Bus subscription '{SubscriptionId}' for topic '{TopicName}' did not match requested SNS subscriber protocol '{Protocol}' and endpoint '{Endpoint}'. Returning the existing ARN without replacing metadata.")]
    public static partial void MismatchedExistingSubscription(ILogger logger, string topicName, string subscriptionId, string protocol, string endpoint);
}
