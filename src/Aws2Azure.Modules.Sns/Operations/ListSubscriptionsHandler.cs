using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class ListSubscriptionsHandler
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

        var topicSkip = 0;
        var subscriptionSkipWithinTopic = 0;
        if (parseResult.Parameters.TryGetValue("NextToken", out var nextToken)
            && !string.IsNullOrWhiteSpace(nextToken))
        {
            if (!SnsSubscriptionSupport.TryDecodeNextToken(nextToken, out var token))
            {
                await SnsTopicSupport.WriteInvalidParameterAsync(context, "Parameter 'NextToken' was not valid.").ConfigureAwait(false);
                return;
            }

            topicSkip = token.TopicSkip;
            subscriptionSkipWithinTopic = token.SubscriptionSkipWithinTopic;
        }

        ServiceBusTopicPage topicPage;
        try
        {
            topicPage = await managementClient.ListTopicsAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    topicSkip,
                    SnsTopicSupport.ListTopicsPageSize,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        var subscriptions = new List<ListedSubscription>(SnsSubscriptionSupport.ListSubscriptionsPageSize);
        var consumedTopics = 0;
        for (var i = 0; i < topicPage.TopicNames.Count && subscriptions.Count < SnsSubscriptionSupport.ListSubscriptionsPageSize; i++)
        {
            var topicName = topicPage.TopicNames[i];
            var skip = i == 0 ? subscriptionSkipWithinTopic : 0;
            ServiceBusSubscriptionPage subscriptionPage;
            try
            {
                subscriptionPage = await managementClient.ListSubscriptionsAsync(
                        credentials,
                        SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                        topicName,
                        skip,
                        SnsSubscriptionSupport.ListSubscriptionsPageSize - subscriptions.Count,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ServiceBusTopicsManagementException ex)
            {
                await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
                return;
            }

            for (var subIndex = 0; subIndex < subscriptionPage.Subscriptions.Count; subIndex++)
            {
                var item = subscriptionPage.Subscriptions[subIndex];
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

                subscriptions.Add(SnsSubscriptionSupport.ToListedSubscription(context, topicName, item.SubscriptionName, userMetadata));
            }

            if (subscriptions.Count == SnsSubscriptionSupport.ListSubscriptionsPageSize)
            {
                var responseNextToken = SnsSubscriptionSupport.EncodeNextToken(topicSkip + i, skip + subscriptionPage.Subscriptions.Count);
                await SnsResponseWriter.WriteListSubscriptionsResponseAsync(context, subscriptions, responseNextToken, byTopic: false).ConfigureAwait(false);
                return;
            }

            consumedTopics++;
        }

        string? responseToken = null;
        if (topicPage.TopicNames.Count == SnsTopicSupport.ListTopicsPageSize)
        {
            responseToken = SnsSubscriptionSupport.EncodeNextToken(topicSkip + consumedTopics, 0);
        }

        await SnsResponseWriter.WriteListSubscriptionsResponseAsync(context, subscriptions, responseToken, byTopic: false).ConfigureAwait(false);
    }
}
