using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class ConfirmSubscriptionHandler
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
            || !SnsTopicSupport.TryParseTopicArnAllowFifo(topicArn, out var topicName, out error)
            || !SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "Token", out var token, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        if (!SnsSubscriptionSupport.TryResolveConfirmSubscriptionArn(
                context,
                topicArn,
                topicName,
                token,
                out var subscriptionArn,
                out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        _ = SnsSubscriptionSupport.TryParseSubscriptionArn(
            subscriptionArn,
            out _,
            out var subscriptionId,
            out _);
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
            await SnsTopicSupport.WriteNotFoundAsync(
                context,
                $"Subscription does not exist: {subscriptionArn}").ConfigureAwait(false);
            return;
        }

        var metadata = SnsSubscriptionSupport.ParseMetadata(subscription.UserMetadata);
        var expectedSubscriptionId = SnsSubscriptionSupport.CreateSubscriptionId(
            topicArn,
            metadata.Protocol,
            metadata.Endpoint);
        if (!string.Equals(subscriptionId, expectedSubscriptionId, StringComparison.Ordinal))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(
                context,
                "Parameter 'Token' was not a valid auto-confirmed subscription token.").ConfigureAwait(false);
            return;
        }

        await SnsResponseWriter.WriteConfirmSubscriptionResponseAsync(context, subscriptionArn).ConfigureAwait(false);
    }
}
