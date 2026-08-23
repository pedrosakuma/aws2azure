using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class CreateTopicHandler
{
    private const string SnsFifoDeduplicationWindow = "PT5M";

    public static async Task HandleAsync(
        HttpContext context,
        SnsParseResult parseResult,
        ServiceBusTopicsCredentials credentials,
        SnsSettings snsSettings,
        IServiceBusTopicsManagementClient managementClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(snsSettings);
        ArgumentNullException.ThrowIfNull(managementClient);

        if (!SnsTopicSupport.TryGetRequiredParameter(parseResult.Parameters, "Name", out var topicName, out var error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        if (!SnsTopicSupport.IsValidTopicName(topicName))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(
                    context,
                    "Parameter 'Name' must match the supported topic-name pattern [A-Za-z0-9_-]{1,256} or [A-Za-z0-9_-]{1,251}.fifo.")
                .ConfigureAwait(false);
            return;
        }

        if (!SnsTopicSupport.IsValidServiceBusTopicName(topicName))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(
                    context,
                    "Parameter 'Name' is a valid SNS topic name but is rejected by the underlying Azure Service Bus topic-path naming restriction: the name must start with a letter and end with a letter or digit (leading/trailing '-' or '_' is not allowed).")
                .ConfigureAwait(false);
            return;
        }

        if (!SnsTopicAttributeSupport.TryParseCreateTopicAttributes(parseResult.Parameters, topicName, out var attributes, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        if (attributes.IsFifoTopic
            && SnsTopicRouting.Resolve(credentials, snsSettings, topicName).Backend == SnsTopicBackend.EventGrid)
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(
                    context,
                    "FIFO topics are supported only when the resolved SNS backend is Service Bus; the Event Grid backend cannot honor SNS FIFO semantics.")
                .ConfigureAwait(false);
            return;
        }

        var namespaceFqdn = SnsTopicSupport.ResolveNamespaceFqdn(credentials);
        CreateTopicResult createResult;

        try
        {
            var description = new ServiceBusTopicDescription(
                topicName,
                SubscriptionCount: 0,
                RequiresDuplicateDetection: attributes.RequiresDuplicateDetection,
                UserMetadata: attributes.UserMetadata,
                DuplicateDetectionHistoryTimeWindow: attributes.IsFifoTopic ? SnsFifoDeduplicationWindow : null);

            if (managementClient is ServiceBusTopicsManagementClient concreteClient)
            {
                createResult = await concreteClient.CreateTopicDetailedAsync(
                        credentials,
                        namespaceFqdn,
                        description,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await managementClient.CreateTopicAsync(
                        credentials,
                        namespaceFqdn,
                        description,
                        cancellationToken)
                    .ConfigureAwait(false);
                createResult = CreateTopicResult.Unknown;
            }
        }
        catch (ServiceBusTopicsManagementException ex)
        {
            await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        if (createResult == CreateTopicResult.Created)
        {
            var metadata = SnsTopicAttributeSupport.ParseMetadata(attributes.UserMetadata);
            SnsFifoPublishSupport.RecordServiceBusTopicState(
                credentials,
                topicName,
                attributes.RequiresDuplicateDetection,
                metadata.ContentBasedDeduplication);
        }
        else
        {
            SnsFifoPublishSupport.InvalidateServiceBusTopicState(credentials, topicName);
        }

        await SnsResponseWriter.WriteCreateTopicResponseAsync(
                context,
                SnsTopicSupport.BuildTopicArn(context, topicName))
            .ConfigureAwait(false);
    }
}
