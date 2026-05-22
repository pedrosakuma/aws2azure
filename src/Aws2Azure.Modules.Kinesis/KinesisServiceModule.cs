using System;
using System.Threading.Tasks;
using Aws2Azure.Core;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Core.Modules;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.Operations;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis;

/// <summary>
/// Kinesis Data Streams → Azure Event Hubs module. Phase-4 Slice 1
/// lands routing + AWS-JSON-1.1 parsing + AAD/SAS credential gating;
/// every recognised operation currently dispatches to a stub handler
/// that returns <c>InternalFailure</c> with HTTP 501. Slices 2-7
/// replace stub handlers with real Event Hubs translations.
/// </summary>
public sealed class KinesisServiceModule : IServiceModule
{
    private readonly ICredentialResolver _credentials;
    private readonly IEventHubsManagementClient _managementClient;
    private readonly IEventHubMetadataCache _metadataCache;
    private readonly IEventHubsAmqpSender _amqpSender;
    private readonly IEventHubsAmqpReceiver _amqpReceiver;
    private readonly ListShardsCursorCodecFactory _listShardsCursorCodecFactory;
    private readonly ShardIteratorTokenCodecFactory _shardIteratorTokenCodecFactory;

    public KinesisServiceModule(
        ICredentialResolver credentials,
        IEventHubsManagementClient managementClient,
        IEventHubMetadataCache metadataCache,
        IEventHubsAmqpSender amqpSender,
        IEventHubsAmqpReceiver amqpReceiver,
        ListShardsCursorCodecFactory listShardsCursorCodecFactory,
        ShardIteratorTokenCodecFactory shardIteratorTokenCodecFactory,
        CapabilityMatrix capabilities)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(managementClient);
        ArgumentNullException.ThrowIfNull(metadataCache);
        ArgumentNullException.ThrowIfNull(amqpSender);
        ArgumentNullException.ThrowIfNull(amqpReceiver);
        ArgumentNullException.ThrowIfNull(listShardsCursorCodecFactory);
        ArgumentNullException.ThrowIfNull(shardIteratorTokenCodecFactory);
        ArgumentNullException.ThrowIfNull(capabilities);
        _credentials = credentials;
        _managementClient = managementClient;
        _metadataCache = metadataCache;
        _amqpSender = amqpSender;
        _amqpReceiver = amqpReceiver;
        _listShardsCursorCodecFactory = listShardsCursorCodecFactory;
        _shardIteratorTokenCodecFactory = shardIteratorTokenCodecFactory;
        Capabilities = capabilities;
    }

    public string ServiceName => "kinesis";
    public bool RequiresSigV4 => true;
    public bool BuffersRequestBodyForSigV4 => true;
    // Dispatch is keyed on X-Amz-Target — refuse signatures that don't cover it
    // so the operation can't be tampered with after-signature.
    public IReadOnlyList<string> RequiredSignedHeaders { get; } = new[] { "x-amz-target" };
    public AwsErrorFormat ErrorFormat => AwsErrorFormat.Json;
    public CapabilityMatrix Capabilities { get; }

    public ValueTask EmitAuthErrorAsync(HttpContext context, int statusCode, string code, string message)
        => new(KinesisErrorResponse.WriteAsync(context, statusCode, code, message));

    public bool MatchesHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        return host.StartsWith("kinesis.", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("kinesis-", StringComparison.OrdinalIgnoreCase)
            || host.Equals("kinesis", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask HandleAsync(HttpContext context)
    {
        var parsed = await KinesisWireProtocolParser.ParseAsync(context, context.RequestAborted)
            .ConfigureAwait(false);

        if (parsed.Error is not null)
        {
            await KinesisErrorResponse.WriteAsync(context,
                parsed.Error.StatusCode, parsed.Error.Code, parsed.Error.Message)
                .ConfigureAwait(false);
            return;
        }

        var accessKey = context.Items["aws2azure.accessKeyId"] as string;
        if (string.IsNullOrEmpty(accessKey))
        {
            await KinesisErrorResponse.WriteAsync(context,
                StatusCodes.Status403Forbidden,
                "MissingAuthenticationTokenException",
                "Request is missing AWS credentials.").ConfigureAwait(false);
            return;
        }

        if (_credentials.GetAzureCredentialsFor(accessKey, AzureService.EventHubs) is not EventHubsCredentials eventHubsCredentials)
        {
            await KinesisErrorResponse.WriteAsync(context,
                StatusCodes.Status403Forbidden,
                "AccessDeniedException",
                "No Event Hubs credentials configured for the supplied AWS access key.").ConfigureAwait(false);
            return;
        }

        switch (parsed.Operation)
        {
            case KinesisOperation.DescribeStream:
                await DescribeStreamHandler.HandleAsync(
                        context,
                        parsed,
                        eventHubsCredentials,
                        _managementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                break;
            case KinesisOperation.DescribeStreamSummary:
                await DescribeStreamSummaryHandler.HandleAsync(
                        context,
                        parsed,
                        eventHubsCredentials,
                        _managementClient,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                break;
            case KinesisOperation.ListShards:
                await ListShardsHandler.HandleAsync(
                        context,
                        parsed,
                        eventHubsCredentials,
                        _managementClient,
                        _listShardsCursorCodecFactory,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                break;
            case KinesisOperation.PutRecord:
                await PutRecordHandler.HandleAsync(
                        context,
                        parsed,
                        eventHubsCredentials,
                        _metadataCache,
                        _amqpSender,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                break;
            case KinesisOperation.PutRecords:
                await PutRecordsHandler.HandleAsync(
                        context,
                        parsed,
                        eventHubsCredentials,
                        _metadataCache,
                        _amqpSender,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                break;
            case KinesisOperation.GetShardIterator:
                await GetShardIteratorHandler.HandleAsync(
                        context,
                        parsed,
                        eventHubsCredentials,
                        _metadataCache,
                        _shardIteratorTokenCodecFactory,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                break;
            case KinesisOperation.GetRecords:
                await GetRecordsHandler.HandleAsync(
                        context,
                        parsed,
                        eventHubsCredentials,
                        _metadataCache,
                        _amqpReceiver,
                        _shardIteratorTokenCodecFactory,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                break;
            default:
                await StubHandlers.HandleNotImplementedAsync(context, parsed.Operation).ConfigureAwait(false);
                break;
        }
    }
}
