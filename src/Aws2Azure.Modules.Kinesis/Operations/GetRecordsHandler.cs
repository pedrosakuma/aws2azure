using System.Globalization;
using System.Buffers.Text;
using System.Text.Json;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsAmqp;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class GetRecordsHandler
{
    private const int DefaultLimit = 10_000;
    private const int MaxLimit = 10_000;
    private const int MaxResponseBytes = 10 * 1024 * 1024;

    public static async Task HandleAsync(
        HttpContext context,
        KinesisParseResult parseResult,
        EventHubsCredentials credentials,
        IEventHubMetadataCache metadataCache,
        IEventHubsAmqpReceiver amqpReceiver,
        ShardIteratorTokenCodecFactory codecFactory,
        CancellationToken cancellationToken)
    {
        if (!KinesisMetadataSupport.TryDeserialize(
                parseResult.Body,
                KinesisJsonSerializerContext.Default.GetRecordsRequest,
                out GetRecordsRequest? request,
                out var parseError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "SerializationException", parseError!)
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request?.ShardIterator))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "ShardIterator is required.")
                .ConfigureAwait(false);
            return;
        }

        if (request.Limit is <= 0)
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "Limit must be greater than zero.")
                .ConfigureAwait(false);
            return;
        }

        var limit = Math.Min(request.Limit ?? DefaultLimit, MaxLimit);
        var codec = codecFactory.Create(credentials);
        if (!codec.TryDecode(request.ShardIterator!, out var token, out var verifyError))
        {
            await WriteIteratorDecodeErrorAsync(context, verifyError).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.StreamARN))
        {
            if (!KinesisMetadataSupport.TryResolveStreamName(streamName: null, request.StreamARN, out var streamNameFromArn, out var validationError))
            {
                await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                    .ConfigureAwait(false);
                return;
            }

            if (!string.Equals(streamNameFromArn, token.Stream, StringComparison.Ordinal))
            {
                await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "StreamARN does not match the supplied ShardIterator.")
                    .ConfigureAwait(false);
                return;
            }
        }

        var namespaceFqdn = KinesisMetadataSupport.ResolveNamespaceFqdn(credentials);
        var eventHubName = KinesisMetadataSupport.ResolveEventHubName(credentials, token.Stream);

        EventHubDescription eventHub;
        try
        {
            eventHub = await metadataCache.GetEventHubAsync(credentials, namespaceFqdn, eventHubName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EventHubsManagementException ex)
        {
            await KinesisMetadataSupport.WriteManagementErrorAsync(context, ex, token.Stream).ConfigureAwait(false);
            return;
        }

        if (!KinesisMetadataSupport.TryResolveShard(eventHub, token.Shard, out var shard)
            || !TryResolvePartitionId(shard!, out var partitionId))
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "ResourceNotFoundException",
                    $"Shard '{token.Shard}' was not found for stream '{token.Stream}'.")
                .ConfigureAwait(false);
            return;
        }

        EventHubsReceiveResult receiveResult;
        try
        {
            receiveResult = await amqpReceiver.ReceiveAsync(
                    credentials,
                    namespaceFqdn,
                    eventHubName,
                    KinesisMetadataSupport.ResolveConsumerGroup(credentials, token.Stream),
                    partitionId,
                    TranslatePosition(token),
                    limit,
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EventHubsAmqpException ex)
        {
            await WriteReceiveErrorAsync(context, ex).ConfigureAwait(false);
            return;
        }

        var includedMessages = TrimToResponseSize(receiveResult.Messages);
        var nextToken = BuildNextShardIterator(codec, codecFactory.TimeProvider, token, includedMessages);
        var millisBehindLatest = ComputeMillisBehindLatest(codecFactory.TimeProvider, includedMessages.Count == 0 ? null : includedMessages[^1].EnqueuedTime);

        await WriteGetRecordsResponseAsync(context, includedMessages, nextToken, millisBehindLatest, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static EventHubsReceivePosition TranslatePosition(ShardIteratorToken token)
    {
        return token.Type switch
        {
            ShardIteratorType.TrimHorizon => new EventHubsReceivePosition.FromStart(),
            ShardIteratorType.Latest => new EventHubsReceivePosition.FromLatest(),
            ShardIteratorType.AtTimestamp => TryParseTimestampPosition(token.Position, out var timestamp)
                ? new EventHubsReceivePosition.FromEnqueuedTime(timestamp)
                : new EventHubsReceivePosition.FromStart(),
            ShardIteratorType.AtSequenceNumber => TranslateSequencePosition(token.Position, inclusive: true),
            ShardIteratorType.AfterSequenceNumber => TranslateSequencePosition(token.Position, inclusive: false),
            _ => new EventHubsReceivePosition.FromStart(),
        };
    }

    private static EventHubsReceivePosition TranslateSequencePosition(string? position, bool inclusive)
    {
        if (string.IsNullOrWhiteSpace(position))
        {
            return new EventHubsReceivePosition.FromStart();
        }

        if (position.StartsWith("offset:", StringComparison.Ordinal))
        {
            return new EventHubsReceivePosition.FromOffsetExclusive(position["offset:".Length..]);
        }

        if (position.StartsWith("sequence:", StringComparison.Ordinal)
            && long.TryParse(position["sequence:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var directSequence))
        {
            return new EventHubsReceivePosition.FromSequenceExclusive(directSequence);
        }

        if (position.StartsWith("time:", StringComparison.Ordinal)
            && long.TryParse(position["time:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
        {
            return new EventHubsReceivePosition.FromEnqueuedTime(AdjustSequenceBoundary(DateTimeOffset.FromUnixTimeMilliseconds(millis), inclusive));
        }

        return TryExtractSyntheticSequenceTimestamp(position, out var enqueuedTime)
            ? new EventHubsReceivePosition.FromEnqueuedTime(AdjustSequenceBoundary(enqueuedTime, inclusive))
            : new EventHubsReceivePosition.FromStart();
    }

    private static DateTimeOffset AdjustSequenceBoundary(DateTimeOffset timestamp, bool inclusive)
        => inclusive ? timestamp.AddMilliseconds(-1) : timestamp;

    internal static bool TryExtractSyntheticSequenceTimestamp(string? sequenceNumber, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(sequenceNumber)
            || !long.TryParse(sequenceNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            return false;
        }

        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(parsed >> 20);
        return true;
    }

    private static bool TryParseTimestampPosition(string? position, out DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(position))
        {
            timestamp = default;
            return false;
        }

        if (position.StartsWith("time:", StringComparison.Ordinal)
            && long.TryParse(position["time:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(millis);
            return true;
        }

        return DateTimeOffset.TryParse(
            position,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static List<EventHubsReceivedMessage> TrimToResponseSize(IReadOnlyList<EventHubsReceivedMessage> messages)
    {
        var included = new List<EventHubsReceivedMessage>(messages.Count);
        var totalBytes = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (totalBytes + message.Body.Length > MaxResponseBytes)
            {
                break;
            }

            included.Add(message);
            totalBytes += message.Body.Length;
        }

        return included;
    }

    private static async Task WriteGetRecordsResponseAsync(
        HttpContext context,
        IReadOnlyList<EventHubsReceivedMessage> messages,
        string nextToken,
        long millisBehindLatest,
        CancellationToken cancellationToken)
    {
        KinesisMetadataSupport.PrepareJsonResponse(context);
        using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WritePropertyName("Records"u8);
        writer.WriteStartArray();
        for (var i = 0; i < messages.Count; i++)
        {
            WriteRecord(writer, messages[i]);
        }

        writer.WriteEndArray();
        writer.WriteString("NextShardIterator"u8, nextToken);
        writer.WriteNumber("MillisBehindLatest"u8, millisBehindLatest);
        writer.WritePropertyName("ChildShards"u8);
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteRecord(Utf8JsonWriter writer, EventHubsReceivedMessage message)
    {
        writer.WriteStartObject();

        Span<byte> sequenceNumber = stackalloc byte[20];
        Utf8Formatter.TryFormat(message.SequenceNumber ?? 0L, sequenceNumber, out var sequenceBytes);
        writer.WriteString("SequenceNumber"u8, sequenceNumber[..sequenceBytes]);

        writer.WriteBase64String("Data"u8, message.Body.Span);

        if (message.PartitionKey is not null)
        {
            writer.WriteString("PartitionKey"u8, message.PartitionKey);
        }

        writer.WriteNumber(
            "ApproximateArrivalTimestamp"u8,
            message.EnqueuedTime is { } enqueuedTime
                ? KinesisMetadataSupport.ToUnixTimeSeconds(enqueuedTime)
                : 0d);

        writer.WriteEndObject();
    }

    private static string BuildNextShardIterator(
        ShardIteratorTokenCodec codec,
        TimeProvider timeProvider,
        ShardIteratorToken current,
        IReadOnlyList<EventHubsReceivedMessage> messages)
    {
        var issuedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (messages.Count == 0)
        {
            return codec.Encode(current with { IssuedAtUnixSeconds = issuedAt });
        }

        var last = messages[^1];
        if (!TryBuildContinuationPosition(last, out var position))
        {
            return codec.Encode(current with { IssuedAtUnixSeconds = issuedAt });
        }

        return codec.Encode(new ShardIteratorToken(
            current.Stream,
            current.Shard,
            ShardIteratorType.AfterSequenceNumber,
            position,
            issuedAt));
    }

    private static bool TryBuildContinuationPosition(EventHubsReceivedMessage message, out string position)
    {
        if (!string.IsNullOrWhiteSpace(message.Offset))
        {
            position = "offset:" + message.Offset;
            return true;
        }

        if (message.SequenceNumber is { } sequenceNumber)
        {
            position = "sequence:" + sequenceNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (message.EnqueuedTime is { } enqueuedTime)
        {
            position = "time:" + enqueuedTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return true;
        }

        position = string.Empty;
        return false;
    }

    private static long ComputeMillisBehindLatest(TimeProvider timeProvider, DateTimeOffset? enqueuedTime)
    {
        if (enqueuedTime is null)
        {
            return 0;
        }

        var delta = timeProvider.GetUtcNow() - enqueuedTime.Value;
        if (delta <= TimeSpan.Zero)
        {
            return 0;
        }

        return (long)delta.TotalMilliseconds;
    }

    private static bool TryResolvePartitionId(MappedShard shard, out int partitionId)
        => int.TryParse(shard.PartitionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out partitionId);

    private static Task WriteIteratorDecodeErrorAsync(HttpContext context, ShardIteratorVerifyError error)
    {
        return error switch
        {
            ShardIteratorVerifyError.Expired => KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "ExpiredIteratorException",
                "The provided ShardIterator is expired."),
            ShardIteratorVerifyError.MalformedFormat or ShardIteratorVerifyError.MalformedPayload or ShardIteratorVerifyError.BadSignature
                => KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "InvalidArgumentException",
                    "The provided ShardIterator is invalid."),
            _ => KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "InvalidArgumentException",
                "The provided ShardIterator is invalid."),
        };
    }

    private static Task WriteReceiveErrorAsync(HttpContext context, EventHubsAmqpException ex)
    {
        if (ex.Kind == EventHubsAmqpFailureKind.Auth)
        {
            return KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "AccessDeniedException",
                "Access denied when receiving from Azure Event Hubs over AMQP.");
        }

        if (ex.Kind == EventHubsAmqpFailureKind.Throttled)
        {
            return KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "ProvisionedThroughputExceededException",
                "Azure Event Hubs throttled the GetRecords request.");
        }

        return KinesisErrorResponse.WriteAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "InternalFailureException",
            "Azure Event Hubs AMQP receive failed.");
    }
}
