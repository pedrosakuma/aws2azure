using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Internal;

namespace Aws2Azure.Modules.DynamoDb.Operations;

internal static partial class CosmosOpsShared
{
    /// <summary>
    /// Reads a Cosmos response body into a pooled contiguous buffer. Text JSON
    /// is returned unchanged; Cosmos binary JSON (<c>0x80</c> first byte) is
    /// decoded into canonical UTF-8 JSON before existing parsers see it.
    /// </summary>
    public static async Task<PooledByteBufferWriter> ReadCosmosJsonBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        int initialCapacity = 4096;
        if (content.Headers.ContentLength is > 0 and <= int.MaxValue)
        {
            initialCapacity = (int)content.Headers.ContentLength;
        }

        var input = new PooledByteBufferWriter(initialCapacity);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                Memory<byte> memory = input.GetMemory(4096);
                int read = await stream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                input.Advance(read);
            }

            if (!CosmosBinaryDecoder.IsBinary(input.WrittenMemory.Span))
            {
                return input;
            }

            var output = new PooledByteBufferWriter(Math.Max(4096, input.WrittenMemory.Length));
            try
            {
                CosmosBinaryDecoder.Decode(input.WrittenMemory.Span, output);
                input.Dispose();
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the Cosmos response body into a single contiguous pooled buffer
    /// <b>without</b> decoding a CosmosBinary payload. Lets a caller attempt a
    /// binary-direct transform (via <c>CosmosBinaryReader</c>) and decode to text
    /// only on fallback, instead of always paying the binary→text page decode.
    /// </summary>
    public static async Task<PooledByteBufferWriter> ReadCosmosRawBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        int initialCapacity = 4096;
        if (content.Headers.ContentLength is > 0 and <= int.MaxValue)
        {
            initialCapacity = (int)content.Headers.ContentLength;
        }

        var input = new PooledByteBufferWriter(initialCapacity);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                Memory<byte> memory = input.GetMemory(4096);
                int read = await stream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                input.Advance(read);
            }

            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Materializes a single Cosmos document into a DynamoDB AttributeValue map,
    /// preferring a binary-direct walk (<c>CosmosBinaryReader</c> →
    /// <see cref="Persistence.InferredAttributeStorage.ExtractItemFused"/>) over
    /// the binary→text decode + <see cref="JsonDocument"/> path when Cosmos
    /// returned a CosmosBinary body and the streaming reader can fast-path it.
    /// Records <c>aws2azure_dynamodb_read_decode_path_total{op,path}</c>.
    /// </summary>
    public static async Task<Dictionary<string, JsonElement>?> ReadAndExtractItemAsync(
        HttpContent content, string op, CancellationToken cancellationToken)
    {
        using var raw = await ReadCosmosRawBodyAsync(content, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> body = raw.WrittenMemory;

        if (CosmosBinaryDecoder.IsBinary(body.Span))
        {
            if (TryExtractItemBinary(body.Span, out Dictionary<string, JsonElement>? item))
            {
                DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeBinary);
                return item;
            }

            DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeFallback);
            using var decoded = new PooledByteBufferWriter(Math.Max(4096, body.Length));
            CosmosBinaryDecoder.Decode(body.Span, decoded);
            return ExtractItemTextStreaming(decoded.WrittenMemory.Span);
        }

        DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeText);
        return ExtractItemTextStreaming(body.Span);
    }

    /// <summary>
    /// Materializes a single Cosmos document into an AttributeValue map via a
    /// streaming <see cref="Utf8JsonTokenReader"/> +
    /// <see cref="Persistence.InferredAttributeStorage.ExtractItemFused"/> — the
    /// text twin of the binary-direct <see cref="TryExtractItemBinary"/>. Avoids
    /// the per-document whole-DOM <see cref="JsonDocument.Parse"/> so the text
    /// point-read materialization allocates on par with the binary path. Output
    /// is identical to the former <c>JsonDocument.Parse → ExtractItem(JsonElement)</c>
    /// walk (pinned by the streaming-vs-DOM parity tests).
    /// </summary>
    private static Dictionary<string, JsonElement>? ExtractItemTextStreaming(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonTokenReader(json);
        return Persistence.InferredAttributeStorage.ExtractItemFused(ref reader);
    }

    /// <summary>
    /// Builds the AttributeValue map for a single Cosmos document straight off
    /// the binary body via <see cref="CosmosBinaryReader"/> (no decode-to-text
    /// pass). Returns <c>true</c> with the map on success; returns <c>false</c>
    /// (map <c>null</c>) if the streaming reader hits a marker it does not
    /// fast-path, so the caller can fall back to the proven decode-to-text path.
    /// Mirrors <see cref="TryWriteFusedBinaryEnvelope"/>.
    /// </summary>
    private static bool TryExtractItemBinary(
        ReadOnlySpan<byte> binaryBody, out Dictionary<string, JsonElement>? item)
    {
        try
        {
            var reader = new CosmosBinaryReader(binaryBody);
            try
            {
                item = Persistence.InferredAttributeStorage.ExtractItemFused(ref reader);
            }
            finally
            {
                reader.Dispose();
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            item = null;
            return false;
        }
    }

    /// <summary>
    /// Materializes every document of a Cosmos query/feed page into an ordered
    /// list of DynamoDB AttributeValue maps, preferring a binary-direct walk
    /// (<c>CosmosBinaryReader</c> →
    /// <see cref="Persistence.InferredAttributeStorage.ExtractItemsFused"/> over
    /// the binary→text page decode + full-page <see cref="JsonDocument"/> DOM
    /// when Cosmos returned a CosmosBinary body and the streaming reader can
    /// fast-path it. The whole-page fallback is atomic: a mid-page reader
    /// decline discards the partially-built list and re-materializes the page
    /// via decode-to-text. Records
    /// <c>aws2azure_dynamodb_read_decode_path_total{op,path}</c>.
    /// </summary>
    public static async Task<List<Dictionary<string, JsonElement>>> ReadAndExtractItemsAsync(
        HttpContent content, string op, CancellationToken cancellationToken)
    {
        using var raw = await ReadCosmosRawBodyAsync(content, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> body = raw.WrittenMemory;
        var items = new List<Dictionary<string, JsonElement>>();

        if (CosmosBinaryDecoder.IsBinary(body.Span))
        {
            if (TryExtractItemsBinary(body.Span, items))
            {
                DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeBinary);
                return items;
            }

            items.Clear();
            DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeFallback);
            using var decoded = new PooledByteBufferWriter(Math.Max(4096, body.Length));
            CosmosBinaryDecoder.Decode(body.Span, decoded);
            ExtractItemsTextStreaming(decoded.WrittenMemory.Span, items);
            return items;
        }

        DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeText);
        ExtractItemsTextStreaming(body.Span, items);
        return items;
    }

    /// <summary>
    /// Materializes a Cosmos query/feed page's <c>Documents</c> into
    /// AttributeValue maps via a streaming <see cref="Utf8JsonTokenReader"/> —
    /// the same reader-agnostic <see cref="Persistence.InferredAttributeStorage.ExtractItemsFused"/>
    /// the CosmosBinary path uses. This avoids the whole-page
    /// <see cref="JsonDocument"/> DOM (which would parse every document, every
    /// untransformed value, and the Cosmos system fields) so the text read path
    /// allocates on par with the binary path: only the small per-document
    /// re-serialize-and-parse the AttributeValue map itself requires. Output is
    /// element-for-element identical to the former
    /// <c>JsonDocument.Parse → ExtractItem(JsonElement)</c> walk (pinned by the
    /// streaming-vs-DOM parity tests).
    /// </summary>
    private static void ExtractItemsTextStreaming(
        ReadOnlySpan<byte> json, List<Dictionary<string, JsonElement>> sink)
    {
        var reader = new Utf8JsonTokenReader(json);
        Persistence.InferredAttributeStorage.ExtractItemsFused(ref reader, sink);
    }

    /// <summary>
    /// Materializes a Cosmos query/feed page straight off the binary body via
    /// <see cref="CosmosBinaryReader"/> (no decode-to-text), appending each
    /// document's map to <paramref name="sink"/>. Returns <c>true</c> on
    /// success; returns <c>false</c> if the streaming reader hits a marker it
    /// does not fast-path (the caller must discard <paramref name="sink"/> and
    /// fall back). Mirrors <see cref="TryExtractItemBinary"/>.
    /// </summary>
    private static bool TryExtractItemsBinary(
        ReadOnlySpan<byte> binaryBody, List<Dictionary<string, JsonElement>> sink)
    {
        try
        {
            var reader = new CosmosBinaryReader(binaryBody);
            try
            {
                Persistence.InferredAttributeStorage.ExtractItemsFused(ref reader, sink);
            }
            finally
            {
                reader.Dispose();
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
