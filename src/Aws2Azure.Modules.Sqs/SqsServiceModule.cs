using System;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Sqs.Errors;
using Aws2Azure.Modules.Sqs.Internal;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs;

/// <summary>
/// SQS service module. Slice 0 wires routing, protocol detection
/// (query + AWS JSON 1.0), error rendering, and the Service Bus REST
/// client. Every operation currently surfaces a NotImplemented response
/// in the caller's wire format; per-op handlers land in Slice 1+.
/// </summary>
public sealed class SqsServiceModule : IServiceModule
{
    private readonly AzureHttpClient _http;
    private readonly ICredentialResolver _credentials;

    public SqsServiceModule(AzureHttpClient http, ICredentialResolver credentials, CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _http = http;
        _credentials = credentials;
        Capabilities = capabilities;
    }

    public string ServiceName => "sqs";
    public bool RequiresSigV4 => true;

    // Module-level ErrorFormat covers auth errors emitted by the registry
    // BEFORE the per-request wire protocol has been negotiated. Modern AWS
    // SDKs talk AWS JSON 1.0 to SQS, so JSON is the right default. Per-op
    // handlers render their own errors via SqsErrorResponse, which respects
    // the actual request protocol (Query → XML, AwsJson → JSON).
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Json;
    public CapabilityMatrix Capabilities { get; }

    /// <summary>
    /// Registry-level auth errors (e.g. SigV4 failures) must reach the
    /// caller in the wire protocol the caller actually used. Modern AWS-JSON
    /// SDKs accept the JSON envelope; legacy SQS-Query SDKs (boto/aws-cli v1,
    /// the v1 Java SDK) only parse the XML <c>&lt;ErrorResponse&gt;</c>
    /// envelope and would otherwise see a 4xx with an unparseable body.
    /// Negotiate the wire format from the request before rendering so both
    /// audiences see an SQS-shaped error.
    /// </summary>
    public async ValueTask EmitAuthErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        var protocol = SqsWireProtocolParser.Sniff(context);
        await SqsErrorResponse.WriteAsync(context, protocol, statusCode, code, message,
            statusCode >= 500 ? SqsErrorResponse.FaultType.Receiver : SqsErrorResponse.FaultType.Sender)
            .ConfigureAwait(false);
    }

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        // SQS endpoints: sqs.<region>.amazonaws.com, sqs-fips.<region>.amazonaws.com,
        // and the regional bare hostnames used by some legacy clients.
        return host.StartsWith("sqs.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("sqs-", StringComparison.OrdinalIgnoreCase)
            || host.Equals("sqs", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var parsed = await SqsWireProtocolParser.ParseAsync(context, context.RequestAborted).ConfigureAwait(false);

        if (parsed.Error is not null)
        {
            await SqsErrorResponse.WriteAsync(context, parsed.Protocol,
                StatusCodes.Status400BadRequest, parsed.Error.Code, parsed.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        var accessKey = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrEmpty(accessKey))
        {
            var err = SqsErrorMapping.MissingCredentials();
            await SqsErrorResponse.WriteAsync(context, parsed.Protocol,
                err.StatusCode, err.Code, err.Message, err.FaultType).ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKey, AzureService.ServiceBus) is not ServiceBusCredentials sbCreds)
        {
            var err = SqsErrorMapping.MissingCredentials();
            await SqsErrorResponse.WriteAsync(context, parsed.Protocol,
                err.StatusCode, err.Code, err.Message, err.FaultType).ConfigureAwait(false);
            return;
        }

        // Slice 1: dispatch the queue-lifecycle ops; everything else still
        // surfaces NotImplemented in the caller's wire protocol.
        var sbClient = new ServiceBusClient(_http, sbCreds);
        if (parsed.Operation is SqsOperation.CreateQueue
            or SqsOperation.DeleteQueue
            or SqsOperation.ListQueues
            or SqsOperation.GetQueueUrl
            or SqsOperation.GetQueueAttributes)
        {
            await Operations.QueueLifecycleHandlers
                .HandleAsync(context, parsed, sbClient, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (parsed.Operation is SqsOperation.SendMessage or SqsOperation.SendMessageBatch)
        {
            await Operations.SendMessageHandlers
                .HandleAsync(context, parsed, sbClient, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (parsed.Operation is SqsOperation.ReceiveMessage
            or SqsOperation.DeleteMessage
            or SqsOperation.ChangeMessageVisibility)
        {
            await Operations.ReceiveMessageHandlers
                .HandleAsync(context, parsed, sbClient, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        var notImpl = SqsErrorMapping.NotImplemented(parsed.Operation);
        await SqsErrorResponse.WriteAsync(context, parsed.Protocol,
            notImpl.StatusCode, notImpl.Code, notImpl.Message, notImpl.FaultType).ConfigureAwait(false);
    }
}
