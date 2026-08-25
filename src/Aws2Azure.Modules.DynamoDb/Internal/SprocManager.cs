using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Buffers;
using Aws2Azure.Modules.DynamoDb.Expressions;
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
    private readonly ConcurrentDictionary<SprocCacheKey, SprocState> _sprocCache = new();

    // atomicWrite_v2 is frozen by the persisted-format inventory. The current
    // single-write body therefore advances under a new ID.
    public const string SprocId = DynamoDbPersistedFormatContract.AtomicWriteStoredProcedureId;

    // Transaction bodies are immutable by ID. v5 starts the durable token window
    // at completion without mutating the frozen v2/v3/v4 bodies.
    public const string TransactSprocId =
        DynamoDbPersistedFormatContract.AtomicTransactWriteStoredProcedureId;

    public const string TransactGetSprocId =
        DynamoDbPersistedFormatContract.AtomicTransactGetStoredProcedureId;

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
        ConditionNode? conditionAst,
        UpdateExpressionAst? updateAst,
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
        WriteSingleWriteParams(
            paramsBuf,
            operation,
            docId,
            payload,
            conditionAst,
            updateAst);

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

        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                InvalidateNamedSproc(cosmos, containerName, SprocId);
                if (await EnsureNamedSprocAsync(
                        cosmos,
                        containerName,
                        SprocId,
                        SprocBody,
                        ct).ConfigureAwait(false))
                {
                    response.Dispose();
                    response = await cosmos.SendAsync(
                        HttpMethod.Post,
                        "sprocs",
                        sprocLink,
                        requestUri,
                        paramsBuf.WrittenMemory,
                        "application/json",
                        headers,
                        ct).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        InvalidateNamedSproc(cosmos, containerName, SprocId);
                    }
                }
            }

            return await SprocResponseParser.ParseSingleWriteAsync(response, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Ensures the multi-op <c>atomicTransactWrite</c> sproc exists in the
    /// given container. Cached separately from the single-write sproc.
    /// </summary>
    public async Task<bool> EnsureTransactSprocAsync(CosmosClient cosmos, string containerName, CancellationToken ct)
        => await EnsureNamedSprocAsync(
                cosmos,
                containerName,
                TransactSprocId,
                TransactSprocBody,
                transactionRoute: null,
                ct)
            .ConfigureAwait(false);

    public async Task<bool> EnsureTransactSprocAsync(
        CosmosClient cosmos,
        string containerName,
        CosmosTransactionRoute transactionRoute,
        CancellationToken ct)
        => await EnsureNamedSprocAsync(
                cosmos,
                containerName,
                TransactSprocId,
                TransactSprocBody,
                transactionRoute,
                ct)
            .ConfigureAwait(false);

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
        => await ExecuteTransactCoreAsync(
                cosmos,
                containerName,
                partitionKey,
                paramsBody,
                transactionRoute: null,
                ct)
            .ConfigureAwait(false);

    public async Task<SprocTransactResult> ExecuteTransactAsync(
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        ReadOnlyMemory<byte> paramsBody,
        CosmosTransactionRoute transactionRoute,
        CancellationToken ct)
        => await ExecuteTransactCoreAsync(
                cosmos,
                containerName,
                partitionKey,
                paramsBody,
                transactionRoute,
                ct)
            .ConfigureAwait(false);

    private async Task<SprocTransactResult> ExecuteTransactCoreAsync(
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        ReadOnlyMemory<byte> paramsBody,
        CosmosTransactionRoute? transactionRoute,
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

        var response = transactionRoute is { } pinned
            ? await cosmos.SendTransactionAsync(
                    pinned,
                    HttpMethod.Post,
                    "sprocs",
                    sprocLink,
                    requestUri,
                    paramsBody,
                    "application/json",
                    headers,
                    ct,
                    noRetry: true)
                .ConfigureAwait(false)
            : await cosmos.SendAsync(
                    HttpMethod.Post,
                    "sprocs",
                    sprocLink,
                    requestUri,
                    paramsBody,
                    "application/json",
                    headers,
                    ct,
                    noRetry: true)
                .ConfigureAwait(false);

        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                InvalidateNamedSproc(cosmos, containerName, TransactSprocId);
                if (await EnsureNamedSprocAsync(
                        cosmos,
                        containerName,
                        TransactSprocId,
                        TransactSprocBody,
                        transactionRoute,
                        ct).ConfigureAwait(false))
                {
                    response.Dispose();
                    response = transactionRoute is { } retryPinned
                        ? await cosmos.SendTransactionAsync(
                                retryPinned,
                                HttpMethod.Post,
                                "sprocs",
                                sprocLink,
                                requestUri,
                                paramsBody,
                                "application/json",
                                headers,
                                ct,
                                noRetry: true)
                            .ConfigureAwait(false)
                        : await cosmos.SendAsync(
                                HttpMethod.Post,
                                "sprocs",
                                sprocLink,
                                requestUri,
                                paramsBody,
                                "application/json",
                                headers,
                                ct,
                                noRetry: true)
                            .ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        InvalidateNamedSproc(cosmos, containerName, TransactSprocId);
                    }
                }
            }

            return await SprocResponseParser.ParseTransactAsync(response, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    public async Task<bool> EnsureTransactGetSprocAsync(
        CosmosClient cosmos,
        string containerName,
        CancellationToken ct)
        => await EnsureNamedSprocAsync(
            cosmos,
            containerName,
            TransactGetSprocId,
            TransactGetSprocBody,
            transactionRoute: null,
            ct).ConfigureAwait(false);

    public async Task<bool> EnsureTransactGetSprocAsync(
        CosmosClient cosmos,
        string containerName,
        CosmosTransactionRoute transactionRoute,
        CancellationToken ct)
        => await EnsureNamedSprocAsync(
                cosmos,
                containerName,
                TransactGetSprocId,
                TransactGetSprocBody,
                transactionRoute,
                ct)
            .ConfigureAwait(false);

    public async Task<SprocTransactGetResult> ExecuteTransactGetAsync(
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        IReadOnlyList<string> documentIds,
        CancellationToken ct)
        => await ExecuteTransactGetCoreAsync(
                cosmos,
                containerName,
                partitionKey,
                documentIds,
                transactionRoute: null,
                ct)
            .ConfigureAwait(false);

    public async Task<SprocTransactGetResult> ExecuteTransactGetAsync(
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        IReadOnlyList<string> documentIds,
        CosmosTransactionRoute transactionRoute,
        CancellationToken ct)
        => await ExecuteTransactGetCoreAsync(
                cosmos,
                containerName,
                partitionKey,
                documentIds,
                transactionRoute,
                ct)
            .ConfigureAwait(false);

    private async Task<SprocTransactGetResult> ExecuteTransactGetCoreAsync(
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        IReadOnlyList<string> documentIds,
        CosmosTransactionRoute? transactionRoute,
        CancellationToken ct)
    {
        var sprocLink =
            $"dbs/{cosmos.DatabaseName}/colls/{containerName}/sprocs/{TransactGetSprocId}";
        var requestUri = $"/{sprocLink}";

        using var paramsBuf = new PooledByteBufferWriter(256);
        using (var writer = new Utf8JsonWriter(paramsBuf))
        {
            writer.WriteStartArray();
            writer.WriteStartArray();
            for (var i = 0; i < documentIds.Count; i++)
            {
                writer.WriteStringValue(documentIds[i]);
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
            writer.Flush();
        }

        var headers = new[]
        {
            new KeyValuePair<string, string>(
                "x-ms-documentdb-partitionkey",
                $"[\"{EscapeJsonString(partitionKey)}\"]"),
        };

        var response = transactionRoute is { } pinned
            ? await cosmos.SendTransactionAsync(
                    pinned,
                    HttpMethod.Post,
                    "sprocs",
                    sprocLink,
                    requestUri,
                    paramsBuf.WrittenMemory,
                    "application/json",
                    headers,
                    ct,
                    noRetry: true)
                .ConfigureAwait(false)
            : await cosmos.SendAsync(
                    HttpMethod.Post,
                    "sprocs",
                    sprocLink,
                    requestUri,
                    paramsBuf.WrittenMemory,
                    "application/json",
                    headers,
                    ct)
                .ConfigureAwait(false);

        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                InvalidateNamedSproc(cosmos, containerName, TransactGetSprocId);
                if (await EnsureNamedSprocAsync(
                        cosmos,
                        containerName,
                        TransactGetSprocId,
                        TransactGetSprocBody,
                        transactionRoute,
                        ct).ConfigureAwait(false))
                {
                    response.Dispose();
                    response = transactionRoute is { } retryPinned
                        ? await cosmos.SendTransactionAsync(
                                retryPinned,
                                HttpMethod.Post,
                                "sprocs",
                                sprocLink,
                                requestUri,
                                paramsBuf.WrittenMemory,
                                "application/json",
                                headers,
                                ct,
                                noRetry: true)
                            .ConfigureAwait(false)
                        : await cosmos.SendAsync(
                                HttpMethod.Post,
                                "sprocs",
                                sprocLink,
                                requestUri,
                                paramsBuf.WrittenMemory,
                                "application/json",
                                headers,
                                ct)
                            .ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        InvalidateNamedSproc(cosmos, containerName, TransactGetSprocId);
                    }
                }
            }

            return await SprocResponseParser.ParseTransactGetAsync(response, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<bool> EnsureNamedSprocAsync(
        CosmosClient cosmos,
        string containerName,
        string sprocId,
        string sprocBody,
        CancellationToken ct)
        => await EnsureNamedSprocAsync(
                cosmos,
                containerName,
                sprocId,
                sprocBody,
                transactionRoute: null,
                ct)
            .ConfigureAwait(false);

    private async Task<bool> EnsureNamedSprocAsync(
        CosmosClient cosmos,
        string containerName,
        string sprocId,
        string sprocBody,
        CosmosTransactionRoute? transactionRoute,
        CancellationToken ct)
    {
        var cacheKey = CacheKey(cosmos, containerName, sprocId);

        if (_sprocCache.TryGetValue(cacheKey, out var state) && state == SprocState.Available)
        {
            return true;
        }

        var created = await TryCreateNamedSprocAsync(
                cosmos,
                containerName,
                sprocId,
                sprocBody,
                transactionRoute,
                ct)
            .ConfigureAwait(false);
        _sprocCache[cacheKey] = created ? SprocState.Available : SprocState.Failed;
        return created;
    }

    public void InvalidateContainer(CosmosClient cosmos, string containerName)
    {
        ArgumentNullException.ThrowIfNull(cosmos);
        ArgumentException.ThrowIfNullOrEmpty(containerName);
        InvalidateNamedSproc(cosmos, containerName, SprocId);
        InvalidateNamedSproc(cosmos, containerName, TransactSprocId);
        InvalidateNamedSproc(cosmos, containerName, TransactGetSprocId);
    }

    private void InvalidateNamedSproc(
        CosmosClient cosmos,
        string containerName,
        string sprocId)
        => _sprocCache.TryRemove(CacheKey(cosmos, containerName, sprocId), out _);

    private static SprocCacheKey CacheKey(
        CosmosClient cosmos,
        string containerName,
        string sprocId)
        => new(
            cosmos.AccountEndpoint,
            cosmos.DatabaseName,
            containerName,
            sprocId);

    private async Task<bool> TryCreateNamedSprocAsync(
        CosmosClient cosmos,
        string containerName,
        string sprocId,
        string sprocBody,
        CosmosTransactionRoute? transactionRoute,
        CancellationToken ct)
    {
        var sprocsLink = $"dbs/{cosmos.DatabaseName}/colls/{containerName}";
        var requestUri = $"/{sprocsLink}/sprocs";

        var body = new SprocCreateBody { Id = sprocId, Body = sprocBody };
        var json = JsonSerializer.Serialize(body, SprocJsonContext.Default.SprocCreateBody);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = transactionRoute is { } pinned
            ? await cosmos.SendTransactionAsync(
                    pinned,
                    HttpMethod.Post,
                    "sprocs",
                    sprocsLink,
                    requestUri,
                    content,
                    extraHeaders: null,
                    ct)
                .ConfigureAwait(false)
            : await cosmos.SendAsync(
                    HttpMethod.Post,
                    "sprocs",
                    sprocsLink,
                    requestUri,
                    content,
                    extraHeaders: null,
                    ct)
                .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            DynamoDbLog.LogSprocCreated(_logger, containerName);
            return true;
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            if (await VerifyNamedSprocAsync(
                    cosmos,
                    containerName,
                    sprocId,
                    sprocBody,
                    transactionRoute,
                    ct).ConfigureAwait(false))
            {
                DynamoDbLog.LogSprocAlreadyExists(_logger, containerName);
                return true;
            }

            DynamoDbLog.LogSprocCreateFailed(
                _logger,
                containerName,
                (int)response.StatusCode,
                $"Stored procedure '{sprocId}' already exists with an unverifiable or different body.");
            return false;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        DynamoDbLog.LogSprocCreateFailed(_logger, containerName, (int)response.StatusCode, errorBody);
        return false;
    }

    private async Task<bool> VerifyNamedSprocAsync(
        CosmosClient cosmos,
        string containerName,
        string sprocId,
        string expectedBody,
        CosmosTransactionRoute? transactionRoute,
        CancellationToken ct)
    {
        var sprocLink =
            $"dbs/{cosmos.DatabaseName}/colls/{containerName}/sprocs/{sprocId}";
        using var response = transactionRoute is { } pinned
            ? await cosmos.SendTransactionAsync(
                    pinned,
                    HttpMethod.Get,
                    "sprocs",
                    sprocLink,
                    $"/{sprocLink}",
                    content: null,
                    extraHeaders: null,
                    ct)
                .ConfigureAwait(false)
            : await cosmos.SendAsync(
                    HttpMethod.Get,
                    "sprocs",
                    sprocLink,
                    $"/{sprocLink}",
                    content: null,
                    extraHeaders: null,
                    ct)
                .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), sprocId, StringComparison.Ordinal)
                && root.TryGetProperty("body", out var script)
                && script.ValueKind == JsonValueKind.String
                && string.Equals(script.GetString(), expectedBody, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct SprocCacheKey(
        string AccountEndpoint,
        string DatabaseName,
        string ContainerName,
        string SprocId);

    private enum SprocState { Available, Failed }
}
