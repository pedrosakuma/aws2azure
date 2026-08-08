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
        using var scratch = new PooledByteBufferWriter(4096);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        await WriteBufferedJsonResponseAsync(ctx, status, scratch.WrittenMemory, ctx.RequestAborted)
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
    public static Task WriteJsonBufferedAsync<T>(
        HttpContext ctx, int status, T payload, JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var scratch = new PooledByteBufferWriter(4096);
        using (var writer = new Utf8JsonWriter(scratch))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        return WriteBufferedJsonResponseAsync(ctx, status, scratch.WrittenMemory, cancellationToken);
    }

    public static async Task WriteBufferedJsonResponseAsync(
        HttpContext ctx,
        int status,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        ctx.Response.Headers["x-amz-crc32"] = ComputeAwsCrc32Decimal(body.Span);
        await ctx.Response.BodyWriter.WriteAsync(body, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string ComputeAwsCrc32Decimal(ReadOnlySpan<byte> body)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in body)
        {
            crc = Crc32Table[(int)((crc ^ b) & 0xFF)] ^ (crc >> 8);
        }

        return (~crc).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ReadOnlySpan<uint> Crc32Table =>
    [
        0x00000000u, 0x77073096u, 0xEE0E612Cu, 0x990951BAu, 0x076DC419u, 0x706AF48Fu, 0xE963A535u, 0x9E6495A3u,
        0x0EDB8832u, 0x79DCB8A4u, 0xE0D5E91Eu, 0x97D2D988u, 0x09B64C2Bu, 0x7EB17CBDu, 0xE7B82D07u, 0x90BF1D91u,
        0x1DB71064u, 0x6AB020F2u, 0xF3B97148u, 0x84BE41DEu, 0x1ADAD47Du, 0x6DDDE4EBu, 0xF4D4B551u, 0x83D385C7u,
        0x136C9856u, 0x646BA8C0u, 0xFD62F97Au, 0x8A65C9ECu, 0x14015C4Fu, 0x63066CD9u, 0xFA0F3D63u, 0x8D080DF5u,
        0x3B6E20C8u, 0x4C69105Eu, 0xD56041E4u, 0xA2677172u, 0x3C03E4D1u, 0x4B04D447u, 0xD20D85FDu, 0xA50AB56Bu,
        0x35B5A8FAu, 0x42B2986Cu, 0xDBBBC9D6u, 0xACBCF940u, 0x32D86CE3u, 0x45DF5C75u, 0xDCD60DCFu, 0xABD13D59u,
        0x26D930ACu, 0x51DE003Au, 0xC8D75180u, 0xBFD06116u, 0x21B4F4B5u, 0x56B3C423u, 0xCFBA9599u, 0xB8BDA50Fu,
        0x2802B89Eu, 0x5F058808u, 0xC60CD9B2u, 0xB10BE924u, 0x2F6F7C87u, 0x58684C11u, 0xC1611DABu, 0xB6662D3Du,
        0x76DC4190u, 0x01DB7106u, 0x98D220BCu, 0xEFD5102Au, 0x71B18589u, 0x06B6B51Fu, 0x9FBFE4A5u, 0xE8B8D433u,
        0x7807C9A2u, 0x0F00F934u, 0x9609A88Eu, 0xE10E9818u, 0x7F6A0DBBu, 0x086D3D2Du, 0x91646C97u, 0xE6635C01u,
        0x6B6B51F4u, 0x1C6C6162u, 0x856530D8u, 0xF262004Eu, 0x6C0695EDu, 0x1B01A57Bu, 0x8208F4C1u, 0xF50FC457u,
        0x65B0D9C6u, 0x12B7E950u, 0x8BBEB8EAu, 0xFCB9887Cu, 0x62DD1DDFu, 0x15DA2D49u, 0x8CD37CF3u, 0xFBD44C65u,
        0x4DB26158u, 0x3AB551CEu, 0xA3BC0074u, 0xD4BB30E2u, 0x4ADFA541u, 0x3DD895D7u, 0xA4D1C46Du, 0xD3D6F4FBu,
        0x4369E96Au, 0x346ED9FCu, 0xAD678846u, 0xDA60B8D0u, 0x44042D73u, 0x33031DE5u, 0xAA0A4C5Fu, 0xDD0D7CC9u,
        0x5005713Cu, 0x270241AAu, 0xBE0B1010u, 0xC90C2086u, 0x5768B525u, 0x206F85B3u, 0xB966D409u, 0xCE61E49Fu,
        0x5EDEF90Eu, 0x29D9C998u, 0xB0D09822u, 0xC7D7A8B4u, 0x59B33D17u, 0x2EB40D81u, 0xB7BD5C3Bu, 0xC0BA6CADu,
        0xEDB88320u, 0x9ABFB3B6u, 0x03B6E20Cu, 0x74B1D29Au, 0xEAD54739u, 0x9DD277AFu, 0x04DB2615u, 0x73DC1683u,
        0xE3630B12u, 0x94643B84u, 0x0D6D6A3Eu, 0x7A6A5AA8u, 0xE40ECF0Bu, 0x9309FF9Du, 0x0A00AE27u, 0x7D079EB1u,
        0xF00F9344u, 0x8708A3D2u, 0x1E01F268u, 0x6906C2FEu, 0xF762575Du, 0x806567CBu, 0x196C3671u, 0x6E6B06E7u,
        0xFED41B76u, 0x89D32BE0u, 0x10DA7A5Au, 0x67DD4ACCu, 0xF9B9DF6Fu, 0x8EBEEFF9u, 0x17B7BE43u, 0x60B08ED5u,
        0xD6D6A3E8u, 0xA1D1937Eu, 0x38D8C2C4u, 0x4FDFF252u, 0xD1BB67F1u, 0xA6BC5767u, 0x3FB506DDu, 0x48B2364Bu,
        0xD80D2BDAu, 0xAF0A1B4Cu, 0x36034AF6u, 0x41047A60u, 0xDF60EFC3u, 0xA867DF55u, 0x316E8EEFu, 0x4669BE79u,
        0xCB61B38Cu, 0xBC66831Au, 0x256FD2A0u, 0x5268E236u, 0xCC0C7795u, 0xBB0B4703u, 0x220216B9u, 0x5505262Fu,
        0xC5BA3BBEu, 0xB2BD0B28u, 0x2BB45A92u, 0x5CB36A04u, 0xC2D7FFA7u, 0xB5D0CF31u, 0x2CD99E8Bu, 0x5BDEAE1Du,
        0x9B64C2B0u, 0xEC63F226u, 0x756AA39Cu, 0x026D930Au, 0x9C0906A9u, 0xEB0E363Fu, 0x72076785u, 0x05005713u,
        0x95BF4A82u, 0xE2B87A14u, 0x7BB12BAEu, 0x0CB61B38u, 0x92D28E9Bu, 0xE5D5BE0Du, 0x7CDCEFB7u, 0x0BDBDF21u,
        0x86D3D2D4u, 0xF1D4E242u, 0x68DDB3F8u, 0x1FDA836Eu, 0x81BE16CDu, 0xF6B9265Bu, 0x6FB077E1u, 0x18B74777u,
        0x88085AE6u, 0xFF0F6A70u, 0x66063BCAu, 0x11010B5Cu, 0x8F659EFFu, 0xF862AE69u, 0x616BFFD3u, 0x166CCF45u,
        0xA00AE278u, 0xD70DD2EEu, 0x4E048354u, 0x3903B3C2u, 0xA7672661u, 0xD06016F7u, 0x4969474Du, 0x3E6E77DBu,
        0xAED16A4Au, 0xD9D65ADCu, 0x40DF0B66u, 0x37D83BF0u, 0xA9BCAE53u, 0xDEBB9EC5u, 0x47B2CF7Fu, 0x30B5FFE9u,
        0xBDBDF21Cu, 0xCABAC28Au, 0x53B39330u, 0x24B4A3A6u, 0xBAD03605u, 0xCDD70693u, 0x54DE5729u, 0x23D967BFu,
        0xB3667A2Eu, 0xC4614AB8u, 0x5D681B02u, 0x2A6F2B94u, 0xB40BBE37u, 0xC30C8EA1u, 0x5A05DF1Bu, 0x2D02EF8Du,
    ];

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
                await WriteBufferedJsonResponseAsync(ctx, 200, fused!.WrittenMemory, cancellationToken)
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

            await WriteBufferedJsonResponseAsync(ctx, 200, scratch.WrittenMemory, cancellationToken)
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
                await WriteBufferedJsonResponseAsync(ctx, 200, fused!.WrittenMemory, cancellationToken)
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

            await WriteBufferedJsonResponseAsync(ctx, 200, scratch.WrittenMemory, cancellationToken)
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
