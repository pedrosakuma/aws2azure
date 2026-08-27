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

        var route = SnsTopicRouting.Resolve(credentials, snsSettings, topicName);
        if (!SnsTopicAttributeSupport.TryParseCreateTopicAttributes(parseResult.Parameters, topicName, out var attributes, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        if (attributes.IsFifoTopic
            && route.Backend == SnsTopicBackend.EventGrid)
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(
                    context,
                    "FIFO topics are supported only when the resolved SNS backend is Service Bus; the Event Grid backend cannot honor SNS FIFO semantics.")
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(route.ServiceBusTopicName, topicName, StringComparison.Ordinal))
        {
            var metadata = SnsTopicAttributeSupport.ParseMetadata(attributes.UserMetadata);
            metadata.SnsTopicName = topicName;
            if (!SnsTopicAttributeSupport.TryBuildUserMetadata(metadata, out var remappedUserMetadata))
            {
                await SnsTopicSupport.WriteInvalidParameterAsync(
                        context,
                        $"Topic metadata exceeds the Azure Service Bus UserMetadata limit of {SnsTopicAttributeSupport.UserMetadataMaxLength} characters.")
                    .ConfigureAwait(false);
                return;
            }

            attributes = attributes with { UserMetadata = remappedUserMetadata };
        }

        var namespaceFqdn = SnsTopicSupport.ResolveNamespaceFqdn(credentials);
        CreateTopicResult createResult;

        try
        {
            var description = new ServiceBusTopicDescription(
                route.ServiceBusTopicName,
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

        if (!string.Equals(route.ServiceBusTopicName, topicName, StringComparison.Ordinal)
            && createResult is CreateTopicResult.Conflict or CreateTopicResult.Ok)
        {
            ServiceBusTopicDescription? existingTopic;
            try
            {
                existingTopic = await managementClient.GetTopicAsync(
                        credentials,
                        namespaceFqdn,
                        route.ServiceBusTopicName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ServiceBusTopicsManagementException ex)
            {
                await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
                return;
            }

            var existingMetadata = SnsTopicAttributeSupport.ParseMetadata(existingTopic?.UserMetadata);
            if (!string.IsNullOrWhiteSpace(existingMetadata.SnsTopicName)
                && !string.Equals(existingMetadata.SnsTopicName, topicName, StringComparison.Ordinal))
            {
                await SnsTopicSupport.WriteInvalidParameterAsync(
                        context,
                        $"Configured ServiceBusTopicName '{route.ServiceBusTopicName}' is already bound to a different SNS topic.")
                    .ConfigureAwait(false);
                return;
            }
        }

        if (createResult == CreateTopicResult.Created)
        {
            var metadata = SnsTopicAttributeSupport.ParseMetadata(attributes.UserMetadata);
            SnsFifoPublishSupport.RecordServiceBusTopicState(
                credentials,
                route.ServiceBusTopicName,
                attributes.RequiresDuplicateDetection,
                metadata.ContentBasedDeduplication);
        }
        else
        {
            SnsFifoPublishSupport.InvalidateServiceBusTopicState(credentials, route.ServiceBusTopicName);
        }

        await SnsResponseWriter.WriteCreateTopicResponseAsync(
                context,
                SnsTopicSupport.BuildTopicArn(context, topicName))
            .ConfigureAwait(false);
    }
}
