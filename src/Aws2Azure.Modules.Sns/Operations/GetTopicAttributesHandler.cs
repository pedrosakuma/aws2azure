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

        var arnParts = topicArn.Split(':', 6, StringSplitOptions.None);
        var accountId = arnParts[4];
        var isFifo = topicName.EndsWith(".fifo", StringComparison.Ordinal) || topic.RequiresDuplicateDetection;

        var attributes = new List<KeyValuePair<string, string>>(10)
        {
            new("TopicArn", topicArn),
            new("Owner", accountId),
            new("DisplayName", string.Empty),
            new("Policy", "{}"),
            new("SubscriptionsConfirmed", topic.SubscriptionCount.ToString(CultureInfo.InvariantCulture)),
            new("SubscriptionsPending", "0"),
            new("SubscriptionsDeleted", "0"),
            new("KmsMasterKeyId", string.Empty),
            new("FifoTopic", isFifo ? "true" : "false"),
            new("ContentBasedDeduplication", topic.RequiresDuplicateDetection ? "true" : "false"),
        };

        await SnsResponseWriter.WriteAttributesResponseAsync(context, "GetTopicAttributes", attributes).ConfigureAwait(false);
    }
}
