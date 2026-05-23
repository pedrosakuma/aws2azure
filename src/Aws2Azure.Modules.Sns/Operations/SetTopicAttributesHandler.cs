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
            case "EffectiveDeliveryPolicy":
            case "KmsMasterKeyId":
            case "SignatureVersion":
            case "TracingConfig":
                await SnsResponseWriter.WriteMetadataOnlyResponseAsync(context, "SetTopicAttributes").ConfigureAwait(false);
                return;
            case "ContentBasedDeduplication":
                break;
            default:
                await SnsTopicSupport.WriteInvalidParameterAsync(context, $"Invalid attribute name: {attributeName}").ConfigureAwait(false);
                return;
        }

        if (!SnsSubscriptionSupport.TryParseBooleanAttribute(attributeValue, out var requestedValue))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, "Attribute 'ContentBasedDeduplication' must be a boolean value ('true' or 'false').").ConfigureAwait(false);
            return;
        }

        ServiceBusTopicDescription? topic;
        try
        {
            topic = await managementClient.GetTopicAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    topicName,
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

        if (topic.RequiresDuplicateDetection != requestedValue)
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
