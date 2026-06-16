using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Sns;

/// <summary>
/// SNS → Azure Service Bus Topics / Event Grid module. Topic management,
/// attributes, and subscriptions remain on Service Bus Topics in Phase 5,
/// while Publish / PublishBatch can dispatch to Service Bus Topics or
/// Event Grid per topic configuration.
/// </summary>
public sealed class SnsServiceModule : IServiceModule
{
    private readonly ICredentialResolver _credentials;
    private readonly SnsSettings _settings;
    private readonly IServiceBusTopicsManagementClient _serviceBusTopicsManagementClient;
    private readonly ISnsAmqpSender _amqpSender;
    private readonly IEventGridPublisher _eventGridPublisher;
    private readonly ILogger<SnsServiceModule> _logger;

    internal SnsServiceModule(
        ICredentialResolver credentials,
        SnsSettings settings,
        IServiceBusTopicsManagementClient serviceBusTopicsManagementClient,
        ISnsAmqpSender amqpSender,
        IEventGridPublisher eventGridPublisher,
        ILogger<SnsServiceModule> logger,
        CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(serviceBusTopicsManagementClient);
        ArgumentNullException.ThrowIfNull(amqpSender);
        ArgumentNullException.ThrowIfNull(eventGridPublisher);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(capabilities);
        _credentials = credentials;
        _settings = settings;
        _serviceBusTopicsManagementClient = serviceBusTopicsManagementClient;
        _amqpSender = amqpSender;
        _eventGridPublisher = eventGridPublisher;
        _logger = logger;
        Capabilities = capabilities;
    }

    public string ServiceName => "sns";
    public bool RequiresSigV4 => true;
    public bool BuffersRequestBodyForSigV4 => true;
    public IReadOnlyList<string> RequiredSignedHeaders { get; } = ["content-type"];
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Xml;
    public IReadOnlySet<string> KnownOperations => _knownOperations;
    // Derived from the wire-protocol action table (single source of truth) so
    // the metrics allowlist can never drift from the set of actions the parser
    // recognises. Every parseable action is labelled by name; unrecognised
    // actions still collapse to "unknown".
    private static readonly FrozenSet<string> _knownOperations =
        SnsOperationNames.Names.ToFrozenSet(StringComparer.Ordinal);
    public CapabilityMatrix Capabilities { get; }

    public ValueTask EmitAuthErrorAsync(HttpContext context, int statusCode, string code, string message)
        => new(SnsErrorResponse.WriteErrorAsync(
            context,
            statusCode,
            statusCode >= 500 ? "Receiver" : "Sender",
            code,
            message));

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.StartsWith("sns.", StringComparison.OrdinalIgnoreCase)
            || host.Equals("sns", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var parsed = await SnsWireProtocolParser.ParseAsync(context, context.RequestAborted).ConfigureAwait(false);
        if (parsed.Error is not null)
        {
            await WriteParseErrorAsync(context, parsed.Error).ConfigureAwait(false);
            return;
        }

        var accessKey = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrEmpty(accessKey))
        {
            await SnsErrorResponse.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                errorType: "Sender",
                errorCode: "MissingAuthenticationToken",
                message: "Request is missing AWS credentials.").ConfigureAwait(false);
            return;
        }

        var serviceBusTopicsCredentials = _credentials.GetAzureCredentialsFor(accessKey, AzureService.ServiceBusTopics) as ServiceBusTopicsCredentials;
        var eventGridCredentials = _credentials.GetAzureCredentialsFor(accessKey, AzureService.EventGrid) as EventGridCredentials;
        if (serviceBusTopicsCredentials is null)
        {
            await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
            return;
        }

        switch (parsed.Operation)
        {
            case SnsOperation.CreateTopic:
                await CreateTopicHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.DeleteTopic:
                await DeleteTopicHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.ListTopics:
                await ListTopicsHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.Publish:
                await PublishHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        eventGridCredentials,
                        _settings,
                        _amqpSender,
                        _eventGridPublisher,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.PublishBatch:
                await PublishBatchHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        eventGridCredentials,
                        _settings,
                        _amqpSender,
                        _eventGridPublisher,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.Subscribe:
                await SubscribeHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        _logger,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.Unsubscribe:
                await UnsubscribeHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.ListSubscriptions:
                await ListSubscriptionsHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.ListSubscriptionsByTopic:
                await ListSubscriptionsByTopicHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.ConfirmSubscription:
                await ConfirmSubscriptionHandler.HandleAsync(context, parsed).ConfigureAwait(false);
                return;
            case SnsOperation.GetTopicAttributes:
                await GetTopicAttributesHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.SetTopicAttributes:
                await SetTopicAttributesHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.GetSubscriptionAttributes:
                await GetSubscriptionAttributesHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.SetSubscriptionAttributes:
                await SetSubscriptionAttributesHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            default:
                await SnsErrorResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    errorType: "Sender",
                    errorCode: "InvalidAction",
                    message: "Unsupported SNS action.").ConfigureAwait(false);
                return;
        }
    }

    private static Task WriteParseErrorAsync(HttpContext context, SnsParseError error)
    {
        var statusCode = error.Type == SnsParseErrorType.InvalidRequest
            ? StatusCodes.Status413PayloadTooLarge
            : StatusCodes.Status400BadRequest;

        return SnsErrorResponse.WriteErrorAsync(
            context,
            statusCode,
            errorType: "Sender",
            errorCode: error.Code,
            message: error.Message);
    }

    private static Task WriteServiceBusTopicsCredentialErrorAsync(HttpContext context)
        => SnsErrorResponse.WriteErrorAsync(
            context,
            StatusCodes.Status403Forbidden,
            errorType: "Sender",
            errorCode: "AuthorizationError",
            message: "SNS operations in this slice require Azure Service Bus Topics credentials for the supplied AWS access key.");
}
