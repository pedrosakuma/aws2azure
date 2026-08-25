using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SetTopicAttributesHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        SnsParseResult parseResult,
        ServiceBusTopicsCredentials credentials,
        IServiceBusTopicsManagementClient managementClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(managementClient);

        if (!SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "TopicArn", out var topicArn, out var error)
            || !SnsTopicSupport.TryParseTopicArn(topicArn, out var topicName, out error)
            || !SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "AttributeName", out var attributeName, out error)
            || !SnsTopicSupport.TryGetParameter(parseResult.Parameters, "AttributeValue", out var attributeValue, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        switch (attributeName)
        {
            case "DisplayName":
            case "Policy":
            case "DeliveryPolicy":
            case "ContentBasedDeduplication":
                break;
            case "EffectiveDeliveryPolicy":
            case "KmsMasterKeyId":
            case "SignatureVersion":
            case "TracingConfig":
                await SnsResponseWriter.WriteMetadataOnlyResponseAsync(context, "SetTopicAttributes").ConfigureAwait(false);
                return;
            default:
                await SnsTopicSupport.WriteInvalidParameterAsync(context, $"Invalid attribute name: {attributeName}").ConfigureAwait(false);
                return;
        }

        var serviceBusTopicName = SnsTopicRouting.ResolveServiceBusTopicName(credentials, topicName);
        if (!await SnsTopicOwnershipSupport.EnsureTopicOwnershipAsync(
                context,
                credentials,
                managementClient,
                topicName,
                serviceBusTopicName,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        ServiceBusTopicDescription? topic;
        try
        {
            topic = await managementClient.GetTopicAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    serviceBusTopicName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        if (topic is null)
        {
            await SnsTopicSupport.WriteNotFoundAsync(context, $"Topic does not exist: {topicArn}").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(attributeName, "ContentBasedDeduplication", StringComparison.Ordinal))
        {
            var metadata = SnsTopicAttributeSupport.ParseMetadata(topic.UserMetadata);
            switch (attributeName)
            {
                case "DisplayName":
                    metadata.DisplayName = string.IsNullOrWhiteSpace(attributeValue) ? null : attributeValue;
                    break;
                case "Policy":
                    metadata.PolicyJson = string.IsNullOrWhiteSpace(attributeValue)
                        ? null
                        : SnsSubscriptionSupport.NormalizeJsonAttribute(attributeValue, "Policy", out error);
                    if (error is not null)
                    {
                        await SnsTopicSupport.WriteInvalidParameterAsync(context, error).ConfigureAwait(false);
                        return;
                    }

                    break;
                case "DeliveryPolicy":
                    metadata.DeliveryPolicyJson = string.IsNullOrWhiteSpace(attributeValue)
                        ? null
                        : SnsSubscriptionSupport.NormalizeJsonAttribute(attributeValue, "DeliveryPolicy", out error);
                    if (error is not null)
                    {
                        await SnsTopicSupport.WriteInvalidParameterAsync(context, error).ConfigureAwait(false);
                        return;
                    }

                    break;
            }

            if (!SnsTopicAttributeSupport.TryBuildUserMetadata(metadata, out var serializedMetadata))
            {
                await SnsTopicSupport.WriteInvalidParameterAsync(
                        context,
                        $"Topic metadata exceeds the Azure Service Bus UserMetadata limit of {SnsTopicAttributeSupport.UserMetadataMaxLength} characters.")
                    .ConfigureAwait(false);
                return;
            }

            var updatedTopic = topic with
            {
                UserMetadata = serializedMetadata,
            };

            try
            {
                await managementClient.UpdateTopicAsync(
                        credentials,
                        SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                        updatedTopic,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ServiceBusTopicsManagementException ex)
            {
                await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
                return;
            }

            await SnsResponseWriter.WriteMetadataOnlyResponseAsync(context, "SetTopicAttributes").ConfigureAwait(false);
            return;
        }

        if (!SnsSubscriptionSupport.TryParseBooleanAttribute(attributeValue, out var requestedValue))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, "Attribute 'ContentBasedDeduplication' must be a boolean value ('true' or 'false').").ConfigureAwait(false);
            return;
        }

        var currentMetadata = SnsTopicAttributeSupport.ParseMetadata(topic.UserMetadata);
        var isFifoTopic = topicName.EndsWith(".fifo", StringComparison.Ordinal);
        var currentValue = currentMetadata.ContentBasedDeduplication;
        if (currentValue is null
            && SnsFifoPublishSupport.TryGetCachedServiceBusTopicState(credentials, serviceBusTopicName, out var cachedState))
        {
            currentValue = cachedState.ContentBasedDeduplication;
        }

        currentValue ??= isFifoTopic && topic.RequiresDuplicateDetection;
        if (currentValue != requestedValue)
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(
                    context,
                    "Attribute 'ContentBasedDeduplication' cannot be changed after the Service Bus topic has been created.")
                .ConfigureAwait(false);
            return;
        }

        await SnsResponseWriter.WriteMetadataOnlyResponseAsync(context, "SetTopicAttributes").ConfigureAwait(false);
    }
}
