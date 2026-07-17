using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SubscribeHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        SnsParseResult parseResult,
        ServiceBusTopicsCredentials credentials,
        IServiceBusTopicsManagementClient managementClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(managementClient);
        ArgumentNullException.ThrowIfNull(logger);

        if (!SnsSubscriptionSupport.TryParseSubscribeRequest(parseResult.Parameters, out var request, out var error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var subscriptionId = SnsSubscriptionSupport.CreateSubscriptionId(request.TopicArn, request.Protocol, request.Endpoint);
        var subscriptionArn = SnsSubscriptionSupport.BuildSubscriptionArn(context, request.TopicName, subscriptionId);

        try
        {
            await managementClient.CreateSubscriptionAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    request.TopicName,
                    subscriptionId,
                    request.UserMetadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ServiceBusTopicsManagementException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            ServiceBusSubscriptionDescription? existing;
            try
            {
                existing = await managementClient.GetSubscriptionAsync(
                        credentials,
                        SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                        request.TopicName,
                        subscriptionId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ServiceBusTopicsManagementException getException)
            {
                await SnsTopicSupport.WriteManagementErrorAsync(context, getException).ConfigureAwait(false);
                return;
            }

            if (!SnsSubscriptionSupport.MetadataMatches(existing?.UserMetadata, request.Metadata))
            {
                SnsLog.MismatchedExistingSubscription(logger, request.TopicName, subscriptionId, request.Protocol, request.Endpoint);
            }
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        await SnsResponseWriter.WriteSubscribeResponseAsync(context, subscriptionArn).ConfigureAwait(false);
    }
}
