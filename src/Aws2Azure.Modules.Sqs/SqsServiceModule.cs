using System;
using System.Threading.Tasks;
using Aws2Azure.Amqp.ServiceBus;
using Aws2Azure.Core;
using Aws2Azure.Core.Azure;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Core.SigV4;
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
    private readonly ServiceBusAmqpPool? _amqpPool;

    public SqsServiceModule(AzureHttpClient http, ICredentialResolver credentials, CapabilityMatrix capabilities)
        : this(http, credentials, capabilities, amqpPool: null)
    {
    }

    /// <summary>
    /// Overload that lets the host wire an AMQP connection pool. When
    /// the pool is present, queues whose effective transport (per
    /// <see cref="SqsTransportResolver"/>) is
    /// <see cref="SqsTransport.Amqp"/> route ReceiveMessage /
    /// DeleteMessage / ChangeMessageVisibility through native AMQP
    /// instead of the REST handlers. Queues configured for
    /// <see cref="SqsTransport.Rest"/> still take the REST path even
    /// when a pool is supplied.
    ///
    /// <para>Internal because <see cref="ServiceBusAmqpPool"/> itself is
    /// internal to <c>Aws2Azure.Amqp</c>; the host
    /// (<c>Aws2Azure.Proxy</c>) consumes it via the InternalsVisibleTo
    /// grant on both assemblies.</para>
    /// </summary>
    internal SqsServiceModule(
        AzureHttpClient http,
        ICredentialResolver credentials,
        CapabilityMatrix capabilities,
        ServiceBusAmqpPool? amqpPool)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(capabilities);
        _http = http;
        _credentials = credentials;
        _amqpPool = amqpPool;
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

    /// <summary>
    /// SQS speaks two wire protocols, and the SigV4 auth-error vocabulary
    /// differs between them: the AWS-JSON path uses
    /// <c>InvalidSignatureException</c>/<c>UnrecognizedClientException</c> at
    /// HTTP 400, while the legacy Query path keeps the XML
    /// <c>SignatureDoesNotMatch</c>/403 shape. The module-level
    /// <see cref="ErrorFormat"/> can't capture this — it's per-request — so we
    /// sniff the protocol the caller used and resolve the vocabulary for that
    /// format before rendering. See issue #241.
    /// </summary>
    public ValueTask EmitSigV4FailureAsync(HttpContext context, SigV4ValidationStatus status, string reason)
    {
        var format = SqsWireProtocolParser.Sniff(context) == SqsWireProtocol.AwsJson
            ? AwsErrorFormat.Json
            : AwsErrorFormat.Xml;
        var (statusCode, code) = AuthErrorVocabulary.Resolve(format, status);
        return EmitAuthErrorAsync(context, statusCode, code,
            string.IsNullOrEmpty(reason) ? code : reason);
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
            if (_amqpPool is not null && TryRouteSendToAmqp(parsed, sbCreds, out var senders))
            {
                if (parsed.Operation is SqsOperation.SendMessage)
                {
                    await Operations.AmqpSendMessageHandlers
                        .HandleAsync(context, parsed, senders, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                else
                {
                    await Operations.AmqpSendMessageBatchHandlers
                        .HandleAsync(context, parsed, senders, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                return;
            }
            await Operations.SendMessageHandlers
                .HandleAsync(context, parsed, sbClient, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (parsed.Operation is SqsOperation.ReceiveMessage
            or SqsOperation.DeleteMessage
            or SqsOperation.ChangeMessageVisibility
            or SqsOperation.DeleteMessageBatch
            or SqsOperation.ChangeMessageVisibilityBatch)
        {
            if (_amqpPool is not null && TryRouteToAmqp(parsed, sbCreds, out var receivers))
            {
                await Operations.AmqpReceiveMessageHandlers
                    .HandleAsync(context, parsed, receivers, context.RequestAborted)
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
            // Batch fall-through routes to BatchAdminHandlers (REST).
            await Operations.BatchAdminHandlers
                .HandleAsync(context, parsed, sbClient, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (parsed.Operation is SqsOperation.SetQueueAttributes
            or SqsOperation.PurgeQueue)
        {
            await Operations.BatchAdminHandlers
                .HandleAsync(context, parsed, sbClient, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (parsed.Operation is SqsOperation.ListDeadLetterSourceQueues
            or SqsOperation.ListQueueTags
            or SqsOperation.TagQueue
            or SqsOperation.UntagQueue
            or SqsOperation.AddPermission
            or SqsOperation.RemovePermission)
        {
            await Operations.TailHandlers
                .HandleAsync(context, parsed, sbClient, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        var notImpl = SqsErrorMapping.NotImplemented(parsed.Operation);
        await SqsErrorResponse.WriteAsync(context, parsed.Protocol,
            notImpl.StatusCode, notImpl.Code, notImpl.Message, notImpl.FaultType).ConfigureAwait(false);
    }

    /// <summary>
    /// Per-request decision: should this Receive/Delete/CMV go over
    /// native AMQP? The dispatch is queue-scoped so an installation can
    /// migrate one queue at a time. Returns false when the queue is
    /// REST or when we can't extract a queue name from the request —
    /// the REST handlers report the missing-parameter error in that
    /// case, so we let them.
    /// </summary>
    private bool TryRouteToAmqp(
        SqsParseResult parsed, ServiceBusCredentials sbCreds, out IAmqpReceiverProvider receivers)
    {
        receivers = null!;
        if (_amqpPool is null) return false;

        if (!parsed.Parameters.TryGetValue("QueueUrl", out var url) || string.IsNullOrEmpty(url))
            return false;
        var queueName = Internal.QueueUrlBuilder.ExtractQueueName(url);
        if (string.IsNullOrEmpty(queueName)) return false;

        if (SqsTransportResolver.Resolve(sbCreds, queueName) != SqsTransport.Amqp)
            return false;

        receivers = new ServiceBusAmqpReceiverProvider(_amqpPool, sbCreds);
        return true;
    }

    /// <summary>Sender-side twin of <see cref="TryRouteToAmqp"/>.</summary>
    private bool TryRouteSendToAmqp(
        SqsParseResult parsed, ServiceBusCredentials sbCreds, out IAmqpSenderProvider senders)
    {
        senders = null!;
        if (_amqpPool is null) return false;

        if (!parsed.Parameters.TryGetValue("QueueUrl", out var url) || string.IsNullOrEmpty(url))
            return false;
        var queueName = Internal.QueueUrlBuilder.ExtractQueueName(url);
        if (string.IsNullOrEmpty(queueName)) return false;

        if (SqsTransportResolver.Resolve(sbCreds, queueName) != SqsTransport.Amqp)
            return false;

        senders = new ServiceBusAmqpSenderProvider(_amqpPool, sbCreds);
        return true;
    }
}
