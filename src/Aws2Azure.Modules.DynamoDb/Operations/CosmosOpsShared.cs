using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
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
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var meta = JsonSerializer.Deserialize(stream, TableMetadataJsonContext.Default.TableMetadata);
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
}
