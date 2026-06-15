using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Aws2Azure.Core.Observability;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Module-local metrics for the DynamoDB translator. Emitted on a static
/// <see cref="Meter"/> that reuses <see cref="ProxyMetrics.MeterName"/> so the
/// proxy's <c>PrometheusExporter</c> (which filters published instruments by
/// meter name) exports these alongside the request-level metrics without any
/// DI plumbing into the static operation handlers. AOT-safe: no reflection,
/// instrument created once at type init.
/// </summary>
internal static class DynamoDbMetrics
{
    private static readonly Meter Meter = new(ProxyMetrics.MeterName, "1.0.0");

    /// <summary>
    /// Counts GetItem responses by the Cosmos decode path taken, tagged
    /// <c>path</c>:
    /// <list type="bullet">
    ///   <item><c>fused</c> — Cosmos returned a CosmosBinary body and it was
    ///   streamed straight into the response envelope via the fused
    ///   <c>CosmosBinaryReader</c> (the fast path).</item>
    ///   <item><c>fallback</c> — Cosmos returned a CosmosBinary body but the
    ///   fused reader declined a marker, so the body was decoded to text first.</item>
    ///   <item><c>text</c> — Cosmos returned ordinary text JSON (binary not
    ///   negotiated or not honored, e.g. against the emulator).</item>
    /// </list>
    /// Lets an operator confirm the opt-in CosmosBinary fast path is actually
    /// active in their topology rather than silently degrading to text.
    /// </summary>
    private static readonly Counter<long> GetItemDecodePath = Meter.CreateCounter<long>(
        "aws2azure_dynamodb_getitem_decode_path_total",
        unit: "{response}",
        description: "GetItem responses by Cosmos decode path (fused / fallback / text).");

    public const string PathFused = "fused";
    public const string PathFallback = "fallback";
    public const string PathText = "text";

    public static void RecordGetItemDecodePath(string path)
        => GetItemDecodePath.Add(1, new KeyValuePair<string, object?>("path", path));

    /// <summary>
    /// Counts multi-item read responses (Query / Scan) by the transform path
    /// taken, tagged <c>op</c> (<c>scan</c>/<c>query</c>) and <c>path</c>:
    /// <list type="bullet">
    ///   <item><c>fused</c> — no FilterExpression and no ProjectionExpression,
    ///   so each Cosmos document was pumped straight into the response
    ///   <c>Items</c> array via <c>WriteTransformedItem</c> with no JsonDocument
    ///   DOM, no AttributeValue map, and no per-item model re-serialization.</item>
    ///   <item><c>materialized</c> — a FilterExpression or ProjectionExpression
    ///   (or <c>Select=COUNT</c>) forced per-item materialization into a map for
    ///   evaluation/projection before serialization.</item>
    /// </list>
    /// Lets an operator confirm the fused fast path is engaged for their
    /// filter-free scans/queries rather than silently materializing.
    /// </summary>
    private static readonly Counter<long> ReadTransformPath = Meter.CreateCounter<long>(
        "aws2azure_dynamodb_read_transform_path_total",
        unit: "{response}",
        description: "Query/Scan responses by transform path (fused / materialized).");

    public const string PathMaterialized = "materialized";

    public static void RecordReadTransformPath(string op, string path)
        => ReadTransformPath.Add(
            1,
            new KeyValuePair<string, object?>("op", op),
            new KeyValuePair<string, object?>("path", path));

    public const string OpScan = "scan";
    public const string OpQuery = "query";

    /// <summary>
    /// Counts materialized read responses by the Cosmos decode path taken,
    /// tagged <c>op</c> (which operation materialized a map) and <c>path</c>:
    /// <list type="bullet">
    ///   <item><c>binary</c> — the AttributeValue map was built straight off the
    ///   CosmosBinary body via <c>CosmosBinaryReader</c> (<c>ExtractItemFused</c>),
    ///   skipping the binary→text page decode + JsonDocument DOM.</item>
    ///   <item><c>fallback</c> — a binary body whose streaming reader declined a
    ///   marker, so the map was built via decode-to-text + JsonDocument.</item>
    ///   <item><c>text</c> — Cosmos returned ordinary text JSON (binary not
    ///   negotiated or not honored, e.g. against the emulator).</item>
    /// </list>
    /// Complements <see cref="GetItemDecodePath"/> (which covers the fused
    /// GetItem envelope) for the paths that must still materialize a map
    /// (ReturnValues, BatchGet/TransactGet, filtered/projected Query/Scan).
    /// </summary>
    private static readonly Counter<long> ReadDecodePath = Meter.CreateCounter<long>(
        "aws2azure_dynamodb_read_decode_path_total",
        unit: "{response}",
        description: "Materialized read responses by Cosmos decode path (binary / fallback / text).");

    public const string DecodeBinary = "binary";
    public const string DecodeFallback = "fallback";
    public const string DecodeText = "text";

    public static void RecordReadDecodePath(string op, string path)
        => ReadDecodePath.Add(
            1,
            new KeyValuePair<string, object?>("op", op),
            new KeyValuePair<string, object?>("path", path));

    public const string OpPut = "put";
    public const string OpDelete = "delete";
    public const string OpUpdate = "update";
    public const string OpBatchGet = "batchget";
    public const string OpTransactGet = "transactget";
}
