using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.Kinesis.ShardIterators;

internal static partial class ShardIteratorTokenCodecFactoryLog
{
    [LoggerMessage(
        EventId = 4401,
        Level = LogLevel.Warning,
        Message = "Event Hubs shard iterator signing key is not configured; using a per-process random key so iterator tokens will be invalid after proxy restarts.")]
    public static partial void UsingEphemeralSigningKey(ILogger logger);
}
