using System.Globalization;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class GetShardIteratorHandler
{
    private static readonly double MinUnixTimestampMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly double MaxUnixTimestampMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    public static async Task HandleAsync(
        HttpContext context,
        KinesisParseResult parseResult,
        EventHubsCredentials credentials,
        IEventHubMetadataCache metadataCache,
        ShardIteratorTokenCodecFactory codecFactory,
        CancellationToken cancellationToken)
    {
        if (!KinesisMetadataSupport.TryDeserialize(
                parseResult.Body,
                KinesisJsonSerializerContext.Default.GetShardIteratorRequest,
                out GetShardIteratorRequest? request,
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

        if (string.IsNullOrWhiteSpace(request?.ShardId))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "ShardId is required.")
                .ConfigureAwait(false);
            return;
        }

        if (!TryParseShardIteratorType(request.ShardIteratorType, out var iteratorType))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", "ShardIteratorType is required and must be a supported value.")
                .ConfigureAwait(false);
            return;
        }

        if (!TryValidateRequest(request, iteratorType, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
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

        if (!KinesisMetadataSupport.TryResolveShard(eventHub, request.ShardId!, out _))
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "ResourceNotFoundException",
                    $"Shard '{request.ShardId}' was not found for stream '{streamName}'.")
                .ConfigureAwait(false);
            return;
        }

        var position = BuildPosition(request, iteratorType);
        var codec = codecFactory.Create(credentials);
        var token = codec.Encode(new ShardIteratorToken(
            streamName,
            request.ShardId!,
            iteratorType,
            position,
            codecFactory.TimeProvider.GetUtcNow().ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("N")));

        await KinesisMetadataSupport.WriteJsonAsync(
                context,
                new GetShardIteratorResponse { ShardIterator = token },
                KinesisJsonSerializerContext.Default.GetShardIteratorResponse)
            .ConfigureAwait(false);
    }

    private static string? BuildPosition(GetShardIteratorRequest request, ShardIteratorType iteratorType)
    {
        return iteratorType switch
        {
            ShardIteratorType.AtSequenceNumber or ShardIteratorType.AfterSequenceNumber => request.StartingSequenceNumber,
            ShardIteratorType.AtTimestamp => DateTimeOffset.UnixEpoch
                .AddMilliseconds(request.Timestamp!.Value * 1000d)
                .UtcDateTime
                .ToString("O", CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static bool TryValidateRequest(GetShardIteratorRequest request, ShardIteratorType iteratorType, out string? error)
    {
        error = null;
        switch (iteratorType)
        {
            case ShardIteratorType.AtSequenceNumber:
            case ShardIteratorType.AfterSequenceNumber:
                if (string.IsNullOrWhiteSpace(request.StartingSequenceNumber))
                {
                    error = "StartingSequenceNumber is required for the selected ShardIteratorType.";
                    return false;
                }

                if (request.Timestamp.HasValue)
                {
                    error = "Timestamp cannot be used with the selected ShardIteratorType.";
                    return false;
                }

                return true;
            case ShardIteratorType.AtTimestamp:
                if (!request.Timestamp.HasValue)
                {
                    error = "Timestamp is required for AT_TIMESTAMP.";
                    return false;
                }

                var timestampSeconds = request.Timestamp.Value;
                var timestampMilliseconds = timestampSeconds * 1000d;
                if (double.IsNaN(timestampSeconds)
                    || double.IsInfinity(timestampSeconds)
                    || double.IsNaN(timestampMilliseconds)
                    || double.IsInfinity(timestampMilliseconds)
                    || timestampMilliseconds < MinUnixTimestampMilliseconds
                    || timestampMilliseconds > MaxUnixTimestampMilliseconds)
                {
                    error = "Timestamp is invalid for AT_TIMESTAMP.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(request.StartingSequenceNumber))
                {
                    error = "StartingSequenceNumber cannot be used with AT_TIMESTAMP.";
                    return false;
                }

                return true;
            case ShardIteratorType.TrimHorizon:
            case ShardIteratorType.Latest:
                if (!string.IsNullOrWhiteSpace(request.StartingSequenceNumber))
                {
                    error = "StartingSequenceNumber is not supported for the selected ShardIteratorType.";
                    return false;
                }

                if (request.Timestamp.HasValue)
                {
                    error = "Timestamp is not supported for the selected ShardIteratorType.";
                    return false;
                }

                return true;
            default:
                error = "ShardIteratorType is required and must be a supported value.";
                return false;
        }
    }

    internal static bool TryParseShardIteratorType(string? value, out ShardIteratorType iteratorType)
    {
        switch (value)
        {
            case "TRIM_HORIZON":
                iteratorType = ShardIteratorType.TrimHorizon;
                return true;
            case "LATEST":
                iteratorType = ShardIteratorType.Latest;
                return true;
            case "AT_SEQUENCE_NUMBER":
                iteratorType = ShardIteratorType.AtSequenceNumber;
                return true;
            case "AFTER_SEQUENCE_NUMBER":
                iteratorType = ShardIteratorType.AfterSequenceNumber;
                return true;
            case "AT_TIMESTAMP":
                iteratorType = ShardIteratorType.AtTimestamp;
                return true;
            default:
                iteratorType = default;
                return false;
        }
    }
}
