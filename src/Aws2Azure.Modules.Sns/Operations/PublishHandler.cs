using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.WireProtocol;
using Aws2Azure.Modules.Sns.Xml;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class PublishHandler
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

        if (!SnsPublishSupport.TryParsePublishRequest(parseResult.Parameters, out var request, out var error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var messageId = Guid.NewGuid();
        var route = SnsTopicRouting.Resolve(credentials, snsSettings, request.TopicName);
        if (route.Backend == SnsTopicBackend.EventGrid
            && !SnsFifoPublishSupport.TryValidateEventGridRequest(request.TopicName, SnsFifoPublishSupport.HasFifoFields(request), out error))
        {
            await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
            return;
        }

        var brokerMessageId = messageId.ToString();
        if (route.Backend == SnsTopicBackend.ServiceBusTopics
            && (request.TopicName.EndsWith(".fifo", StringComparison.Ordinal) || SnsFifoPublishSupport.HasFifoFields(request)))
        {
            var serviceBusCredentials = credentials
                ?? throw new InvalidOperationException(
                    "Service Bus SNS routing requires Service Bus Topics credentials.");
            ServiceBusFifoTopicState topicState;
            try
            {
                topicState = await SnsFifoPublishSupport.GetServiceBusTopicStateAsync(
                        request.TopicArn,
                        request.TopicName,
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

            if (!SnsFifoPublishSupport.TryResolveBrokerMessageId(
                    topicState,
                    request.MessageGroupId,
                    request.MessageDeduplicationId,
                    request.Message,
                    out brokerMessageId,
                    out error))
            {
                await SnsTopicSupport.WriteInvalidParameterAsync(context, error!).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(brokerMessageId))
            {
                brokerMessageId = messageId.ToString();
            }
        }

        var publisher = SnsBackendPublisherFactory.Create(
            route, credentials, eventGridCredentials, amqpSender, eventGridPublisher);

        var outcome = await publisher.PublishAsync(request, messageId, brokerMessageId, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            await SnsPublishErrorMapper.WriteSendErrorAsync(context, outcome).ConfigureAwait(false);
            return;
        }

        await SnsResponseWriter.WritePublishResponseAsync(context, messageId.ToString()).ConfigureAwait(false);
    }
}
