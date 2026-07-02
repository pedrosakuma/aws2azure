using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Errors;
using Aws2Azure.Modules.DynamoDb.Internal;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// Helpers shared by every Cosmos-backed DynamoDB op handler:
/// partition-key header construction, Cosmos → DynamoDB error mapping,
/// table-metadata sidecar lookup, and JSON response writing. Keeping
/// this in one place stops Slice N handlers from quietly diverging on
/// the error mapping or the partition-key encoding.
/// </summary>
internal static class CosmosOpsShared
{
    /// <summary>
    /// Builds the Cosmos partition-key header value
    /// <c>["&lt;value&gt;"]</c> with proper JSON escaping. Done by hand
    /// because the array form is fixed and going through
    /// <c>JsonSerializer</c> would either drag in reflection or require
    /// a dedicated source-gen context for a one-line literal.
    /// </summary>
    public static string BuildPartitionKeyHeader(string value)
    {
        return "[\"" + JsonEncodedText.Encode(value).ToString() + "\"]";
    }

    /// <summary>
    /// Outcome of a metadata-read call:
    /// <see cref="Status"/> distinguishes Found from NotFound (Cosmos 404)
    /// vs a Cosmos error (auth/throttle/server). For error outcomes the
    /// raw <see cref="HttpResponseMessage"/> is held so callers can pass
    /// it through <see cref="WriteCosmosErrorAsync"/> without losing the
    /// 429/5xx classification.
    /// </summary>
    internal sealed class TableMetadataReadResult : IDisposable
    {
        public TableMetadataReadStatus Status { get; init; }
        public TableMetadata? Metadata { get; init; }
        public HttpResponseMessage? ErrorResponse { get; init; }
        public void Dispose() => ErrorResponse?.Dispose();
    }

    internal enum TableMetadataReadStatus { Found, NotFound, CosmosError }

    /// <summary>
    /// Global cache for table metadata. Shared across all requests.
    /// TTL is 5 minutes by default. Invalidated on table lifecycle operations.
    /// </summary>
    internal static readonly TableMetadataCache MetadataCache = new();

    /// <summary>
    /// Reads the sidecar metadata doc for <paramref name="tableName"/>.
    /// Returns a tri-state result so callers can distinguish a true
    /// missing-table (Cosmos 404) from a Cosmos error response (which
    /// must NOT be reported as ResourceNotFoundException — e.g. 429
    /// must surface as ProvisionedThroughputExceededException, 401/403
    /// as AccessDeniedException). A malformed sidecar (valid 200 body
    /// that fails to deserialize) is also reported as NotFound since the
    /// table is effectively unusable.
    /// 
    /// Results are cached in-memory with a 5-minute TTL to avoid
    /// per-request Cosmos roundtrips. Cache is invalidated on
    /// CreateTable/DeleteTable/UpdateTable.
    /// </summary>
    public static async Task<TableMetadataReadResult> TryReadTableMetadataAsync(
        CosmosClient cosmos, string tableName, CancellationToken ct)
    {
        // Check cache first (includes DatabaseName in key to handle multi-database setups)
        var cached = MetadataCache.TryGet(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName);
        if (cached is not null)
        {
            return new TableMetadataReadResult { Status = TableMetadataReadStatus.Found, Metadata = cached };
        }

        // Capture generation before read to prevent stale writes after invalidation
        var generation = MetadataCache.GetGeneration();

        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName + "/docs/" + TableMetadata.DocId;
        var pkHeader = BuildPartitionKeyHeader(TableMetadata.DocId);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
        };
        var resp = await cosmos.SendAsync(
            HttpMethod.Get, "docs", docLink, "/" + docLink,
            content: null, headers, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            resp.Dispose();
            return new TableMetadataReadResult { Status = TableMetadataReadStatus.NotFound };
        }
        if (!resp.IsSuccessStatusCode)
        {
            return new TableMetadataReadResult { Status = TableMetadataReadStatus.CosmosError, ErrorResponse = resp };
        }
        try
        {
            using var body = await ReadCosmosJsonBodyAsync(resp.Content, ct).ConfigureAwait(false);
            var meta = JsonSerializer.Deserialize(
                body.WrittenMemory.Span,
                TableMetadataJsonContext.Default.TableMetadata);
            resp.Dispose();
            if (meta is null) return new TableMetadataReadResult { Status = TableMetadataReadStatus.NotFound };
            
            // Cache the result (only if generation hasn't changed due to invalidation)
            MetadataCache.Set(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName, meta, generation);
            
            return new TableMetadataReadResult { Status = TableMetadataReadStatus.Found, Metadata = meta };
        }
        catch (JsonException)
        {
            resp.Dispose();
            return new TableMetadataReadResult { Status = TableMetadataReadStatus.NotFound };
        }
    }

    /// <summary>
    /// Read-merge-write of the table metadata sidecar under optimistic
    /// concurrency. Loads the sidecar doc, applies <paramref name="mutate"/>,
    /// then persists it with <c>If-Match</c> and a bounded retry on concurrent
    /// modification, invalidating the metadata cache on success. If the sidecar
    /// is absent (e.g. a container created out-of-band) a minimal doc is created.
    /// Writes an AWS error response to <paramref name="ctx"/> and returns false
    /// on any failure (Cosmos error or exhausted conflict retries). The caller is
    /// responsible for validating that the table/container exists first.
    /// </summary>
    internal static async Task<bool> MutateTableMetadataAsync(
        HttpContext ctx, CosmosClient cosmos, string tableName,
        Action<TableMetadata> mutate, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName + "/docs/" + TableMetadata.DocId;
        var collLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName;
        var pkHeader = BuildPartitionKeyHeader(TableMetadata.DocId);
        var readHeaders = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
        };

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            TableMetadata meta;
            string? etag;
            using (var getResp = await cosmos.SendAsync(
                HttpMethod.Get, "docs", docLink, "/" + docLink,
                content: null, readHeaders, ct).ConfigureAwait(false))
            {
                if (getResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    meta = new TableMetadata { TableName = tableName };
                    etag = null;
                }
                else if (!getResp.IsSuccessStatusCode)
                {
                    await WriteCosmosErrorAsync(ctx, getResp, ct).ConfigureAwait(false);
                    return false;
                }
                else
                {
                    using var body = await ReadCosmosJsonBodyAsync(getResp.Content, ct).ConfigureAwait(false);
                    TableMetadata? parsed;
                    try
                    {
                        parsed = JsonSerializer.Deserialize(
                            body.WrittenMemory.Span, TableMetadataJsonContext.Default.TableMetadata);
                    }
                    catch (JsonException ex)
                    {
                        await WriteErrorAsync(ctx, 500, "InternalServerError",
                            "Malformed table metadata: " + ex.Message).ConfigureAwait(false);
                        return false;
                    }
                    if (parsed is null)
                    {
                        await WriteErrorAsync(ctx, 400, "ResourceNotFoundException",
                            $"Cannot do operations on a non-existent table: {tableName}").ConfigureAwait(false);
                        return false;
                    }
                    meta = parsed;
                    etag = ExtractETag(getResp);
                }
            }

            mutate(meta);

            var metaJson = JsonSerializer.Serialize(meta, TableMetadataJsonContext.Default.TableMetadata);
            using var content = new StringContent(metaJson, Encoding.UTF8, "application/json");
            HttpResponseMessage writeResp;
            if (etag is null)
            {
                writeResp = await cosmos.SendAsync(
                    HttpMethod.Post, "docs", collLink, "/" + collLink + "/docs",
                    content, readHeaders, ct).ConfigureAwait(false);
            }
            else
            {
                var writeHeaders = new[]
                {
                    new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
                    new KeyValuePair<string, string>("If-Match", etag),
                };
                writeResp = await cosmos.SendAsync(
                    HttpMethod.Put, "docs", docLink, "/" + docLink,
                    content, writeHeaders, ct).ConfigureAwait(false);
            }

            using (writeResp)
            {
                bool conflict = writeResp.StatusCode == System.Net.HttpStatusCode.PreconditionFailed
                    || (etag is null && writeResp.StatusCode == System.Net.HttpStatusCode.Conflict);
                if (conflict)
                {
                    if (attempt + 1 < maxAttempts) continue;
                    await WriteErrorAsync(ctx, 400, "ResourceInUseException",
                        "Could not update table metadata because it changed concurrently. Retry the request.")
                        .ConfigureAwait(false);
                    return false;
                }
                if (!writeResp.IsSuccessStatusCode)
                {
                    await WriteCosmosErrorAsync(ctx, writeResp, ct).ConfigureAwait(false);
                    return false;
                }
            }

            MetadataCache.Invalidate(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName);
            return true;
        }

        return false;
    }

    private static string? ExtractETag(HttpResponseMessage resp)
    {
        if (resp.Headers.ETag is not null)
        {
            return resp.Headers.ETag.Tag;
        }
        if (resp.Headers.TryGetValues("ETag", out var etags))
        {
            foreach (var value in etags)
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>
    /// True when a Cosmos 404 response carries the <c>x-ms-substatus</c>
    /// header indicating the container itself is missing (sub-status 1003)
    /// rather than just the requested document. Lets item handlers tell
    /// "item not found" (DynamoDB-success) apart from "table deleted
    /// between metadata read and op" (ResourceNotFoundException).
    /// </summary>
    public static bool Is404ContainerMissing(HttpResponseMessage resp)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound) return false;
        if (!resp.Headers.TryGetValues("x-ms-substatus", out var values)) return false;
        foreach (var v in values)
        {
            // 1003 = Collection (container) not found.
            if (string.Equals(v?.Trim(), "1003", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static async Task WriteCosmosErrorAsync(
        HttpContext ctx, HttpResponseMessage cosmosResp, CancellationToken ct)
    {
        var status = (int)cosmosResp.StatusCode;
        var (awsStatus, code) = status switch
        {
            401 or 403 => (400, "AccessDeniedException"),
            408 => (500, "InternalServerError"),
            429 => (400, "ProvisionedThroughputExceededException"),
            503 => (500, "InternalServerError"),
            _ when status >= 500 => (500, "InternalServerError"),
            _ => (400, "ValidationException"),
        };
        string body = string.Empty;
        try { body = await cosmosResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { }
        var message = string.IsNullOrEmpty(body) ? cosmosResp.ReasonPhrase ?? "Cosmos request failed." : body;
        await WriteErrorAsync(ctx, awsStatus, code, message).ConfigureAwait(false);
    }

    public static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message)
        => DynamoDbErrorResponse.WriteAsync(ctx, status, code, message);

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

        // Everything below is synchronous, so the binary reader's span never
        // crosses an await and the pooled buffer stays valid for its lifetime.
        if (CosmosBinaryDecoder.IsBinary(body.Span))
        {
            if (TryExtractItemBinary(body.Span, out Dictionary<string, JsonElement>? item))
            {
                DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeBinary);
                return item;
            }

            // Streaming reader declined a marker: decode to text and materialize
            // via the same streaming extractor (no whole-page JsonDocument DOM).
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
            // Malformed/truncated or not-yet-fast-pathed binary: defer to the
            // authoritative decode-to-text path. Nothing has been written to the
            // response, so the fallback is clean. InvalidOperationException (the
            // transform's malformed-envelope signal) is intentionally NOT caught —
            // the fallback would throw the identical exception.
            item = null;
            return false;
        }
    }

    /// <summary>
    /// Materializes every document of a Cosmos query/feed page into an ordered
    /// list of DynamoDB AttributeValue maps, preferring a binary-direct walk
    /// (<c>CosmosBinaryReader</c> →
    /// <see cref="Persistence.InferredAttributeStorage.ExtractItemsFused"/>) over
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

        // Everything below is synchronous, so the binary reader's span never
        // crosses an await and the pooled buffer stays valid for its lifetime.
        if (CosmosBinaryDecoder.IsBinary(body.Span))
        {
            if (TryExtractItemsBinary(body.Span, items))
            {
                DynamoDbMetrics.RecordReadDecodePath(op, DynamoDbMetrics.DecodeBinary);
                return items;
            }

            // Streaming reader declined a marker mid-page: discard the partial
            // list and re-materialize the whole page. The binary body decodes to
            // text, then the same reader-agnostic streaming extractor walks it —
            // no whole-page JsonDocument DOM.
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

    public static async Task WriteJsonAsync<T>(
        HttpContext ctx, int status, T payload, JsonTypeInfo<T> typeInfo)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        // SerializeAsync flushes to the response stream asynchronously, which
        // is mandatory under TestServer (sync IO disallowed) and the default
        // Kestrel config (AllowSynchronousIO=false). Wrapping a sync
        // Utf8JsonWriter around ctx.Response.Body would issue blocking writes
        // whenever the writer's 16 KB internal buffer fills mid-serialization.
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
            // Fast path: stream the envelope straight off the binary body via
            // CosmosBinaryReader, skipping the decode-to-text materialization.
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

        // A binary body that reaches here means the fused reader declined a
        // marker, so we decode to text; otherwise Cosmos returned text JSON.
        DynamoDbMetrics.RecordGetItemDecodePath(
            isBinary ? DynamoDbMetrics.PathFallback : DynamoDbMetrics.PathText);

        PooledByteBufferWriter? decoded = null;
        try
        {
            if (isBinary)
            {
                decoded = new PooledByteBufferWriter(Math.Max(4096, cosmosJson.Length));
                // Decode may throw on a malformed binary body; the catch below
                // returns the rented buffer to the pool before rethrowing.
                CosmosBinaryDecoder.Decode(cosmosJson.Span, decoded);
                cosmosJson = decoded.WrittenMemory;
            }

            // Pre-size the envelope buffer to the doc length so the build does
            // not start at 1KB and re-grow (Array.Copy) through several doublings
            // for large items — that growth-copy dominated CPU profiling of large
            // GetItem reads. The DDB envelope (per-attribute {"S"|"N":...} tags)
            // runs ~1.5x the Cosmos doc text, so 2x gives headroom with no growth
            // for the common large case; small items stay at the 4096 floor.
            using var scratch = new PooledByteBufferWriter(Math.Max(4096, cosmosJson.Length * 2));
            using (var writer = new Utf8JsonWriter(scratch))
            {
                // The reader lives entirely within this synchronous call; the
                // pooled input buffer stays valid for its whole lifetime.
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
        // Pre-size to ~2x the binary body: the decoded DDB envelope runs ~1.9x
        // the CosmosBinary body, so this avoids the 1KB-start re-grow (Array.Copy)
        // doubling chain that dominated CPU profiling of large fused GetItem reads.
        // Small items stay at the 4096 floor.
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
            // Malformed/truncated or not-yet-fast-pathed binary: fall back to
            // decode-to-text, which surfaces the canonical error (or decodes a
            // marker the streaming reader does not cover) without emitting a
            // partial response. The reader does direct span slicing for speed, so
            // a truncated body can raise an index/range error rather than a
            // JsonException; both mean "the streaming walk cannot trust this body"
            // and must defer to the authoritative decoder. InvalidOperationException
            // (the transform's malformed-envelope signal) is intentionally NOT
            // caught — the fallback path would throw the identical exception.
            scratch.Dispose();
            envelope = null;
            return false;
        }
    }
}
