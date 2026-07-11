using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class CosmosOpsShared
{
    public static async Task WriteJsonAsync<T>(
        HttpContext ctx, int status, T payload, JsonTypeInfo<T> typeInfo)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body, payload, typeInfo, ctx.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes a bounded JSON payload into a pooled off-pipe buffer, then
    /// commits it with a single asynchronous <see cref="System.IO.Pipelines.PipeWriter"/>
    /// write — the "error wall" pattern the GetItem read path uses uniformly
    /// (see <see cref="WriteGetItemEnvelopeAsync"/>). Unlike
    /// <see cref="WriteJsonAsync"/> (which streams to <c>Response.Body</c> and
    /// can auto-flush a partial 200 for a multi-KB item), the whole response is
    /// built before any byte is committed, so a mid-serialization throw leaves
    /// the response uncommitted. Use for materialized responses whose size is
    /// item-bounded and therefore may exceed the serializer's flush threshold.
    /// </summary>
    public static async Task WriteJsonBufferedAsync<T>(
        HttpContext ctx, int status, T payload, JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var scratch = new PooledByteBufferWriter(4096);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        await ctx.Response.BodyWriter.WriteAsync(scratch.WrittenMemory, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the Cosmos response body into a single contiguous pooled buffer and
    /// pumps it through
    /// <see cref="Persistence.InferredAttributeStorage.WriteGetItemEnvelope(Utf8JsonWriter, ReadOnlySpan{byte})"/>
    /// — no <see cref="JsonDocument"/> DOM, no <see cref="JsonElement"/>, and no
    /// per-attribute <see cref="string"/> materialization.
    ///
    /// <para>The envelope is built fully into a pooled
    /// <see cref="PooledByteBufferWriter"/> scratch buffer and only then handed
    /// to <see cref="HttpResponse.BodyWriter"/> in a single
    /// <see cref="System.IO.Pipelines.PipeWriter.WriteAsync"/>. Building into
    /// scratch first preserves the error wall: a malformed Cosmos document that
    /// makes the per-attribute writer throw fails <b>before</b> any byte reaches
    /// the response, so the pipeline can still emit a clean error. The single
    /// <c>WriteAsync</c> is the only socket-facing call, so no synchronous IO is
    /// issued (safe under TestServer / AllowSynchronousIO=false).</para>
    /// </summary>
    public static async Task WriteGetItemEnvelopeAsync(
        HttpContext ctx, System.IO.Stream cosmosBody, CancellationToken cancellationToken)
    {
        using var input = new PooledByteBufferWriter(4096);
        while (true)
        {
            Memory<byte> mem = input.GetMemory(4096);
            int read = await cosmosBody.ReadAsync(mem, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            input.Advance(read);
        }

        ReadOnlyMemory<byte> cosmosJson = input.WrittenMemory;
        bool isBinary = CosmosBinaryDecoder.IsBinary(cosmosJson.Span);
        if (isBinary
            && TryWriteFusedBinaryEnvelope(cosmosJson.Span, out PooledByteBufferWriter? fused))
        {
            DynamoDbMetrics.RecordGetItemDecodePath(DynamoDbMetrics.PathFused);
            using (fused)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-amz-json-1.0";
                await ctx.Response.BodyWriter.WriteAsync(fused!.WrittenMemory, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        DynamoDbMetrics.RecordGetItemDecodePath(
            isBinary ? DynamoDbMetrics.PathFallback : DynamoDbMetrics.PathText);

        PooledByteBufferWriter? decoded = null;
        try
        {
            if (isBinary)
            {
                decoded = new PooledByteBufferWriter(Math.Max(4096, cosmosJson.Length));
                CosmosBinaryDecoder.Decode(cosmosJson.Span, decoded);
                cosmosJson = decoded.WrittenMemory;
            }

            using var scratch = new PooledByteBufferWriter(Math.Max(4096, cosmosJson.Length * 2));
            using (var writer = new Utf8JsonWriter(scratch))
            {
                Persistence.InferredAttributeStorage.WriteGetItemEnvelope(writer, cosmosJson.Span);
                writer.Flush();
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/x-amz-json-1.0";
            await ctx.Response.BodyWriter.WriteAsync(scratch.WrittenMemory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            decoded?.Dispose();
        }
    }

    /// <summary>
    /// Builds the GetItem envelope straight off the binary body via
    /// <see cref="CosmosBinaryReader"/> (no decode-to-text pass), into a pooled
    /// buffer. Returns <c>true</c> with the (caller-owned) buffer on success;
    /// returns <c>false</c> (having disposed the buffer) if the fused reader hits
    /// a marker it does not fast-path, so the caller can fall back to the proven
    /// decode-to-text path. The buffer is committed only after the whole envelope
    /// is built, so a fallback never emits a partial response.
    /// <para>Internal (not private) so the Tier 0 allocation guard
    /// (<c>CosmosFusedEnvelopeAllocTests</c>, issue #420) measures the SHIPPED
    /// fused path, not a reconstruction of it.</para>
    /// </summary>
    internal static bool TryWriteFusedBinaryEnvelope(
        ReadOnlySpan<byte> binaryBody, out PooledByteBufferWriter? envelope)
    {
        var scratch = new PooledByteBufferWriter(Math.Max(4096, binaryBody.Length * 2));
        try
        {
            using (var writer = new Utf8JsonWriter(scratch))
            {
                var reader = new CosmosBinaryReader(binaryBody);
                try
                {
                    Persistence.InferredAttributeStorage.WriteGetItemEnvelope(writer, ref reader);
                }
                finally
                {
                    reader.Dispose();
                }
                writer.Flush();
            }
            envelope = scratch;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            scratch.Dispose();
            envelope = null;
            return false;
        }
    }

    /// <summary>
    /// Projected twin of <see cref="WriteGetItemEnvelopeAsync"/>: streams the
    /// GetItem envelope straight off the Cosmos body while keeping only the
    /// projection's top-level attributes, with no intermediate AttributeValue
    /// map. The projection must be non-nested
    /// (<see cref="Expressions.Projection.HasNestedPaths"/> is <c>false</c>);
    /// nested projections take the materialized <c>Projection.Apply</c> path.
    /// Same binary-fused / decode-to-text / text dispatch and single-write
    /// error-wall discipline as the non-projected path.
    /// </summary>
    public static async Task WriteProjectedGetItemEnvelopeAsync(
        HttpContext ctx, System.IO.Stream cosmosBody, Expressions.Projection projection, CancellationToken cancellationToken)
    {
        using var input = new PooledByteBufferWriter(4096);
        while (true)
        {
            Memory<byte> mem = input.GetMemory(4096);
            int read = await cosmosBody.ReadAsync(mem, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            input.Advance(read);
        }

        ReadOnlyMemory<byte> cosmosJson = input.WrittenMemory;
        bool isBinary = CosmosBinaryDecoder.IsBinary(cosmosJson.Span);
        if (isBinary
            && TryWriteProjectedFusedBinaryEnvelope(cosmosJson.Span, projection, out PooledByteBufferWriter? fused))
        {
            DynamoDbMetrics.RecordGetItemDecodePath(DynamoDbMetrics.PathFused);
            using (fused)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-amz-json-1.0";
                await ctx.Response.BodyWriter.WriteAsync(fused!.WrittenMemory, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        DynamoDbMetrics.RecordGetItemDecodePath(
            isBinary ? DynamoDbMetrics.PathFallback : DynamoDbMetrics.PathText);

        PooledByteBufferWriter? decoded = null;
        try
        {
            if (isBinary)
            {
                decoded = new PooledByteBufferWriter(Math.Max(4096, cosmosJson.Length));
                CosmosBinaryDecoder.Decode(cosmosJson.Span, decoded);
                cosmosJson = decoded.WrittenMemory;
            }

            using var scratch = new PooledByteBufferWriter(Math.Max(4096, cosmosJson.Length * 2));
            using (var writer = new Utf8JsonWriter(scratch))
            {
                Persistence.InferredAttributeStorage.WriteProjectedGetItemEnvelope(writer, cosmosJson.Span, projection);
                writer.Flush();
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/x-amz-json-1.0";
            await ctx.Response.BodyWriter.WriteAsync(scratch.WrittenMemory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            decoded?.Dispose();
        }
    }

    /// <summary>
    /// Projected twin of <see cref="TryWriteFusedBinaryEnvelope"/>: builds the
    /// projected GetItem envelope straight off the binary body via
    /// <see cref="CosmosBinaryReader"/>. Returns <c>true</c> with the caller-owned
    /// buffer on success, or <c>false</c> (buffer disposed) when the fused reader
    /// hits a marker it does not fast-path, so the caller falls back to
    /// decode-to-text. Never emits a partial response.
    /// </summary>
    private static bool TryWriteProjectedFusedBinaryEnvelope(
        ReadOnlySpan<byte> binaryBody, Expressions.Projection projection, out PooledByteBufferWriter? envelope)
    {
        var scratch = new PooledByteBufferWriter(Math.Max(4096, binaryBody.Length * 2));
        try
        {
            using (var writer = new Utf8JsonWriter(scratch))
            {
                var reader = new CosmosBinaryReader(binaryBody);
                try
                {
                    Persistence.InferredAttributeStorage.WriteProjectedGetItemEnvelope(writer, ref reader, projection);
                }
                finally
                {
                    reader.Dispose();
                }
                writer.Flush();
            }
            envelope = scratch;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            scratch.Dispose();
            envelope = null;
            return false;
        }
    }
}
