using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SetSubscriptionAttributesHandler
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

        if (!SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "SubscriptionArn", out var subscriptionArn, out var error)
            || !SnsSubscriptionSupport.TryParseSubscriptionArn(subscriptionArn, out var topicName, out var subscriptionId, out error)
            || !SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "AttributeName", out var attributeName, out error)
            || !SnsTopicSupport.TryGetParameter(parseResult.Parameters, "AttributeValue", out var attributeValue, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        switch (attributeName)
        {
            case "DeliveryPolicy":
            case "RedrivePolicy":
            case "SubscriptionRoleArn":
                await SnsResponseWriter.WriteMetadataOnlyResponseAsync(context, "SetSubscriptionAttributes").ConfigureAwait(false);
                return;
            case "FilterPolicy":
            case "FilterPolicyScope":
            case "RawMessageDelivery":
                break;
            default:
                await SnsTopicSupport.WriteInvalidParameterAsync(context, $"Invalid attribute name: {attributeName}").ConfigureAwait(false);
                return;
        }

        ServiceBusSubscriptionDescription? existingSubscription;
        try
        {
            existingSubscription = await managementClient.GetSubscriptionAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    topicName,
                    subscriptionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceBusTopicsManagementException ex)
        {
                await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        if (existingSubscription is null)
        {
            await SnsTopicSupport.WriteNotFoundAsync(context, $"Subscription does not exist: {subscriptionArn}").ConfigureAwait(false);
            return;
        }

        var metadata = SnsSubscriptionSupport.ParseMetadata(existingSubscription.UserMetadata);
        var previousMetadata = new SnsSubscriptionMetadata
        {
            Protocol = metadata.Protocol,
            Endpoint = metadata.Endpoint,
            FilterPolicyJson = metadata.FilterPolicyJson,
            FilterPolicyScope = metadata.FilterPolicyScope,
            RawDeliveryEnabled = metadata.RawDeliveryEnabled,
        };
        switch (attributeName)
        {
            case "FilterPolicy":
                if (string.IsNullOrWhiteSpace(attributeValue))
                {
                    metadata.FilterPolicyJson = null;
                    metadata.FilterPolicyScope = null;
                }
                else
                {
                    metadata.FilterPolicyJson = SnsSubscriptionSupport.NormalizeJsonAttribute(attributeValue, "FilterPolicy", out error);
                    if (error is not null)
                    {
                        await SnsTopicSupport.WriteInvalidParameterAsync(context, error).ConfigureAwait(false);
                        return;
                    }
                }

                if (metadata.FilterPolicyJson is not null && string.IsNullOrWhiteSpace(metadata.FilterPolicyScope))
                {
                    metadata.FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope;
                }
                break;
            case "FilterPolicyScope":
                if (string.Equals(attributeValue, SnsSubscriptionMetadata.MessageAttributesScope, StringComparison.OrdinalIgnoreCase))
                {
                    metadata.FilterPolicyScope = SnsSubscriptionMetadata.MessageAttributesScope;
                }
                else if (string.Equals(attributeValue, SnsSubscriptionMetadata.MessageBodyScope, StringComparison.OrdinalIgnoreCase))
                {
                    metadata.FilterPolicyScope = SnsSubscriptionMetadata.MessageBodyScope;
                }
                else
                {
                    await SnsTopicSupport.WriteInvalidParameterAsync(
                            context,
                            "Attribute 'FilterPolicyScope' must be 'MessageAttributes' or 'MessageBody'.")
                        .ConfigureAwait(false);
                    return;
                }
                break;
            case "RawMessageDelivery":
                if (!SnsSubscriptionSupport.TryParseBooleanAttribute(attributeValue, out var rawDeliveryEnabled))
                {
                    await SnsTopicSupport.WriteInvalidParameterAsync(
                            context,
                            "Attribute 'RawMessageDelivery' must be a boolean value ('true' or 'false').")
                        .ConfigureAwait(false);
                    return;
                }

                metadata.RawDeliveryEnabled = rawDeliveryEnabled;
                break;
        }

        if (!SnsSubscriptionSupport.TrySerializeMetadata(metadata, out var serializedMetadata))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(
                    context,
                    $"Subscription metadata exceeds the Azure Service Bus UserMetadata limit of {SnsSubscriptionSupport.UserMetadataMaxLength} characters.")
                .ConfigureAwait(false);
            return;
        }

        if (!SnsSubscriptionFilterSupport.TryBuildRuleDescription(metadata, out var ruleDescription, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var updatedSubscription = existingSubscription with
        {
            UserMetadata = serializedMetadata,
        };
        var applyingFirstFilter = !SnsSubscriptionRuleSupport.RequiresCustomRule(previousMetadata)
                                  && SnsSubscriptionRuleSupport.RequiresCustomRule(metadata);
        var updatedExistingCustomRule = false;
        var createdCustomRule = false;
        var deletedDefaultRule = false;

        try
        {
            await managementClient.UpdateSubscriptionAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    topicName,
                    updatedSubscription,
                    cancellationToken)
                .ConfigureAwait(false);
            if (applyingFirstFilter)
            {
                try
                {
                    await managementClient.PutSubscriptionRuleAsync(
                            credentials,
                            SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                            topicName,
                            subscriptionId,
                            ruleDescription,
                            updateExisting: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    updatedExistingCustomRule = true;
                }
                catch (ServiceBusTopicsManagementException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.PreconditionFailed)
                {
                    await managementClient.PutSubscriptionRuleAsync(
                            credentials,
                            SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                            topicName,
                            subscriptionId,
                            ruleDescription,
                            updateExisting: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    createdCustomRule = true;
                }

                await managementClient.DeleteSubscriptionRuleAsync(
                        credentials,
                        SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                        topicName,
                        subscriptionId,
                        SnsSubscriptionFilterSupport.DefaultRuleName,
                        cancellationToken)
                    .ConfigureAwait(false);
                deletedDefaultRule = true;
            }
            else if (SnsSubscriptionRuleSupport.RequiresCustomRule(previousMetadata)
                     || SnsSubscriptionRuleSupport.RequiresCustomRule(metadata))
            {
                await managementClient.PutSubscriptionRuleAsync(
                        credentials,
                        SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                        topicName,
                        subscriptionId,
                        SnsSubscriptionRuleSupport.RequiresCustomRule(metadata)
                            ? ruleDescription
                            : new ServiceBusSubscriptionRuleDescription(SnsSubscriptionFilterSupport.CustomRuleName, null),
                        updateExisting: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            if (!string.Equals(existingSubscription.UserMetadata, serializedMetadata, StringComparison.Ordinal))
            {
                try
                {
                    var currentSubscription = await managementClient.GetSubscriptionAsync(
                            credentials,
                            SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                            topicName,
                            subscriptionId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (currentSubscription is not null
                        && string.Equals(currentSubscription.UserMetadata, serializedMetadata, StringComparison.Ordinal))
                    {
                        await managementClient.UpdateSubscriptionAsync(
                                credentials,
                                SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                                topicName,
                                currentSubscription with { UserMetadata = existingSubscription.UserMetadata },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            }

            try
            {
                if (!deletedDefaultRule)
                {
                    if (createdCustomRule)
                    {
                        await managementClient.DeleteSubscriptionRuleAsync(
                                credentials,
                                SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                                topicName,
                                subscriptionId,
                                SnsSubscriptionFilterSupport.CustomRuleName,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (updatedExistingCustomRule)
                    {
                        await managementClient.PutSubscriptionRuleAsync(
                                credentials,
                                SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                                topicName,
                                subscriptionId,
                                new ServiceBusSubscriptionRuleDescription(SnsSubscriptionFilterSupport.CustomRuleName, null),
                                updateExisting: true,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch
            {
            }

            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        await SnsResponseWriter.WriteMetadataOnlyResponseAsync(context, "SetSubscriptionAttributes").ConfigureAwait(false);
    }
}
