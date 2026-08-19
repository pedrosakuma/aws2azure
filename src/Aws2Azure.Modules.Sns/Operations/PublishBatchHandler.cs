using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class PublishBatchHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        SnsParseResult parseResult,
        ServiceBusTopicsCredentials? credentials,
        EventGridCredentials? eventGridCredentials,
        SnsSettings snsSettings,
        IServiceBusTopicsManagementClient managementClient,
        ISnsAmqpSender amqpSender,
        IEventGridPublisher eventGridPublisher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(snsSettings);
        ArgumentNullException.ThrowIfNull(managementClient);
        ArgumentNullException.ThrowIfNull(amqpSender);
        ArgumentNullException.ThrowIfNull(eventGridPublisher);

        if (!SnsPublishSupport.TryParsePublishBatchRequest(parseResult.Parameters, out var topicArn, out var topicName, out var entries, out var error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var messageIds = new Guid[entries.Count];
        var route = SnsTopicRouting.Resolve(credentials, snsSettings, topicName);
        var hasFifoFields = false;
        for (var i = 0; i < entries.Count; i++)
        {
            if (SnsFifoPublishSupport.HasFifoFields(entries[i]))
            {
                hasFifoFields = true;
                break;
            }
        }

        if (route.Backend == SnsTopicBackend.EventGrid
            && !SnsFifoPublishSupport.TryValidateEventGridRequest(topicName, hasFifoFields, out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var brokerMessageIds = new string[entries.Count];
        if (route.Backend == SnsTopicBackend.ServiceBusTopics
            && (topicName.EndsWith(".fifo", StringComparison.Ordinal) || hasFifoFields))
        {
            var serviceBusCredentials = credentials
                ?? throw new InvalidOperationException(
                    "Service Bus SNS routing requires Service Bus Topics credentials.");
            ServiceBusFifoTopicState topicState;
            try
            {
                topicState = await SnsFifoPublishSupport.GetServiceBusTopicStateAsync(
                        topicArn,
                        topicName,
                        route.ServiceBusTopicName,
                        serviceBusCredentials,
                        managementClient,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SnsFifoPublishValidationException ex) when (ex.FailureType == ValidationFailureType.NotFound)
            {
                await SnsTopicSupport.WriteNotFoundAsync(context, ex.Message).ConfigureAwait(false);
                return;
            }
            catch (ServiceBusTopicsManagementException ex)
            {
                await SnsTopicSupport.WriteManagementErrorAsync(context, ex).ConfigureAwait(false);
                return;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                if (!SnsFifoPublishSupport.TryResolveBrokerMessageId(
                        topicState,
                        entries[i].MessageGroupId,
                        entries[i].MessageDeduplicationId,
                        entries[i].Message,
                        out brokerMessageIds[i],
                        out error))
                {
                    await SnsTopicSupport.WriteInvalidParameterAsync(context, $"Entry {i + 1}: {error}").ConfigureAwait(false);
                    return;
                }
            }
        }
        else
        {
            for (var i = 0; i < brokerMessageIds.Length; i++)
            {
                brokerMessageIds[i] = string.Empty;
            }
        }

        var publisher = SnsBackendPublisherFactory.Create(
            route, credentials, eventGridCredentials, amqpSender, eventGridPublisher);

        var sendResult = await publisher.PublishBatchAsync(topicArn, entries, messageIds, brokerMessageIds, cancellationToken).ConfigureAwait(false);

        var successful = new List<PublishBatchSuccess>(entries.Count);
        var failedEntries = new List<PublishBatchFailure>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var outcome = sendResult.Outcomes[i];
            if (outcome.Succeeded)
            {
                successful.Add(new PublishBatchSuccess(entries[i].Id, messageIds[i].ToString()));
                continue;
            }

            failedEntries.Add(new PublishBatchFailure(
                entries[i].Id,
                outcome.ErrorCode ?? "InternalFailure",
                outcome.ErrorMessage ?? "Azure publish failed.",
                outcome.SenderFault));
        }

        await SnsResponseWriter.WritePublishBatchResponseAsync(context, new PublishBatchResult(successful, failedEntries)).ConfigureAwait(false);
    }
}
