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
    /// Reads the sidecar metadata doc for <paramref name="tableName"/>.
    /// Returns null when the container exists but has no sidecar (e.g.
    /// created out-of-band) or when the container itself is missing.
    /// Callers map null → ResourceNotFoundException when appropriate.
    /// </summary>
    public static async Task<TableMetadata?> TryReadTableMetadataAsync(
        CosmosClient cosmos, string tableName, CancellationToken ct)
    {
        var docLink = "dbs/" + cosmos.DatabaseName + "/colls/" + tableName + "/docs/" + TableMetadata.DocId;
        var pkHeader = BuildPartitionKeyHeader(TableMetadata.DocId);
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", pkHeader),
        };
        using var resp = await cosmos.SendAsync(
            HttpMethod.Get, "docs", docLink, "/" + docLink,
            content: null, headers, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(stream, TableMetadataJsonContext.Default.TableMetadata);
        }
        catch (JsonException)
        {
            return null;
        }
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
        using var writer = new Utf8JsonWriter(ctx.Response.Body);
        JsonSerializer.Serialize(writer, payload, typeInfo);
        await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }
}
