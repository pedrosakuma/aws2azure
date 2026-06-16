using System.Collections.Generic;

namespace Aws2Azure.Modules.DynamoDb.WireProtocol;

/// <summary>
/// Maps DynamoDB <c>X-Amz-Target</c> values to <see cref="DynamoDbOperation"/>.
/// Keys are <c>DynamoDB_20120810.&lt;Op&gt;</c> exactly as the AWS SDK
/// emits them; lookups are case-sensitive because AWS SDKs are.
/// </summary>
internal static class DynamoDbOperationNames
{
    public const string TargetPrefix = "DynamoDB_20120810.";

    private static readonly Dictionary<string, DynamoDbOperation> Map = new(System.StringComparer.Ordinal)
    {
        ["GetItem"] = DynamoDbOperation.GetItem,
        ["PutItem"] = DynamoDbOperation.PutItem,
        ["UpdateItem"] = DynamoDbOperation.UpdateItem,
        ["DeleteItem"] = DynamoDbOperation.DeleteItem,
        ["BatchGetItem"] = DynamoDbOperation.BatchGetItem,
        ["BatchWriteItem"] = DynamoDbOperation.BatchWriteItem,
        ["TransactGetItems"] = DynamoDbOperation.TransactGetItems,
        ["TransactWriteItems"] = DynamoDbOperation.TransactWriteItems,
        ["Query"] = DynamoDbOperation.Query,
        ["Scan"] = DynamoDbOperation.Scan,
        ["CreateTable"] = DynamoDbOperation.CreateTable,
        ["DeleteTable"] = DynamoDbOperation.DeleteTable,
        ["DescribeTable"] = DynamoDbOperation.DescribeTable,
        ["ListTables"] = DynamoDbOperation.ListTables,
        ["UpdateTable"] = DynamoDbOperation.UpdateTable,
        ["DescribeTimeToLive"] = DynamoDbOperation.DescribeTimeToLive,
        ["UpdateTimeToLive"] = DynamoDbOperation.UpdateTimeToLive,
        ["TagResource"] = DynamoDbOperation.TagResource,
        ["UntagResource"] = DynamoDbOperation.UntagResource,
        ["ListTagsOfResource"] = DynamoDbOperation.ListTagsOfResource,
        ["DescribeStream"] = DynamoDbOperation.DescribeStream,
        ["GetRecords"] = DynamoDbOperation.GetRecords,
        ["GetShardIterator"] = DynamoDbOperation.GetShardIterator,
        ["ListStreams"] = DynamoDbOperation.ListStreams,
        ["CreateBackup"] = DynamoDbOperation.CreateBackup,
        ["DeleteBackup"] = DynamoDbOperation.DeleteBackup,
        ["DescribeBackup"] = DynamoDbOperation.DescribeBackup,
        ["ListBackups"] = DynamoDbOperation.ListBackups,
        ["RestoreTableFromBackup"] = DynamoDbOperation.RestoreTableFromBackup,
        ["RestoreTableToPointInTime"] = DynamoDbOperation.RestoreTableToPointInTime,
    };

    /// <summary>
    /// Parses a full <c>X-Amz-Target</c> header value
    /// (e.g. <c>"DynamoDB_20120810.PutItem"</c>) into a
    /// <see cref="DynamoDbOperation"/>. Returns
    /// <see cref="DynamoDbOperation.Unknown"/> for missing prefix or
    /// unrecognised op names.
    /// </summary>
    public static DynamoDbOperation FromTarget(string? target)
    {
        if (string.IsNullOrEmpty(target)) return DynamoDbOperation.Unknown;
        if (!target.StartsWith(TargetPrefix, System.StringComparison.Ordinal))
            return DynamoDbOperation.Unknown;

        var name = target.Substring(TargetPrefix.Length);
        return Map.TryGetValue(name, out var op) ? op : DynamoDbOperation.Unknown;
    }

    /// <summary>
    /// Inverse of <see cref="FromTarget"/>: emits the short op name
    /// suitable for error <c>__type</c> bodies. Returns
    /// <c>"Unknown"</c> for <see cref="DynamoDbOperation.Unknown"/>.
    /// </summary>
    public static string ToShortName(DynamoDbOperation op) => op.ToString();

    /// <summary>
    /// All recognised short operation names (the <see cref="Map"/> keys).
    /// Used as the module's <c>KnownOperations</c> allowlist so the
    /// operation metric label stays bounded and every parseable op —
    /// including ones that currently return <c>501 Not Implemented</c> —
    /// is labelled by name rather than collapsing to <c>"unknown"</c>.
    /// </summary>
    public static IReadOnlyCollection<string> ShortNames => Map.Keys;
}
