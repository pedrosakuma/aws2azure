using System.Collections.Generic;
using System.Globalization;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class GetTopicAttributesHandler
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
            || !SnsTopicSupport.TryParseTopicArn(topicArn, out var topicName, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
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

        var arnParts = topicArn.Split(':', 6, StringSplitOptions.None);
        var accountId = arnParts[4];
        var metadata = SnsTopicAttributeSupport.ParseMetadata(topic.UserMetadata);
        var isFifo = metadata.FifoTopic ?? topicName.EndsWith(".fifo", StringComparison.Ordinal);
        var contentBasedDeduplication = metadata.ContentBasedDeduplication;
        if (contentBasedDeduplication is null
            && SnsFifoPublishSupport.TryGetCachedServiceBusTopicState(credentials, serviceBusTopicName, out var cachedState))
        {
            contentBasedDeduplication = cachedState.ContentBasedDeduplication;
        }

        contentBasedDeduplication ??= isFifo && topic.RequiresDuplicateDetection;

        var attributes = new List<KeyValuePair<string, string>>(12)
        {
            new("TopicArn", topicArn),
            new("Owner", accountId),
            new("DisplayName", metadata.DisplayName ?? string.Empty),
            new("Policy", metadata.PolicyJson ?? "{}"),
            new("SubscriptionsConfirmed", topic.SubscriptionCount.ToString(CultureInfo.InvariantCulture)),
            new("SubscriptionsPending", "0"),
            new("SubscriptionsDeleted", "0"),
            new("KmsMasterKeyId", string.Empty),
            new("FifoTopic", isFifo ? "true" : "false"),
            new("ContentBasedDeduplication", contentBasedDeduplication.Value ? "true" : "false"),
        };

        if (!string.IsNullOrWhiteSpace(metadata.DeliveryPolicyJson))
        {
            attributes.Add(new("DeliveryPolicy", metadata.DeliveryPolicyJson));
            attributes.Add(new("EffectiveDeliveryPolicy", metadata.DeliveryPolicyJson));
        }

        await SnsResponseWriter.WriteAttributesResponseAsync(context, "GetTopicAttributes", attributes).ConfigureAwait(false);
    }
}
