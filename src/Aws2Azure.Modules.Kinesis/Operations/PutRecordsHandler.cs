using System.Buffers;
using System.Globalization;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class PutRecordsHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        KinesisParseResult parseResult,
        EventHubsCredentials credentials,
        IEventHubMetadataCache metadataCache,
        IEventHubsAmqpSender amqpSender,
        CancellationToken cancellationToken)
    {
        if (parseResult.Body.Length > PutRecordCommon.MaxRequestPayloadBytes)
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "ValidationException",
                    $"Request body must not exceed {PutRecordCommon.MaxRequestPayloadBytes} bytes.")
                .ConfigureAwait(false);
            return;
        }

        if (!KinesisMetadataSupport.TryDeserialize(
                parseResult.Body,
                KinesisJsonSerializerContext.Default.PutRecordsRequest,
                out PutRecordsRequest? request,
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

        if (request?.Records is null)
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "Records is required.")
                .ConfigureAwait(false);
            return;
        }

        if (request.Records.Count == 0)
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "Records must contain at least one entry.")
                .ConfigureAwait(false);
            return;
        }

        if (request.Records.Count > PutRecordCommon.MaxRecordsPerRequest)
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "ValidationException",
                    $"Records must contain at most {PutRecordCommon.MaxRecordsPerRequest} entries.")
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

        var responseEntries = new PutRecordsResultEntry[request.Records.Count];
        var validRecords = new List<ValidatedRecord>(request.Records.Count);
        var groupedByPartition = new Dictionary<int, List<ValidatedRecord>>();
        var failedRecordCount = 0;

        try
        {
            for (var i = 0; i < request.Records.Count; i++)
            {
                var record = request.Records[i];
                if (!TryValidateRecord(record, i, eventHub, responseEntries, ref failedRecordCount, out var validated))
                {
                    continue;
                }

                validRecords.Add(validated);
                if (!groupedByPartition.TryGetValue(validated.PartitionIndex, out var partitionRecords))
                {
                    partitionRecords = new List<ValidatedRecord>();
                    groupedByPartition.Add(validated.PartitionIndex, partitionRecords);
                }

                partitionRecords.Add(validated);
            }

            foreach (var entry in groupedByPartition)
            {
                var partitionRecords = entry.Value;
                var messages = new (ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, object>? annotations)[partitionRecords.Count];
                for (var i = 0; i < partitionRecords.Count; i++)
                {
                    var record = partitionRecords[i];
                    messages[i] = (record.Buffer.AsMemory(0, record.DataLength), PutRecordCommon.CreatePartitionAnnotations(record.PartitionKey));
                }

                try
                {
                    var batchResult = await amqpSender.SendBatchAsync(
                            credentials,
                            namespaceFqdn,
                            eventHubName + "/Partitions/" + partitionRecords[0].PartitionId,
                            messages,
                            cancellationToken)
                        .ConfigureAwait(false);

                    ApplyBatchOutcomes(partitionRecords, batchResult.Outcomes, responseEntries, ref failedRecordCount);
                }
                catch (EventHubsAmqpException ex) when (ex.Kind != EventHubsAmqpFailureKind.Auth)
                {
                    var batchFailure = PutRecordCommon.ResolveBatchFailure(ex, "PutRecords");
                    ApplyBatchFailure(partitionRecords, batchFailure.ErrorCode, batchFailure.ErrorMessage, responseEntries, ref failedRecordCount);
                }
                catch (EventHubsAmqpException ex)
                {
                    await PutRecordCommon.WriteSendErrorAsync(context, ex, "PutRecords").ConfigureAwait(false);
                    return;
                }
            }

            for (var i = 0; i < responseEntries.Length; i++)
            {
                var responseEntry = responseEntries[i];
                if (!string.IsNullOrEmpty(responseEntry?.ShardId))
                {
                    responseEntry.SequenceNumber = PutRecordCommon.NextSyntheticSequenceNumber().ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        finally
        {
            for (var i = 0; i < validRecords.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(validRecords[i].Buffer);
            }
        }

        var response = new PutRecordsResponse
        {
            FailedRecordCount = failedRecordCount,
            Records = responseEntries,
            EncryptionType = "NONE",
        };

        await KinesisMetadataSupport.WriteJsonAsync(context, response, KinesisJsonSerializerContext.Default.PutRecordsResponse)
            .ConfigureAwait(false);
    }

    private static bool TryValidateRecord(
        PutRecordsRequestEntry? record,
        int requestIndex,
        EventHubDescription eventHub,
        PutRecordsResultEntry[] responseEntries,
        ref int failedRecordCount,
        out ValidatedRecord validated)
    {
        if (record is null)
        {
            responseEntries[requestIndex] = CreateValidationFailure($"Records[{requestIndex}] must be an object.");
            failedRecordCount++;
            validated = default;
            return false;
        }

        if (record.Data is null)
        {
            responseEntries[requestIndex] = CreateValidationFailure($"Records[{requestIndex}].Data is required.");
            failedRecordCount++;
            validated = default;
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.PartitionKey))
        {
            responseEntries[requestIndex] = CreateValidationFailure($"Records[{requestIndex}].PartitionKey is required.");
            failedRecordCount++;
            validated = default;
            return false;
        }

        if (!PutRecordCommon.TryDecodeData(record.Data!, out var rented, out var dataLength, out var error))
        {
            responseEntries[requestIndex] = CreateValidationFailure($"Records[{requestIndex}].{error}");
            failedRecordCount++;
            validated = default;
            return false;
        }

        var partitionIndex = PutRecordCommon.ComputePartitionIndex(record.PartitionKey!, eventHub.PartitionCount);
        validated = new ValidatedRecord(
            requestIndex,
            record.PartitionKey!,
            partitionIndex,
            PutRecordCommon.ResolvePartitionId(eventHub, partitionIndex),
            PutRecordCommon.FormatShardId(partitionIndex),
            rented,
            dataLength);
        return true;
    }

    private static void ApplyBatchOutcomes(
        IReadOnlyList<ValidatedRecord> partitionRecords,
        IReadOnlyList<EventHubsBatchSendOutcome> outcomes,
        PutRecordsResultEntry[] responseEntries,
        ref int failedRecordCount)
    {
        for (var i = 0; i < partitionRecords.Count; i++)
        {
            var record = partitionRecords[i];
            var outcome = outcomes[i];
            if (outcome.Succeeded)
            {
                responseEntries[record.RequestIndex] = new PutRecordsResultEntry
                {
                    ShardId = record.ShardId,
                };
            }
            else
            {
                failedRecordCount++;
                responseEntries[record.RequestIndex] = new PutRecordsResultEntry
                {
                    ErrorCode = outcome.ErrorCode,
                    ErrorMessage = outcome.ErrorMessage,
                };
            }
        }
    }

    private static void ApplyBatchFailure(
        IReadOnlyList<ValidatedRecord> partitionRecords,
        string errorCode,
        string errorMessage,
        PutRecordsResultEntry[] responseEntries,
        ref int failedRecordCount)
    {
        for (var i = 0; i < partitionRecords.Count; i++)
        {
            var record = partitionRecords[i];
            failedRecordCount++;
            responseEntries[record.RequestIndex] = new PutRecordsResultEntry
            {
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
            };
        }
    }

    private static PutRecordsResultEntry CreateValidationFailure(string message)
        => new()
        {
            ErrorCode = "ValidationException",
            ErrorMessage = message,
        };

    private readonly record struct ValidatedRecord(
        int RequestIndex,
        string PartitionKey,
        int PartitionIndex,
        string PartitionId,
        string ShardId,
        byte[] Buffer,
        int DataLength);
}
