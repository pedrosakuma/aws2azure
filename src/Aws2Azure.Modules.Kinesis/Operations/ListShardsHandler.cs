using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.ShardIterators;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class ListShardsHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        KinesisParseResult parseResult,
        EventHubsCredentials credentials,
        IEventHubsManagementClient managementClient,
        ListShardsCursorCodecFactory cursorCodecFactory,
        CancellationToken cancellationToken)
    {
        if (!KinesisMetadataSupport.TryDeserialize(
                parseResult.Body,
                KinesisJsonSerializerContext.Default.ListShardsRequest,
                out ListShardsRequest? request,
                out var parseError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "SerializationException", parseError!)
                .ConfigureAwait(false);
            return;
        }

        if (!KinesisMetadataSupport.TryValidateListShardsFilter(request?.ShardFilter, out var validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        if (!KinesisMetadataSupport.TryValidateShardPaginationLimit(request?.MaxResults, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request?.NextToken)
            && !string.IsNullOrWhiteSpace(request.ExclusiveStartShardId))
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "InvalidArgumentException",
                    "ExclusiveStartShardId cannot be used together with NextToken.")
                .ConfigureAwait(false);
            return;
        }

        var cursorCodec = cursorCodecFactory.Create(credentials);
        string streamName;
        string? startAfterShardId;

        if (!string.IsNullOrWhiteSpace(request?.NextToken))
        {
            if (!string.IsNullOrWhiteSpace(request.StreamName))
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "InvalidArgumentException",
                        "StreamName cannot be used together with NextToken.")
                    .ConfigureAwait(false);
                return;
            }

            if (request.StreamCreationTimestamp.HasValue)
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "InvalidArgumentException",
                        "StreamCreationTimestamp cannot be used together with NextToken.")
                    .ConfigureAwait(false);
                return;
            }

            if (request.ShardFilter is not null)
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "InvalidArgumentException",
                        "ShardFilter cannot be used together with NextToken.")
                    .ConfigureAwait(false);
                return;
            }

            if (!cursorCodec.TryDecode(request.NextToken!, out var decodedCursor, out _))
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "ExpiredNextTokenException",
                        "The supplied NextToken is expired or invalid.")
                    .ConfigureAwait(false);
                return;
            }

            if (!KinesisMetadataSupport.TryResolveStreamName(
                    request.StreamName,
                    request.StreamARN,
                    out var requestedStreamName,
                    out validationError))
            {
                if (!string.IsNullOrWhiteSpace(request.StreamName)
                    || !string.IsNullOrWhiteSpace(request.StreamARN))
                {
                    await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                        .ConfigureAwait(false);
                    return;
                }
            }
            else if (!string.Equals(requestedStreamName, decodedCursor.StreamName, StringComparison.Ordinal))
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "InvalidArgumentException",
                        "StreamName/StreamARN does not match the supplied NextToken.")
                    .ConfigureAwait(false);
                return;
            }
            else if (!KinesisMetadataSupport.TryValidateStreamArnForStream(credentials, request.StreamARN, requestedStreamName, out validationError))
            {
                await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                    .ConfigureAwait(false);
                return;
            }

            streamName = decodedCursor.StreamName;
            startAfterShardId = decodedCursor.StartAfterShardId;
        }
        else if (!KinesisMetadataSupport.TryResolveStreamName(request?.StreamName, request?.StreamARN, out streamName, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                .ConfigureAwait(false);
            return;
        }
        else if (!KinesisMetadataSupport.TryValidateStreamArnForStream(credentials, request?.StreamARN, streamName, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                .ConfigureAwait(false);
            return;
        }
        else
        {
            startAfterShardId = request?.ExclusiveStartShardId;
        }

        var namespaceFqdn = KinesisMetadataSupport.ResolveNamespaceFqdn(credentials);
        var eventHubName = KinesisMetadataSupport.ResolveEventHubName(credentials, streamName);

        EventHubDescription eventHub;
        try
        {
            eventHub = await managementClient.GetEventHubAsync(credentials, namespaceFqdn, eventHubName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EventHubsManagementException ex)
        {
            await KinesisMetadataSupport.WriteManagementErrorAsync(context, ex, streamName).ConfigureAwait(false);
            return;
        }

        if (request?.StreamCreationTimestamp is double streamCreationTimestamp)
        {
            if (!KinesisMetadataSupport.TryGetUnixTimestampMilliseconds(streamCreationTimestamp, out var requestedCreationTimestampMilliseconds))
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "InvalidArgumentException",
                        "StreamCreationTimestamp is invalid.")
                    .ConfigureAwait(false);
                return;
            }

            if (requestedCreationTimestampMilliseconds != eventHub.CreatedAt.ToUnixTimeMilliseconds())
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "ResourceNotFoundException",
                        $"Stream '{streamName}' with the supplied StreamCreationTimestamp was not found.")
                    .ConfigureAwait(false);
                return;
            }
        }

        KinesisShardDescription[] page;
        bool hasMore;
        if (!KinesisMetadataSupport.TryResolveListShardsStartShard(
                eventHub,
                request?.ShardFilter,
                startAfterShardId,
                out var effectiveStartAfterShardId,
                out var returnEmpty,
                out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        if (returnEmpty)
        {
            page = [];
            hasMore = false;
        }
        else if (!KinesisMetadataSupport.TryApplyShardPagination(
                     eventHub.MappedShards,
                     effectiveStartAfterShardId,
                     request?.MaxResults,
                     out page,
                     out hasMore,
                     out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "InvalidArgumentException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        string? nextToken = null;
        if (hasMore && page.Length > 0)
        {
            nextToken = cursorCodec.Encode(new ListShardsCursor(
                streamName,
                page[^1].ShardId,
                cursorCodec.TimeProvider.GetUtcNow().ToUnixTimeSeconds()));
        }

        var response = new ListShardsResponse
        {
            Shards = page,
            NextToken = nextToken,
        };

        await KinesisMetadataSupport.WriteJsonAsync(context, response, KinesisJsonSerializerContext.Default.ListShardsResponse)
            .ConfigureAwait(false);
    }
}
