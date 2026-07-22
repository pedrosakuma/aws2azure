using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
internal static partial class CosmosOpsShared
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
        var cached = MetadataCache.TryGet(cosmos.AccountEndpoint, cosmos.DatabaseName, tableName);
        if (cached is not null)
        {
            return new TableMetadataReadResult { Status = TableMetadataReadStatus.Found, Metadata = cached };
        }

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
            meta.RemoveCosmosSystemExtensionData();

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
                    meta.RemoveCosmosSystemExtensionData();
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
}
