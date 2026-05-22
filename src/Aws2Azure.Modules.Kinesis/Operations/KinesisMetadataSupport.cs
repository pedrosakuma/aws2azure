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
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = KinesisErrorResponse.ContentType;
        context.Response.Headers["x-amzn-requestid"] = context.TraceIdentifier;
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, typeInfo));
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
            || string.Equals(filter.Type, "FROM_TRIM_HORIZON", StringComparison.Ordinal))
        {
            return true;
        }

        error = $"ShardFilter.Type '{filter.Type}' is not supported.";
        return false;
    }

    public static bool TryResolveShard(EventHubDescription eventHub, string shardId, out MappedShard? shard)
    {
        ArgumentNullException.ThrowIfNull(eventHub);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        var mappedShards = ShardMapper.MapShards(eventHub.PartitionIds);
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
}
