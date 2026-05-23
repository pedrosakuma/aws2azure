using System.Collections.Generic;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class GetSubscriptionAttributesHandler
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
            || !SnsSubscriptionSupport.TryParseSubscriptionArn(subscriptionArn, out var topicName, out var subscriptionId, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        ServiceBusSubscriptionDescription? subscription;
        try
        {
            subscription = await managementClient.GetSubscriptionAsync(
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

        if (subscription is null)
        {
            await SnsTopicSupport.WriteNotFoundAsync(context, $"Subscription does not exist: {subscriptionArn}").ConfigureAwait(false);
            return;
        }

        var arnParts = subscriptionArn.Split(':', 7, StringSplitOptions.None);
        var topicArn = string.Join(':', arnParts, 0, 6);
        var accountId = arnParts[4];
        var metadata = SnsSubscriptionSupport.ParseMetadata(subscription.UserMetadata);
        var filterPolicyJson = string.IsNullOrWhiteSpace(metadata.FilterPolicyJson) ? null : metadata.FilterPolicyJson;
        var filterPolicyScope = filterPolicyJson is null
            ? null
            : string.IsNullOrWhiteSpace(metadata.FilterPolicyScope)
                ? SnsSubscriptionMetadata.MessageAttributesScope
                : metadata.FilterPolicyScope;

        var attributes = new List<KeyValuePair<string, string>>(10)
        {
            new("SubscriptionArn", subscriptionArn),
            new("TopicArn", topicArn),
            new("Owner", accountId),
            new("Protocol", metadata.Protocol),
            new("Endpoint", metadata.Endpoint),
            new("ConfirmationWasAuthenticated", "true"),
            new("PendingConfirmation", "false"),
            new("RawMessageDelivery", metadata.RawDeliveryEnabled ? "true" : "false"),
        };

        if (filterPolicyJson is not null)
        {
            attributes.Add(new("FilterPolicy", filterPolicyJson));
            attributes.Add(new("FilterPolicyScope", filterPolicyScope!));
        }

        await SnsResponseWriter.WriteAttributesResponseAsync(context, "GetSubscriptionAttributes", attributes).ConfigureAwait(false);
    }
}
