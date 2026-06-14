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
}
