using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class DescribeStreamHandler
{
    public static async Task HandleAsync(
        HttpContext context,
        KinesisParseResult parseResult,
        EventHubsCredentials credentials,
        IEventHubsManagementClient managementClient,
        CancellationToken cancellationToken)
    {
        if (!KinesisMetadataSupport.TryDeserialize(
                parseResult.Body,
                KinesisJsonSerializerContext.Default.DescribeStreamRequest,
                out DescribeStreamRequest? request,
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

        var mappedShards = ShardMapper.MapShards(eventHub.PartitionIds);
        if (!KinesisMetadataSupport.TryApplyShardPagination(
                mappedShards,
                request?.ExclusiveStartShardId,
                request?.Limit,
                out var page,
                out var hasMore,
                out validationError))
        {
            await KinesisErrorResponse.WriteAsync(context, StatusCodes.Status400BadRequest, "ValidationException", validationError!)
                .ConfigureAwait(false);
            return;
        }

        var response = new DescribeStreamResponse
        {
            StreamDescription = new DescribeStreamResponseBody
            {
                StreamName = streamName,
                StreamARN = KinesisMetadataSupport.BuildStreamArn(credentials, streamName),
                StreamStatus = "ACTIVE",
                StreamCreationTimestamp = KinesisMetadataSupport.ToUnixTimeSeconds(eventHub.CreatedAt),
                RetentionPeriodHours = checked(eventHub.MessageRetentionDays * 24),
                EncryptionType = "NONE",
                EnhancedMonitoring = KinesisMetadataSupport.DefaultEnhancedMonitoring,
                Shards = page,
                HasMoreShards = hasMore,
            },
        };

        await KinesisMetadataSupport.WriteJsonAsync(context, response, KinesisJsonSerializerContext.Default.DescribeStreamResponse)
            .ConfigureAwait(false);
    }
}
