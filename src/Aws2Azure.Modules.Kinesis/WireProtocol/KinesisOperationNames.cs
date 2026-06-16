using System.Collections.Generic;

namespace Aws2Azure.Modules.Kinesis.WireProtocol;

/// <summary>
/// Maps Kinesis <c>X-Amz-Target</c> values to <see cref="KinesisOperation"/>.
/// Keys are <c>Kinesis_20131202.&lt;Op&gt;</c> exactly as the AWS SDK
/// emits them; lookups are case-sensitive because AWS SDKs are.
/// </summary>
internal static class KinesisOperationNames
{
    public const string TargetPrefix = "Kinesis_20131202.";

    private static readonly Dictionary<string, KinesisOperation> Map = new(System.StringComparer.Ordinal)
    {
        ["PutRecord"] = KinesisOperation.PutRecord,
        ["PutRecords"] = KinesisOperation.PutRecords,
        ["GetRecords"] = KinesisOperation.GetRecords,
        ["GetShardIterator"] = KinesisOperation.GetShardIterator,
        ["DescribeStream"] = KinesisOperation.DescribeStream,
        ["DescribeStreamSummary"] = KinesisOperation.DescribeStreamSummary,
        ["ListShards"] = KinesisOperation.ListShards,
    };

    /// <summary>
    /// The recognised operation short-names (the parse-map keys), used as the
    /// single source of truth for the module's metrics allow-list so the two
    /// cannot drift apart.
    /// </summary>
    public static IReadOnlyCollection<string> Names => Map.Keys;

    public static KinesisOperation FromTarget(string? target)
    {
        if (string.IsNullOrEmpty(target)) return KinesisOperation.Unknown;
        if (!target.StartsWith(TargetPrefix, System.StringComparison.Ordinal))
            return KinesisOperation.Unknown;

        var name = target.Substring(TargetPrefix.Length);
        return Map.TryGetValue(name, out var op) ? op : KinesisOperation.Unknown;
    }

    public static string ToShortName(KinesisOperation op) => op.ToString();
}
