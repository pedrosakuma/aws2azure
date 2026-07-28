using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.Kinesis.Errors;
using Aws2Azure.Modules.Kinesis.EventHubsRest;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Operations;

internal static class KinesisMetadataSupport
{
    private static readonly byte[] EmptyJsonObject = "{}"u8.ToArray();
    private static readonly double MinUnixTimestampMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly double MaxUnixTimestampMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
    internal static readonly EnhancedMonitoringDescription[] DefaultEnhancedMonitoring =
    [
        new EnhancedMonitoringDescription
        {
            ShardLevelMetrics = [],
        },
    ];

    public static bool TryDeserialize<T>(byte[] body, JsonTypeInfo<T> typeInfo, out T? request, out string? error)
    {
        try
        {
            request = JsonSerializer.Deserialize(body.Length == 0 ? EmptyJsonObject : body, typeInfo);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            request = default;
            error = ex.Message;
            return false;
        }
    }

    public static Task WriteJsonAsync<T>(HttpContext context, T payload, JsonTypeInfo<T> typeInfo)
    {
        PrepareJsonResponse(context);
        return JsonSerializer.SerializeAsync(context.Response.Body, payload, typeInfo, context.RequestAborted);
    }

    public static void PrepareJsonResponse(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = KinesisErrorResponse.ContentType;
        context.Response.Headers["x-amzn-requestid"] = context.TraceIdentifier;
    }

    public static bool TryResolveStreamName(string? streamName, string? streamArn, out string resolvedStreamName, out string? error)
    {
        resolvedStreamName = string.Empty;
        error = null;

        var hasStreamName = !string.IsNullOrWhiteSpace(streamName);
        var hasStreamArn = !string.IsNullOrWhiteSpace(streamArn);

        if (!hasStreamName && !hasStreamArn)
        {
            error = "One of StreamName or StreamARN is required.";
            return false;
        }

        string? arnStreamName = null;
        if (hasStreamArn && !TryParseStreamNameFromArn(streamArn!, out arnStreamName))
        {
            error = "StreamARN must contain ':stream/<name>'.";
            return false;
        }

        if (hasStreamName && hasStreamArn && !string.Equals(streamName, arnStreamName, StringComparison.Ordinal))
        {
            error = "StreamName and StreamARN must refer to the same stream.";
            return false;
        }

        resolvedStreamName = hasStreamName ? streamName! : arnStreamName!;
        return true;
    }

    public static string ResolveEventHubName(EventHubsCredentials credentials, string streamName)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        if (credentials.Streams is not null
            && credentials.Streams.TryGetValue(streamName, out var settings)
            && settings is not null
            && !string.IsNullOrWhiteSpace(settings.EventHubName))
        {
            return settings.EventHubName;
        }

        return streamName;
    }

    public static string ResolveNamespaceFqdn(EventHubsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (!string.IsNullOrWhiteSpace(credentials.Endpoint)
            && Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            return endpointUri.IsDefaultPort ? endpointUri.Host : endpointUri.Authority;
        }

        return credentials.Namespace + ".servicebus.windows.net";
    }

    public static string BuildStreamArn(EventHubsCredentials credentials, string streamName)
        => $"arn:aws:kinesis:azure:{credentials.Namespace}:stream/{streamName}";

    public static string ResolveConsumerGroup(EventHubsCredentials credentials, string streamName)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);

        if (credentials.Streams is not null
            && credentials.Streams.TryGetValue(streamName, out var settings)
            && settings is not null
            && !string.IsNullOrWhiteSpace(settings.ConsumerGroup))
        {
            return settings.ConsumerGroup;
        }

        return "$Default";
    }

    public static double ToUnixTimeSeconds(DateTimeOffset value)
        => value.ToUnixTimeMilliseconds() / 1000d;

    public static Task WriteManagementErrorAsync(HttpContext context, EventHubsManagementException ex, string streamName)
    {
        return ex.StatusCode switch
        {
            HttpStatusCode.NotFound => KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "ResourceNotFoundException",
                $"Stream '{streamName}' was not found in Azure Event Hubs."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "AccessDeniedException",
                "Access denied when calling the Azure Event Hubs management API."),
            // Throttling (HTTP 429) maps to the Kinesis control-plane throttle
            // shape so the AWS SDK retries with back-off. The shared
            // AzureHttpClient passes 429 through without internal retry.
            HttpStatusCode.TooManyRequests => KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "LimitExceededException",
                "Azure Event Hubs throttled the management request; retry with back-off."),
            _ => KinesisErrorResponse.WriteAsync(
                context,
                StatusCodes.Status502BadGateway,
                "InternalFailure",
                $"Azure Event Hubs management API returned HTTP {(int)ex.StatusCode}.")
        };
    }

    public static bool TryApplyShardPagination(
        IReadOnlyList<MappedShard> shards,
        string? exclusiveStartShardId,
        int? limit,
        out KinesisShardDescription[] page,
        out bool hasMore,
        out string? error)
    {
        page = [];
        hasMore = false;
        error = null;

        if (limit is <= 0)
        {
            error = "Limit/MaxResults must be greater than zero.";
            return false;
        }

        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(exclusiveStartShardId))
        {
            startIndex = -1;
            for (var i = 0; i < shards.Count; i++)
            {
                if (string.Equals(shards[i].ShardId, exclusiveStartShardId, StringComparison.Ordinal))
                {
                    startIndex = i + 1;
                    break;
                }
            }

            if (startIndex < 0)
            {
                error = $"Shard '{exclusiveStartShardId}' was not found for the stream.";
                return false;
            }
        }

        var take = limit ?? (shards.Count - startIndex);
        if (take < 0)
        {
            take = 0;
        }

        var actual = Math.Min(take, Math.Max(0, shards.Count - startIndex));
        page = new KinesisShardDescription[actual];
        for (var i = 0; i < actual; i++)
        {
            page[i] = ToKinesisShard(shards[startIndex + i]);
        }

        hasMore = startIndex + actual < shards.Count;
        return true;
    }

    public static bool TryValidateListShardsFilter(ShardFilterRequest? filter, out string? error)
    {
        error = null;
        if (filter is null || string.IsNullOrWhiteSpace(filter.Type))
        {
            return true;
        }

        if (string.Equals(filter.Type, "AT_LATEST", StringComparison.Ordinal)
            || string.Equals(filter.Type, "AT_TRIM_HORIZON", StringComparison.Ordinal)
            || string.Equals(filter.Type, "FROM_TRIM_HORIZON", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(filter.ShardId))
            {
                error = $"ShardFilter.ShardId is not supported for {filter.Type}.";
                return false;
            }

            if (filter.Timestamp.HasValue)
            {
                error = $"ShardFilter.Timestamp is not supported for {filter.Type}.";
                return false;
            }

            return true;
        }

        if (string.Equals(filter.Type, "AFTER_SHARD_ID", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(filter.ShardId))
            {
                error = "ShardFilter.ShardId is required for AFTER_SHARD_ID.";
                return false;
            }

            if (filter.Timestamp.HasValue)
            {
                error = "ShardFilter.Timestamp is not supported for AFTER_SHARD_ID.";
                return false;
            }

            return true;
        }

        if (string.Equals(filter.Type, "AT_TIMESTAMP", StringComparison.Ordinal)
            || string.Equals(filter.Type, "FROM_TIMESTAMP", StringComparison.Ordinal))
        {
            if (!filter.Timestamp.HasValue)
            {
                error = $"ShardFilter.Timestamp is required for {filter.Type}.";
                return false;
            }

            if (!TryValidateUnixTimestampSeconds(filter.Timestamp.Value))
            {
                error = $"ShardFilter.Timestamp is invalid for {filter.Type}.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(filter.ShardId))
            {
                error = $"ShardFilter.ShardId is not supported for {filter.Type}.";
                return false;
            }

            return true;
        }

        error = $"ShardFilter.Type '{filter.Type}' is not supported.";
        return false;
    }

    public static bool TryResolveListShardsStartShard(
        EventHubDescription eventHub,
        ShardFilterRequest? filter,
        string? exclusiveStartShardId,
        out string? effectiveExclusiveStartShardId,
        out bool returnEmpty,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(eventHub);

        effectiveExclusiveStartShardId = exclusiveStartShardId;
        returnEmpty = false;
        error = null;

        if (!string.IsNullOrWhiteSpace(exclusiveStartShardId)
            && !TryResolveShardIndex(eventHub.MappedShards, exclusiveStartShardId, out _))
        {
            error = $"Shard '{exclusiveStartShardId}' was not found for the stream.";
            return false;
        }

        if (filter is null || string.IsNullOrWhiteSpace(filter.Type))
        {
            return true;
        }

        if (string.Equals(filter.Type, "AFTER_SHARD_ID", StringComparison.Ordinal))
        {
            if (!TryResolveShardIndex(eventHub.MappedShards, filter.ShardId!, out var filterIndex))
            {
                error = $"Shard '{filter.ShardId}' was not found for the stream.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(exclusiveStartShardId)
                && TryResolveShardIndex(eventHub.MappedShards, exclusiveStartShardId, out var exclusiveStartIndex)
                && exclusiveStartIndex > filterIndex)
            {
                effectiveExclusiveStartShardId = exclusiveStartShardId;
                return true;
            }

            effectiveExclusiveStartShardId = filter.ShardId;
            return true;
        }

        if (string.Equals(filter.Type, "AT_TIMESTAMP", StringComparison.Ordinal))
        {
            var timestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(filter.Timestamp!.Value * 1000d);
            if (timestamp < eventHub.CreatedAt)
            {
                effectiveExclusiveStartShardId = null;
                returnEmpty = true;
            }

            return true;
        }

        return true;
    }

    public static bool TryResolveShard(EventHubDescription eventHub, string shardId, out MappedShard? shard)
    {
        ArgumentNullException.ThrowIfNull(eventHub);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        var mappedShards = eventHub.MappedShards;
        for (var i = 0; i < mappedShards.Count; i++)
        {
            if (string.Equals(mappedShards[i].ShardId, shardId, StringComparison.Ordinal))
            {
                shard = mappedShards[i];
                return true;
            }
        }

        shard = null;
        return false;
    }

    private static bool TryParseStreamNameFromArn(string streamArn, out string? streamName)
    {
        const string marker = ":stream/";
        var index = streamArn.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || index + marker.Length >= streamArn.Length)
        {
            streamName = null;
            return false;
        }

        streamName = streamArn[(index + marker.Length)..];
        return !string.IsNullOrWhiteSpace(streamName);
    }

    private static KinesisShardDescription ToKinesisShard(MappedShard shard)
    {
        return new KinesisShardDescription
        {
            ShardId = shard.ShardId,
            HashKeyRange = new HashKeyRangeDescription
            {
                StartingHashKey = shard.StartingHashKey.ToString(CultureInfo.InvariantCulture),
                EndingHashKey = shard.EndingHashKey.ToString(CultureInfo.InvariantCulture),
            },
            SequenceNumberRange = new SequenceNumberRangeDescription
            {
                StartingSequenceNumber = shard.StartingSequenceNumber,
            },
        };
    }

    private static bool TryResolveShardIndex(IReadOnlyList<MappedShard> shards, string shardId, out int index)
    {
        ArgumentNullException.ThrowIfNull(shards);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        for (var i = 0; i < shards.Count; i++)
        {
            if (string.Equals(shards[i].ShardId, shardId, StringComparison.Ordinal))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static bool TryValidateUnixTimestampSeconds(double value)
    {
        var milliseconds = value * 1000d;
        return !double.IsNaN(value)
               && !double.IsInfinity(value)
               && !double.IsNaN(milliseconds)
               && !double.IsInfinity(milliseconds)
               && milliseconds >= MinUnixTimestampMilliseconds
               && milliseconds <= MaxUnixTimestampMilliseconds;
    }
}
