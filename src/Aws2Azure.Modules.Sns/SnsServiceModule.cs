using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Sns;

/// <summary>
/// SNS → Azure Service Bus Topics / Event Grid module. Slice 2 implements
/// topic CRUD over the Service Bus Topics management REST API and
/// Publish / PublishBatch over AMQP 1.0 while the remaining Phase 5
/// operations still dispatch to structured stubs.
/// </summary>
public sealed class SnsServiceModule : IServiceModule
{
    private readonly ICredentialResolver _credentials;
    private readonly IServiceBusTopicsManagementClient _serviceBusTopicsManagementClient;
    private readonly ISnsAmqpSender _amqpSender;
    private readonly ILogger<SnsServiceModule> _logger;

    internal SnsServiceModule(
        ICredentialResolver credentials,
        IServiceBusTopicsManagementClient serviceBusTopicsManagementClient,
        ISnsAmqpSender amqpSender,
        ILogger<SnsServiceModule> logger,
        CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(serviceBusTopicsManagementClient);
        ArgumentNullException.ThrowIfNull(amqpSender);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(capabilities);
        _credentials = credentials;
        _serviceBusTopicsManagementClient = serviceBusTopicsManagementClient;
        _amqpSender = amqpSender;
        _logger = logger;
        Capabilities = capabilities;
    }

    public string ServiceName => "sns";
    public bool RequiresSigV4 => true;
    public bool BuffersRequestBodyForSigV4 => true;
    public IReadOnlyList<string> RequiredSignedHeaders { get; } = Array.Empty<string>();
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Xml;
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
        var hasEventGrid = _credentials.GetAzureCredentialsFor(accessKey, AzureService.EventGrid) is EventGridCredentials;
        if (serviceBusTopicsCredentials is null && !hasEventGrid)
        {
            await SnsErrorResponse.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                errorType: "Sender",
                errorCode: "AuthorizationError",
                message: "No Azure Service Bus Topics or Event Grid credentials configured for the supplied AWS access key.").ConfigureAwait(false);
            return;
        }

        switch (parsed.Operation)
        {
            case SnsOperation.CreateTopic:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await CreateTopicHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.DeleteTopic:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await DeleteTopicHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.ListTopics:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await ListTopicsHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.Publish:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await PublishHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _amqpSender,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.PublishBatch:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await PublishBatchHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _amqpSender,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.Subscribe:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

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
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await UnsubscribeHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.ListSubscriptions:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await ListSubscriptionsHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.ListSubscriptionsByTopic:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

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
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await GetTopicAttributesHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.SetTopicAttributes:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await SetTopicAttributesHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.GetSubscriptionAttributes:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

                await GetSubscriptionAttributesHandler.HandleAsync(
                        context,
                        parsed,
                        serviceBusTopicsCredentials,
                        _serviceBusTopicsManagementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            case SnsOperation.SetSubscriptionAttributes:
                if (serviceBusTopicsCredentials is null)
                {
                    await WriteServiceBusTopicsCredentialErrorAsync(context).ConfigureAwait(false);
                    return;
                }

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
            message: "SNS operations backed by Azure Service Bus Topics require Service Bus Topics credentials for the supplied AWS access key.");
}
