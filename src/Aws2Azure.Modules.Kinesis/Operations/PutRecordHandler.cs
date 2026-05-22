using System.Buffers;
using System.Globalization;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class PutRecordHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        KinesisParseResult parseResult,
        EventHubsCredentials credentials,
        IEventHubMetadataCache metadataCache,
        IEventHubsAmqpSender amqpSender,
        CancellationToken cancellationToken)
    {
        if (!KinesisMetadataSupport.TryDeserialize(
                parseResult.Body,
                KinesisJsonSerializerContext.Default.PutRecordRequest,
                out PutRecordRequest? request,
                out var parseError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "SerializationException", parseError!)
                .ConfigureAwait(false);
            return;
        }

        if (!KinesisMetadataSupport.TryResolveStreamName(request?.StreamName, request?.StreamARN, out var streamName, out var validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        if (request is null || request.Data is null)
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "Data is required.")
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.PartitionKey))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "PartitionKey is required.")
                .ConfigureAwait(false);
            return;
        }

        var namespaceFqdn = KinesisMetadataSupport.ResolveNamespaceFqdn(credentials);
        var eventHubName = KinesisMetadataSupport.ResolveEventHubName(credentials, streamName);

        EventHubDescription eventHub;
        try
        {
            eventHub = await metadataCache.GetEventHubAsync(credentials, namespaceFqdn, eventHubName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EventHubsManagementException ex)
        {
            await KinesisMetadataSupport.WriteManagementErrorAsync(context, ex, streamName).ConfigureAwait(false);
            return;
        }

        if (eventHub.PartitionCount <= 0)
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "InternalFailureException",
                    $"Azure Event Hubs returned an invalid partition count for stream '{streamName}'.")
                .ConfigureAwait(false);
            return;
        }

        if (!PutRecordCommon.TryDecodeData(request.Data!, out var rented, out var dataLength, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var partitionIndex = PutRecordCommon.ComputePartitionIndex(request.PartitionKey!, eventHub.PartitionCount);
            var partitionId = PutRecordCommon.ResolvePartitionId(eventHub, partitionIndex);
            var entityPath = eventHubName + "/Partitions/" + partitionId;

            await amqpSender.SendAsync(
                    credentials,
                    namespaceFqdn,
                    entityPath,
                    rented.AsMemory(0, dataLength),
                    PutRecordCommon.CreatePartitionAnnotations(request.PartitionKey),
                    cancellationToken)
                .ConfigureAwait(false);

            var response = new PutRecordResponse
            {
                ShardId = PutRecordCommon.FormatShardId(partitionIndex),
                SequenceNumber = PutRecordCommon.NextSyntheticSequenceNumber().ToString(CultureInfo.InvariantCulture),
                EncryptionType = "NONE",
            };

            await KinesisMetadataSupport.WriteJsonAsync(context, response, KinesisJsonSerializerContext.Default.PutRecordResponse)
                .ConfigureAwait(false);
        }
        catch (EventHubsAmqpException ex)
        {
            await PutRecordCommon.WriteSendErrorAsync(context, ex, "PutRecord").ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static int ComputePartitionIndex(string partitionKey, int partitionCount)
        => PutRecordCommon.ComputePartitionIndex(partitionKey, partitionCount);

    internal static string FormatShardId(int partitionIndex)
        => PutRecordCommon.FormatShardId(partitionIndex);
}
