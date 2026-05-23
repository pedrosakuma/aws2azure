using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class PublishBatchHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        SnsParseResult parseResult,
        ServiceBusTopicsCredentials credentials,
        EventGridCredentials? eventGridCredentials,
        SnsSettings snsSettings,
        ISnsAmqpSender amqpSender,
        IEventGridPublisher eventGridPublisher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(snsSettings);
        ArgumentNullException.ThrowIfNull(amqpSender);
        ArgumentNullException.ThrowIfNull(eventGridPublisher);

        if (!SnsPublishSupport.TryParsePublishBatchRequest(parseResult.Parameters, out var topicArn, out var topicName, out var entries, out var error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var messageIds = new Guid[entries.Count];
        SnsBatchSendResult sendResult;
        var route = SnsTopicRouting.Resolve(credentials, snsSettings, topicName);
        if (route.Backend == SnsTopicBackend.EventGrid)
        {
            var messages = new EventGridPublishMessage[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                messageIds[i] = Guid.NewGuid();
                messages[i] = SnsPublishSupport.CreateEventGridMessage(topicArn, entries[i], messageIds[i]);
            }

            var destination = SnsTopicRouting.ResolveEventGridDestination(route, eventGridCredentials);
            sendResult = await eventGridPublisher.PublishBatchAsync(destination, messages, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var messages = new SnsAmqpSendMessage[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                messageIds[i] = Guid.NewGuid();
                messages[i] = SnsPublishSupport.CreateAmqpMessage(entries[i], messageIds[i]);
            }

            try
            {
                sendResult = await amqpSender.SendBatchAsync(
                    credentials,
                    SnsTopicSupport.ResolveNamespaceFqdn(credentials),
                    route.ServiceBusTopicName,
                    messages,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SnsAmqpException exception)
            {
                var failure = SnsPublishErrorMapper.CreateBatchFailure(exception);
                var failed = new PublishBatchFailure[entries.Count];
                for (var i = 0; i < entries.Count; i++)
                {
                    failed[i] = new PublishBatchFailure(
                        entries[i].Id,
                        failure.ErrorCode ?? "InternalFailure",
                        failure.ErrorMessage ?? "Azure Service Bus Topics AMQP send failed.",
                        failure.SenderFault);
                }

                await SnsResponseWriter.WritePublishBatchResponseAsync(context, new PublishBatchResult([], failed)).ConfigureAwait(false);
                return;
            }
        }

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
