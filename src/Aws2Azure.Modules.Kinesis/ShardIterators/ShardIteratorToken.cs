namespace Aws2Azure.Modules.Kinesis.ShardIterators;

public sealed record ShardIteratorToken(
    string Stream,
    string Shard,
    ShardIteratorType Type,
    string? Position,
    long IssuedAtUnixSeconds,
    string IteratorId = "");
