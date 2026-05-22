using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class PutRecordHandler
{
    private const int MaxDataBytes = 1_048_576;
    private static long _lastSequenceNumber;
    private static long _sequenceCounter;

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

        if (!TryDecodeData(request.Data, out var rented, out var dataLength, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var partitionIndex = ComputePartitionIndex(request.PartitionKey, eventHub.PartitionCount);
            var partitionId = ResolvePartitionId(eventHub, partitionIndex);
            var entityPath = eventHubName + "/Partitions/" + partitionId;
            Dictionary<string, object> annotations = new(StringComparer.Ordinal)
            {
                [AmqpMessageAnnotations.KeyPartitionKey] = request.PartitionKey,
            };

            await amqpSender.SendAsync(
                    credentials,
                    namespaceFqdn,
                    entityPath,
                    rented.AsMemory(0, dataLength),
                    annotations,
                    cancellationToken)
                .ConfigureAwait(false);

            var response = new PutRecordResponse
            {
                ShardId = FormatShardId(partitionIndex),
                SequenceNumber = NextSyntheticSequenceNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
                EncryptionType = "NONE",
            };

            await KinesisMetadataSupport.WriteJsonAsync(context, response, KinesisJsonSerializerContext.Default.PutRecordResponse)
                .ConfigureAwait(false);
        }
        catch (EventHubsAmqpException ex)
        {
            await WriteSendErrorAsync(context, ex).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static int ComputePartitionIndex(string partitionKey, int partitionCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partitionCount, 0);

        var utf8Length = Encoding.UTF8.GetByteCount(partitionKey);
        byte[]? rented = null;
        Span<byte> utf8Bytes = utf8Length <= 256
            ? stackalloc byte[utf8Length]
            : (rented = ArrayPool<byte>.Shared.Rent(utf8Length));
        Span<byte> hash = stackalloc byte[16];

        try
        {
            Encoding.UTF8.GetBytes(partitionKey.AsSpan(), utf8Bytes);
            MD5.HashData(utf8Bytes, hash);
            var remainder = 0;
            for (var i = 0; i < hash.Length; i++)
            {
                remainder = ((remainder << 8) + hash[i]) % partitionCount;
            }

            return remainder;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    internal static string FormatShardId(int partitionIndex)
        => $"shardId-{partitionIndex:D12}";

    private static string ResolvePartitionId(EventHubDescription eventHub, int partitionIndex)
    {
        if (partitionIndex >= 0 && partitionIndex < eventHub.PartitionIds.Count)
        {
            var partitionId = eventHub.PartitionIds[partitionIndex];
            if (!string.IsNullOrWhiteSpace(partitionId))
            {
                return partitionId;
            }
        }

        return partitionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryDecodeData(string data, out byte[] rented, out int dataLength, out string? error)
    {
        ArgumentNullException.ThrowIfNull(data);

        var maxDecodedLength = GetMaxDecodedLength(data.Length);
        rented = ArrayPool<byte>.Shared.Rent(Math.Max(maxDecodedLength, 1));
        if (!Convert.TryFromBase64String(data, rented, out dataLength))
        {
            ArrayPool<byte>.Shared.Return(rented);
            rented = Array.Empty<byte>();
            error = "Data must be valid base64.";
            return false;
        }

        if (dataLength > MaxDataBytes)
        {
            ArrayPool<byte>.Shared.Return(rented);
            rented = Array.Empty<byte>();
            error = $"Data must decode to at most {MaxDataBytes} bytes.";
            return false;
        }

        error = null;
        return true;
    }

    private static int GetMaxDecodedLength(int encodedLength)
        => ((encodedLength + 3) / 4) * 3;

    private static long NextSyntheticSequenceNumber()
    {
        while (true)
        {
            var previous = Volatile.Read(ref _lastSequenceNumber);
            var candidate = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20)
                | (Interlocked.Increment(ref _sequenceCounter) & 0xFFFFF);
            if (candidate <= previous)
            {
                candidate = previous + 1;
            }

            if (Interlocked.CompareExchange(ref _lastSequenceNumber, candidate, previous) == previous)
            {
                return candidate;
            }
        }
    }

    private static Task WriteSendErrorAsync(HttpContext context, EventHubsAmqpException ex)
    {
        if (ex.Kind == EventHubsAmqpFailureKind.Auth)
        {
            return KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "AccessDeniedException",
                "Access denied when sending to Azure Event Hubs over AMQP.");
        }

        if (ex.Kind == EventHubsAmqpFailureKind.Throttled)
        {
            return KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "ProvisionedThroughputExceededException",
                "Azure Event Hubs throttled the PutRecord request.");
        }

        var message = string.Equals(ex.Condition, AmqpErrorCondition.Timeout, StringComparison.Ordinal)
            ? "Azure Event Hubs AMQP send timed out."
            : "Azure Event Hubs AMQP send failed.";

        if (!string.IsNullOrWhiteSpace(ex.Description))
        {
            message += " " + ex.Description;
        }

        return KinesisErrorResponse.WriteAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "InternalFailureException",
            message);
    }
}
