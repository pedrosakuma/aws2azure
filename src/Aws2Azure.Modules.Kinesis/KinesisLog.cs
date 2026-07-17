using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Kinesis;

internal static partial class KinesisLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Fetching Event Hub metadata for namespace '{NamespaceFqdn}' and entity '{EventHubName}'")]
    public static partial void FetchingEventHub(ILogger logger, string namespaceFqdn, string eventHubName);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Event Hub metadata request for namespace '{NamespaceFqdn}' and entity '{EventHubName}' failed with HTTP {StatusCode}")]
    public static partial void EventHubRequestFailed(
        ILogger logger,
        string namespaceFqdn,
        string eventHubName,
        int statusCode);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Event Hubs list-shards cursor signing key is not configured; using an ephemeral process key.")]
    public static partial void UsingEphemeralListShardsCursorSigningKey(ILogger logger);

    [LoggerMessage(
        EventId = 4401,
        Level = LogLevel.Warning,
        Message = "Event Hubs shard iterator signing key is not configured; using a per-process random key so iterator tokens will be invalid after proxy restarts.")]
    public static partial void UsingEphemeralShardIteratorSigningKey(ILogger logger);
}
