using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.EventGrid;

namespace Aws2Azure.Modules.Sns.Operations;

/// <summary>
/// Backend-neutral publish surface shared by <see cref="PublishHandler"/> and
/// <see cref="PublishBatchHandler"/>. Each implementation owns its own backend
/// (Azure Event Grid or Service Bus Topics over AMQP) including the
/// exception → outcome mapping, so the handlers no longer branch on
/// <see cref="SnsTopicBackend"/> or duplicate the per-backend error handling.
///
/// <para>Single publish returns a <see cref="SnsPublishOutcome"/> rather than
/// throwing — a failed outcome carries everything the handler needs to render
/// the AWS error response. Batch publish returns the per-entry
/// <see cref="SnsBatchSendResult"/>; the AMQP implementation translates a
/// whole-batch AMQP failure into one failed outcome per entry (preserving the
/// pre-refactor asymmetry where AMQP can fail an entire batch while Event Grid
/// reports per-entry outcomes).</para>
/// </summary>
internal interface ISnsBackendPublisher
{
    Task<SnsPublishOutcome> PublishAsync(PublishRequest request, Guid messageId, CancellationToken cancellationToken);

    Task<SnsBatchSendResult> PublishBatchAsync(
        string topicArn,
        IReadOnlyList<PublishBatchRequestEntry> entries,
        Guid[] messageIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of a single SNS publish. On failure carries the exact shape the
/// AWS error response needs (HTTP status, error type, code, message), matching
/// the pre-refactor per-exception mappers.
/// </summary>
internal readonly record struct SnsPublishOutcome(
    bool Succeeded,
    int SnsStatusCode,
    string ErrorType,
    string ErrorCode,
    string ErrorMessage)
{
    public static SnsPublishOutcome Success { get; } = new(true, 0, string.Empty, string.Empty, string.Empty);

    public static SnsPublishOutcome Failure(int snsStatusCode, string errorType, string errorCode, string errorMessage)
        => new(false, snsStatusCode, errorType, errorCode, errorMessage);
}

/// <summary>
/// Selects the backend publisher for a resolved route, resolving the backend's
/// connection context (Event Grid destination or Service Bus namespace) up front.
/// </summary>
internal static class SnsBackendPublisherFactory
{
    public static ISnsBackendPublisher Create(
        SnsTopicRoute route,
        ServiceBusTopicsCredentials credentials,
        EventGridCredentials? eventGridCredentials,
        ISnsAmqpSender amqpSender,
        IEventGridPublisher eventGridPublisher)
    {
        if (route.Backend == SnsTopicBackend.EventGrid)
        {
            var destination = SnsTopicRouting.ResolveEventGridDestination(route, eventGridCredentials);
            return new EventGridBackendPublisher(eventGridPublisher, destination);
        }

        return new ServiceBusAmqpBackendPublisher(
            amqpSender,
            credentials,
            SnsTopicSupport.ResolveNamespaceFqdn(credentials),
            route.ServiceBusTopicName);
    }
}

/// <summary>Event Grid publish backend.</summary>
internal sealed class EventGridBackendPublisher(
    IEventGridPublisher publisher,
    EventGridPublishDestination destination) : ISnsBackendPublisher
{
    public async Task<SnsPublishOutcome> PublishAsync(PublishRequest request, Guid messageId, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(
                destination,
                SnsPublishSupport.CreateEventGridMessage(request, messageId),
                cancellationToken).ConfigureAwait(false);
            return SnsPublishOutcome.Success;
        }
        catch (EventGridPublishException exception)
        {
            return SnsPublishErrorMapper.ToPublishOutcome(exception);
        }
    }

    public Task<SnsBatchSendResult> PublishBatchAsync(
        string topicArn,
        IReadOnlyList<PublishBatchRequestEntry> entries,
        Guid[] messageIds,
        CancellationToken cancellationToken)
    {
        var messages = new EventGridPublishMessage[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            messageIds[i] = Guid.NewGuid();
            messages[i] = SnsPublishSupport.CreateEventGridMessage(topicArn, entries[i], messageIds[i]);
        }

        return publisher.PublishBatchAsync(destination, messages, cancellationToken);
    }
}

/// <summary>Service Bus Topics (AMQP) publish backend.</summary>
internal sealed class ServiceBusAmqpBackendPublisher(
    ISnsAmqpSender sender,
    ServiceBusTopicsCredentials credentials,
    string namespaceFqdn,
    string topicName) : ISnsBackendPublisher
{
    public async Task<SnsPublishOutcome> PublishAsync(PublishRequest request, Guid messageId, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(
                credentials,
                namespaceFqdn,
                topicName,
                SnsPublishSupport.CreateAmqpMessage(request, messageId),
                cancellationToken).ConfigureAwait(false);
            return SnsPublishOutcome.Success;
        }
        catch (SnsAmqpException exception)
        {
            return SnsPublishErrorMapper.ToPublishOutcome(exception);
        }
    }

    public async Task<SnsBatchSendResult> PublishBatchAsync(
        string topicArn,
        IReadOnlyList<PublishBatchRequestEntry> entries,
        Guid[] messageIds,
        CancellationToken cancellationToken)
    {
        var messages = new SnsAmqpSendMessage[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            messageIds[i] = Guid.NewGuid();
            messages[i] = SnsPublishSupport.CreateAmqpMessage(entries[i], messageIds[i]);
        }

        try
        {
            return await sender.SendBatchAsync(
                credentials,
                namespaceFqdn,
                topicName,
                messages,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SnsAmqpException exception)
        {
            // A whole-batch AMQP failure fails every entry with the same mapped
            // outcome (Event Grid never reaches here — it bakes failures into
            // per-entry outcomes).
            var failure = SnsPublishErrorMapper.CreateBatchFailure(exception);
            var outcomes = new SnsBatchSendOutcome[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                outcomes[i] = failure;
            }

            return new SnsBatchSendResult(outcomes);
        }
    }
}
