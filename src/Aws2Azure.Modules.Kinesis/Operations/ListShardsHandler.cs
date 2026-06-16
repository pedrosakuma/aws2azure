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
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request?.NextToken)
            && !string.IsNullOrWhiteSpace(request.ExclusiveStartShardId))
        {
            await KinesisErrorResponse.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "ValidationException",
                    "ExclusiveStartShardId cannot be used together with NextToken.")
                .ConfigureAwait(false);
            return;
        }

        var cursorCodec = cursorCodecFactory.Create(credentials);
        string streamName;
        string? startAfterShardId;

        if (!string.IsNullOrWhiteSpace(request?.NextToken))
        {
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

            if (!string.IsNullOrWhiteSpace(request.StreamName)
                && !string.Equals(request.StreamName, decodedCursor.StreamName, StringComparison.Ordinal))
            {
                await KinesisErrorResponse.WriteAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "ValidationException",
                        "StreamName does not match the supplied NextToken.")
                    .ConfigureAwait(false);
                return;
            }

            streamName = decodedCursor.StreamName;
            startAfterShardId = decodedCursor.StartAfterShardId;
        }
        else if (!KinesisMetadataSupport.TryResolveStreamName(request?.StreamName, streamArn: null, out streamName, out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
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

        if (!KinesisMetadataSupport.TryApplyShardPagination(
                eventHub.MappedShards,
                startAfterShardId,
                request?.MaxResults,
                out var page,
                out var hasMore,
                out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
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
