using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Aws2Azure.Modules.Kinesis.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class DescribeStreamSummaryHandler
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
                KinesisJsonSerializerContext.Default.DescribeStreamSummaryRequest,
                out DescribeStreamSummaryRequest? request,
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

        var response = new DescribeStreamSummaryResponse
        {
            StreamDescriptionSummary = new DescribeStreamSummaryResponseBody
            {
                StreamName = streamName,
                StreamARN = KinesisMetadataSupport.BuildStreamArn(credentials, streamName),
                StreamStatus = "ACTIVE",
                StreamCreationTimestamp = KinesisMetadataSupport.ToUnixTimeSeconds(eventHub.CreatedAt),
                RetentionPeriodHours = checked(eventHub.MessageRetentionDays * 24),
                EnhancedMonitoring = KinesisMetadataSupport.DefaultEnhancedMonitoring,
                EncryptionType = "NONE",
                OpenShardCount = eventHub.PartitionCount,
                ConsumerCount = 0,
            },
        };

        await KinesisMetadataSupport.WriteJsonAsync(context, response, KinesisJsonSerializerContext.Default.DescribeStreamSummaryResponse)
            .ConfigureAwait(false);
    }
}
