using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Operations;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Manages Cosmos DB stored procedures for atomic conditional writes.
/// Creates sprocs lazily on first use (mode=Preferred) or validates on startup (mode=Required).
/// </summary>
internal sealed partial class SprocManager
{
    private readonly ILogger<SprocManager> _logger;
    private readonly ConcurrentDictionary<string, SprocState> _sprocCache = new(StringComparer.Ordinal);

    // Versioned so a body change provisions a fresh sproc instead of silently
    // running stale server-side JS (EnsureSproc treats 409 as success and never
    // replaces the body). v2 fixes the invalid mixed-link readDocument bug (#202).
    public const string SprocId = DynamoDbPersistedFormatContract.AtomicWriteStoredProcedureId;

    // Versioned so a future body change provisions a fresh sproc instead of
    // silently running stale server-side JS (EnsureSproc treats 409 as success
    // and never replaces the body).
    public const string TransactSprocId =
        DynamoDbPersistedFormatContract.AtomicTransactWriteStoredProcedureId;

    public SprocManager(ILogger<SprocManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the atomicWrite sproc exists in the given container.
    /// Returns true if sproc is available (exists or was created), false if creation failed.
    /// </summary>
    public async Task<bool> EnsureSprocAsync(CosmosClient cosmos, string containerName, CancellationToken ct)
        => await EnsureNamedSprocAsync(cosmos, containerName, SprocId, SprocBody, ct).ConfigureAwait(false);

    /// <summary>
    /// Executes the atomicWrite sproc for the given operation.
    /// </summary>
    public async Task<SprocExecuteResult> ExecuteAsync(
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        SprocOperation operation,
        string docId,
        ReadOnlyMemory<byte>? payload,
        string? conditionAst,
        string? updateAst,
        CancellationToken ct)
    {
        // POST /dbs/{db}/colls/{coll}/sprocs/atomicWrite
        var sprocLink = $"dbs/{cosmos.DatabaseName}/colls/{containerName}/sprocs/{SprocId}";
        var requestUri = $"/{sprocLink}";

        // Build the JSON array of parameters [op, docId, payload, conditionAst,
        // updateAst] straight into a pooled UTF-8 buffer — the document body is
        // spliced as raw bytes (no string round-trip) and the buffer is sent
        // zero-copy (no StringContent re-encode). Sproc params are inherently
        // text JSON (CosmosBinary does not apply to stored-procedure input).
        using var paramsBuf = new PooledByteBufferWriter(256);
        WriteSingleWriteParams(paramsBuf, operation, docId, payload, conditionAst, updateAst);

        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", $"[\"{EscapeJsonString(partitionKey)}\"]"),
        };

        var response = await cosmos.SendAsync(
            HttpMethod.Post,
            "sprocs",
            sprocLink,
            requestUri,
            paramsBuf.WrittenMemory,
            "application/json",
            headers,
            ct).ConfigureAwait(false);

        return await SprocResponseParser.ParseSingleWriteAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the multi-op <c>atomicTransactWrite</c> sproc exists in the
    /// given container. Cached separately from the single-write sproc.
    /// </summary>
    public async Task<bool> EnsureTransactSprocAsync(CosmosClient cosmos, string containerName, CancellationToken ct)
        => await EnsureNamedSprocAsync(cosmos, containerName, TransactSprocId, TransactSprocBody, ct).ConfigureAwait(false);

    /// <summary>
    /// Executes the <c>atomicTransactWrite</c> sproc with a pre-built JSON
    /// array of operations. The whole array commits atomically within a single
    /// logical partition (Cosmos stored-procedure transaction) or not at all.
    /// </summary>
    public async Task<SprocTransactResult> ExecuteTransactAsync(
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        ReadOnlyMemory<byte> paramsBody,
        CancellationToken ct)
    {
        var sprocLink = $"dbs/{cosmos.DatabaseName}/colls/{containerName}/sprocs/{TransactSprocId}";
        var requestUri = $"/{sprocLink}";

        // paramsBody is the fully-assembled sproc parameter list [ <operations
        // array> ], written once into a pooled UTF-8 buffer by the caller and
        // sent zero-copy (no "[" + s + "]" concat, no StringContent re-encode).
        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", $"[\"{EscapeJsonString(partitionKey)}\"]"),
        };

        var response = await cosmos.SendAsync(
            HttpMethod.Post, "sprocs", sprocLink, requestUri, paramsBody, "application/json", headers, ct).ConfigureAwait(false);

        return await SprocResponseParser.ParseTransactAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<bool> EnsureNamedSprocAsync(
        CosmosClient cosmos, string containerName, string sprocId, string sprocBody, CancellationToken ct)
    {
        var cacheKey = $"{cosmos.DatabaseName}:{containerName}:{sprocId}";

        if (_sprocCache.TryGetValue(cacheKey, out var state) && state == SprocState.Available)
        {
            return true;
        }

        var created = await TryCreateNamedSprocAsync(cosmos, containerName, sprocId, sprocBody, ct).ConfigureAwait(false);
        _sprocCache[cacheKey] = created ? SprocState.Available : SprocState.Failed;
        return created;
    }

    private async Task<bool> TryCreateNamedSprocAsync(
        CosmosClient cosmos, string containerName, string sprocId, string sprocBody, CancellationToken ct)
    {
        var sprocsLink = $"dbs/{cosmos.DatabaseName}/colls/{containerName}";
        var requestUri = $"/{sprocsLink}/sprocs";

        var body = new SprocCreateBody { Id = sprocId, Body = sprocBody };
        var json = JsonSerializer.Serialize(body, SprocJsonContext.Default.SprocCreateBody);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await cosmos.SendAsync(
            HttpMethod.Post, "sprocs", sprocsLink, requestUri, content, extraHeaders: null, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            DynamoDbLog.LogSprocCreated(_logger, containerName);
            return true;
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            DynamoDbLog.LogSprocAlreadyExists(_logger, containerName);
            return true;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        DynamoDbLog.LogSprocCreateFailed(_logger, containerName, (int)response.StatusCode, errorBody);
        return false;
    }

    private enum SprocState { Unknown, Available, Failed }
}
