using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class ListSubscriptionsByTopicHandler
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
            || !SnsTopicSupport.TryParseTopicArnAllowFifo(topicArn, out var topicName, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var skip = 0;
        if (parseResult.Parameters.TryGetValue("NextToken", out var nextToken)
            && !string.IsNullOrWhiteSpace(nextToken))
        {
            if (!SnsSubscriptionSupport.TryDecodeNextToken(nextToken, out var token))
            {
                await SnsTopicSupport.WriteInvalidParameterAsync(context, "Parameter 'NextToken' was not valid.").ConfigureAwait(false);
                return;
            }

            skip = token.SubscriptionSkipWithinTopic;
        }

        ServiceBusSubscriptionPage page;
        try
        {
            page = await managementClient.ListSubscriptionsAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    topicName,
                    skip,
                    SnsSubscriptionSupport.ListSubscriptionsPageSize,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        var subscriptions = new ListedSubscription[page.Subscriptions.Count];
        for (var i = 0; i < page.Subscriptions.Count; i++)
        {
            var item = page.Subscriptions[i];
            var userMetadata = item.UserMetadata;
            if (string.IsNullOrWhiteSpace(userMetadata))
            {
                try
                {
                    userMetadata = (await managementClient.GetSubscriptionAsync(
                            credentials,
                            SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                            topicName,
                            item.SubscriptionName,
                            cancellationToken)
                        .ConfigureAwait(false))?.UserMetadata;
                }
                catch (ServiceBusTopicsManagementException ex)
                {
                    await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
                    return;
                }
            }

            subscriptions[i] = SnsSubscriptionSupport.ToListedSubscription(context, topicName, item.SubscriptionName, userMetadata);
        }

        var responseNextToken = page.Subscriptions.Count == SnsSubscriptionSupport.ListSubscriptionsPageSize
            ? SnsSubscriptionSupport.EncodeNextToken(0, skip + page.Subscriptions.Count)
            : null;

        await SnsResponseWriter.WriteListSubscriptionsResponseAsync(context, subscriptions, responseNextToken, byTopic: true).ConfigureAwait(false);
    }
}
