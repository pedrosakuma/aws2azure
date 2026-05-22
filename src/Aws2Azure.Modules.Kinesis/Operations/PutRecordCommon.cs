using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class PutRecordCommon
{
    internal const int MaxDataBytes = 1_048_576;
    internal const int MaxRecordsPerRequest = 500;
    internal const int MaxRequestPayloadBytes = 5 * 1024 * 1024;

    private static long _lastSequenceNumber;
    private static long _sequenceCounter;

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

    internal static string ResolvePartitionId(EventHubDescription eventHub, int partitionIndex)
    {
        if (partitionIndex >= 0 && partitionIndex < eventHub.PartitionIds.Count)
        {
            var partitionId = eventHub.PartitionIds[partitionIndex];
            if (!string.IsNullOrWhiteSpace(partitionId))
            {
                return partitionId;
            }
        }

        return partitionIndex.ToString(CultureInfo.InvariantCulture);
    }

    internal static Dictionary<string, object> CreatePartitionAnnotations(string partitionKey)
        => new(StringComparer.Ordinal)
        {
            [AmqpMessageAnnotations.KeyPartitionKey] = partitionKey,
        };

    internal static bool TryDecodeData(string data, out byte[] rented, out int dataLength, out string? error)
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

    internal static long NextSyntheticSequenceNumber()
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

    internal static Task WriteSendErrorAsync(HttpContext context, EventHubsAmqpException ex, string operationName)
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
                $"Azure Event Hubs throttled the {operationName} request.");
        }

        return KinesisErrorResponse.WriteAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "InternalFailureException",
            BuildFailureMessage(ex));
    }

    internal static (string ErrorCode, string ErrorMessage) ResolveBatchFailure(EventHubsAmqpException ex, string operationName)
    {
        if (ex.Kind == EventHubsAmqpFailureKind.Throttled)
        {
            return ("ProvisionedThroughputExceededException", $"Azure Event Hubs throttled the {operationName} request.");
        }

        return ("InternalFailure", BuildFailureMessage(ex));
    }

    private static int GetMaxDecodedLength(int encodedLength)
        => ((encodedLength + 3) / 4) * 3;

    private static string BuildFailureMessage(EventHubsAmqpException ex)
    {
        var message = string.Equals(ex.Condition, AmqpErrorCondition.Timeout, StringComparison.Ordinal)
            ? "Azure Event Hubs AMQP send timed out."
            : "Azure Event Hubs AMQP send failed.";

        if (!string.IsNullOrWhiteSpace(ex.Description))
        {
            message += " " + ex.Description;
        }

        return message;
    }
}
