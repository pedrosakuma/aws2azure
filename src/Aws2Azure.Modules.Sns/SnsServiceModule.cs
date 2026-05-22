using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Sns.Errors;
using Aws2Azure.Modules.Sns.Management;
using Aws2Azure.Modules.Sns.Operations;
using Aws2Azure.Modules.Sns.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns;

/// <summary>
/// SNS → Azure Service Bus Topics / Event Grid module. Slice 2 implements
/// topic CRUD over the Service Bus Topics management REST API while the
/// remaining Phase 5 operations still dispatch to structured stubs.
/// </summary>
public sealed class SnsServiceModule : IServiceModule
{
    private readonly ICredentialResolver _credentials;
    private readonly IServiceBusTopicsManagementClient _serviceBusTopicsManagementClient;

    public SnsServiceModule(
        ICredentialResolver credentials,
        IServiceBusTopicsManagementClient serviceBusTopicsManagementClient,
        CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(serviceBusTopicsManagementClient);
        ArgumentNullException.ThrowIfNull(capabilities);
        _credentials = credentials;
        _serviceBusTopicsManagementClient = serviceBusTopicsManagementClient;
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
            case SnsOperation.PublishBatch:
            case SnsOperation.Subscribe:
            case SnsOperation.Unsubscribe:
            case SnsOperation.ListSubscriptions:
            case SnsOperation.ListSubscriptionsByTopic:
            case SnsOperation.GetTopicAttributes:
            case SnsOperation.SetTopicAttributes:
                await StubHandlers.HandleNotImplementedAsync(context, parsed.Operation).ConfigureAwait(false);
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
            message: "SNS topic CRUD operations require Azure Service Bus Topics credentials for the supplied AWS access key.");
}
