using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    public const string SprocId = "atomicWrite_v2";

    // Versioned so a future body change provisions a fresh sproc instead of
    // silently running stale server-side JS (EnsureSproc treats 409 as success
    // and never replaces the body).
    public const string TransactSprocId = "atomicTransactWrite_v2";

    public SprocManager(ILogger<SprocManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the atomicWrite sproc exists in the given container.
    /// Returns true if sproc is available (exists or was created), false if creation failed.
    /// </summary>
    public async Task<bool> EnsureSprocAsync(CosmosClient cosmos, string containerName, CancellationToken ct)
    {
        var cacheKey = $"{cosmos.DatabaseName}:{containerName}:{SprocId}";

        if (_sprocCache.TryGetValue(cacheKey, out var state) && state == SprocState.Available)
        {
            return true;
        }

        // Try to create (idempotent if already exists)
        var created = await TryCreateSprocAsync(cosmos, containerName, ct).ConfigureAwait(false);
        _sprocCache[cacheKey] = created ? SprocState.Available : SprocState.Failed;
        return created;
    }

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

        return await ParseSprocResponseAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the multi-op <c>atomicTransactWrite</c> sproc exists in the
    /// given container. Cached separately from the single-write sproc.
    /// </summary>
    public async Task<bool> EnsureTransactSprocAsync(CosmosClient cosmos, string containerName, CancellationToken ct)
    {
        var cacheKey = $"{cosmos.DatabaseName}:{containerName}:{TransactSprocId}";

        if (_sprocCache.TryGetValue(cacheKey, out var state) && state == SprocState.Available)
        {
            return true;
        }

        var created = await TryCreateNamedSprocAsync(cosmos, containerName, TransactSprocId, TransactSprocBody, ct).ConfigureAwait(false);
        _sprocCache[cacheKey] = created ? SprocState.Available : SprocState.Failed;
        return created;
    }

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

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            // Condition failure is reported as a 2xx with { success: false }.
            // Parse the body rather than string-matching so whitespace / property
            // ordering from the server can't be misread as a successful commit.
            if (TryReadSuccessFlag(body, out var success) && !success)
            {
                return new SprocTransactResult { Attempted = true, ConditionFailed = true, ResponseBody = body };
            }
            return new SprocTransactResult { Attempted = true, Success = true, ResponseBody = body };
        }

        return new SprocTransactResult
        {
            Attempted = true,
            StatusCode = (int)response.StatusCode,
            ErrorBody = body,
        };
    }

    // Reads the `success` boolean from the sproc response body. Returns false
    // (flag indeterminate) when the body is missing, not JSON, or lacks a
    // boolean `success` property — callers then treat the result as a commit.
    private static bool TryReadSuccessFlag(string? body, out bool success)
    {
        success = false;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("success", out var s)
                && (s.ValueKind == JsonValueKind.True || s.ValueKind == JsonValueKind.False))
            {
                success = s.GetBoolean();
                return true;
            }
        }
        catch (JsonException)
        {
            // Fall through — treat as indeterminate.
        }
        return false;
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
            LogSprocCreated(_logger, containerName);
            return true;
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            LogSprocAlreadyExists(_logger, containerName);
            return true;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        LogSprocCreateFailed(_logger, containerName, (int)response.StatusCode, errorBody);
        return false;
    }

    private async Task<bool> TryCreateSprocAsync(CosmosClient cosmos, string containerName, CancellationToken ct)
    {
        // POST /dbs/{db}/colls/{coll}/sprocs
        var sprocsLink = $"dbs/{cosmos.DatabaseName}/colls/{containerName}";
        var requestUri = $"/{sprocsLink}/sprocs";

        var body = new SprocCreateBody { Id = SprocId, Body = SprocBody };
        var json = JsonSerializer.Serialize(body, SprocJsonContext.Default.SprocCreateBody);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await cosmos.SendAsync(
            HttpMethod.Post,
            "sprocs",
            sprocsLink,
            requestUri,
            content,
            extraHeaders: null,
            ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            LogSprocCreated(_logger, containerName);
            return true;
        }

        // 409 Conflict = already exists, which is fine
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            LogSprocAlreadyExists(_logger, containerName);
            return true;
        }

        var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        LogSprocCreateFailed(_logger, containerName, (int)response.StatusCode, errorBody);
        return false;
    }

    private static async Task<SprocExecuteResult> ParseSprocResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            // Check if body contains conditionFailed: true (sproc returns 200 for condition failures now)
            if (body.Contains("\"conditionFailed\":true") || body.Contains("\"conditionFailed\": true"))
            {
                return new SprocExecuteResult { Success = false, ConditionFailed = true, ResponseBody = body };
            }
            return new SprocExecuteResult { Success = true, ResponseBody = body };
        }

        // Legacy check for thrown condition-failed response (backwards compatibility)
        if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("ConditionalCheckFailedException"))
        {
            return new SprocExecuteResult { Success = false, ConditionFailed = true };
        }

        return new SprocExecuteResult
        {
            Success = false,
            StatusCode = (int)response.StatusCode,
            ErrorBody = body,
        };
    }

    /// <summary>
    /// Writes the single-write sproc parameter list
    /// <c>[op, docId, payload, conditionAst, updateAst]</c> straight into a
    /// pooled UTF-8 buffer. The document <paramref name="payload"/> (and the
    /// already-serialized condition/update ASTs) are spliced as raw JSON bytes,
    /// so there is no <c>byte[] → string → byte[]</c> round-trip. Output is
    /// byte-identical to <see cref="BuildParamsJson"/> (verified by tests).
    /// </summary>
    internal static void WriteSingleWriteParams(
        PooledByteBufferWriter buf,
        SprocOperation op,
        string docId,
        ReadOnlyMemory<byte>? payload,
        string? conditionAst,
        string? updateAst)
    {
        WriteByte(buf, (byte)'[');
        WriteRaw(buf, OpLiteral(op));
        WriteByte(buf, (byte)',');
        WriteMinimallyEscapedJsonString(buf, docId);
        WriteByte(buf, (byte)',');
        WriteRawFragment(buf, payload);
        WriteByte(buf, (byte)',');
        WriteRawFragment(buf, conditionAst);
        WriteByte(buf, (byte)',');
        WriteRawFragment(buf, updateAst);
        WriteByte(buf, (byte)']');
    }

    private static ReadOnlySpan<byte> OpLiteral(SprocOperation op) => op switch
    {
        SprocOperation.Put => "\"PUT\""u8,
        SprocOperation.Update => "\"UPDATE\""u8,
        SprocOperation.Delete => "\"DELETE\""u8,
        _ => "\"PUT\""u8,
    };

    private static void WriteByte(IBufferWriter<byte> buf, byte value)
    {
        var span = buf.GetSpan(1);
        span[0] = value;
        buf.Advance(1);
    }

    private static void WriteRaw(IBufferWriter<byte> buf, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }
        var span = buf.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        buf.Advance(bytes.Length);
    }

    // Splices a raw JSON byte fragment, or the literal `null` when absent —
    // matching the legacy `payload ?? "null"` semantics.
    private static void WriteRawFragment(IBufferWriter<byte> buf, ReadOnlyMemory<byte>? fragment)
    {
        if (fragment.HasValue)
        {
            WriteRaw(buf, fragment.Value.Span);
        }
        else
        {
            WriteRaw(buf, "null"u8);
        }
    }

    private static void WriteRawFragment(IBufferWriter<byte> buf, string? fragment)
    {
        if (fragment is null)
        {
            WriteRaw(buf, "null"u8);
            return;
        }
        var span = buf.GetSpan(Encoding.UTF8.GetMaxByteCount(fragment.Length));
        int written = Encoding.UTF8.GetBytes(fragment, span);
        buf.Advance(written);
    }

    // Writes a quoted JSON string escaping only `\` and `"`, matching
    // <see cref="EscapeJsonString"/> exactly (byte-for-byte). Escaping at the
    // UTF-8 byte level is safe because both characters are single-byte ASCII
    // that never appear as a multi-byte continuation octet.
    private static void WriteMinimallyEscapedJsonString(IBufferWriter<byte> buf, string s)
    {
        WriteByte(buf, (byte)'"');
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        byte[] tmp = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int n = Encoding.UTF8.GetBytes(s, tmp);
            var span = buf.GetSpan(n * 2);
            int pos = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = tmp[i];
                if (b == (byte)'\\' || b == (byte)'"')
                {
                    span[pos++] = (byte)'\\';
                }
                span[pos++] = b;
            }
            buf.Advance(pos);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
        WriteByte(buf, (byte)'"');
    }

    // Legacy string-based parameter encoder, retained as the byte-identity
    // reference for <see cref="WriteSingleWriteParams"/> tests. Not used on the
    // request path (which is now single-pass / zero-copy).
    internal static string BuildParamsJson(SprocOperation op, string docId, string? payload, string? conditionAst, string? updateAst)
    {
        var sb = new StringBuilder(256);
        sb.Append('[');
        sb.Append('"').Append(op.ToString().ToUpperInvariant()).Append('"');
        sb.Append(',').Append('"').Append(EscapeJsonString(docId)).Append('"');
        sb.Append(',').Append(payload ?? "null");
        sb.Append(',').Append(conditionAst ?? "null");
        sb.Append(',').Append(updateAst ?? "null");
        sb.Append(']');
        return sb.ToString();
    }

    private static string EscapeJsonString(string s)
    {
        // Simple JSON string escaping for partition key values
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created atomicWrite sproc in container {ContainerName}")]
    private static partial void LogSprocCreated(ILogger logger, string containerName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "atomicWrite sproc already exists in container {ContainerName}")]
    private static partial void LogSprocAlreadyExists(ILogger logger, string containerName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create sproc in container {ContainerName}: HTTP {StatusCode} - {ErrorBody}")]
    private static partial void LogSprocCreateFailed(ILogger logger, string containerName, int statusCode, string errorBody);

    private enum SprocState { Unknown, Available, Failed }

    /// <summary>
    /// The JavaScript stored procedure body that executes atomic conditional writes.
    /// Handles PUT, UPDATE, and DELETE operations with optional condition evaluation.
    /// </summary>
    internal const string SprocBody = """
function atomicWrite(op, docId, payload, conditionAst, updateAst) {
    var ctx = getContext();
    var coll = ctx.getCollection();
    var resp = ctx.getResponse();
    var selfLink = coll.getSelfLink();

    // getSelfLink() is RID-based, so a constructed 'docs/<userId>' link is an
    // invalid mixed link that real Cosmos rejects with "Error creating request
    // message" (#202). Read by id with a partition-local query instead — the
    // sproc executes within the single logical partition of docId.
    var query = {
        query: 'SELECT * FROM c WHERE c.id = @id',
        parameters: [{ name: '@id', value: docId }]
    };
    var accepted = coll.queryDocuments(selfLink, query, {}, function(err, docs) {
        if (err) throw err;

        var existing = (docs && docs.length > 0) ? docs[0] : null;
        // Capture the document's own RID-based self link before stripping it —
        // deleteDocument requires it (a constructed id link is rejected).
        var existingSelf = existing ? existing._self : null;
        // Strip Cosmos system fields so they neither leak into ReturnValues nor
        // get re-upserted: upsertDocument rejects a body that carries stale
        // _self / _rid / _etag / _ts system properties.
        if (existing) stripSystemFields(existing);

        // Clone existing before any mutation (for ReturnValues=ALL_OLD)
        var oldItemClone = existing ? JSON.parse(JSON.stringify(existing)) : null;

        // Evaluate condition if present
        if (conditionAst !== null) {
            if (!evaluateCondition(conditionAst, existing)) {
                resp.setBody({ success: false, conditionFailed: true, oldItem: oldItemClone });
                return;
            }
        }

        // Execute operation
        switch (op) {
            case 'PUT':
                if (payload === null) throw { code: 400, body: 'Payload required for PUT' };
                // payload is already an object (not JSON string) built clean by C#
                coll.upsertDocument(selfLink, payload, function(e) { if (e) throw e; });
                resp.setBody({ success: true, operation: 'PUT', oldItem: oldItemClone });
                break;

            case 'UPDATE':
                if (updateAst === null) throw { code: 400, body: 'UpdateAst required for UPDATE' };
                var baseDoc = existing || {};
                if (payload) {
                    // payload contains the key attributes to ensure they're set (already an object)
                    for (var k in payload) baseDoc[k] = payload[k];
                }
                // updateAst is already an object (not JSON string)
                var updatedDoc = applyUpdate(baseDoc, updateAst);
                coll.upsertDocument(selfLink, updatedDoc, function(e) { if (e) throw e; });
                resp.setBody({ success: true, operation: 'UPDATE', oldItem: oldItemClone, newItem: updatedDoc });
                break;

            case 'DELETE':
                if (existingSelf) {
                    coll.deleteDocument(existingSelf, function(e) { if (e) throw e; });
                }
                resp.setBody({ success: true, operation: 'DELETE', oldItem: oldItemClone });
                break;

            default:
                throw { code: 400, body: 'Unknown operation: ' + op };
        }
    });

    if (!accepted) throw { code: 429, body: 'Request not accepted' };

    // Removes Cosmos-generated system fields from a queried document so they
    // are not re-written or surfaced as DynamoDB attributes.
    function stripSystemFields(d) {
        delete d._rid;
        delete d._self;
        delete d._etag;
        delete d._ts;
        delete d._attachments;
        delete d._lsn;
        delete d._metadata;
    }
    
    // Condition evaluator: interprets the AST from C# ConditionExpressionParser
    function evaluateCondition(ast, doc) {
        if (!ast) return true;
        switch (ast.type) {
            case 'AND':
                return evaluateCondition(ast.left, doc) && evaluateCondition(ast.right, doc);
            case 'OR':
                return evaluateCondition(ast.left, doc) || evaluateCondition(ast.right, doc);
            case 'NOT':
                return !evaluateCondition(ast.operand, doc);
            case 'COMPARE':
                return evaluateCompare(ast, doc);
            case 'BETWEEN':
                var val = getAttrValue(doc, extractPath(ast.value));
                return val >= extractValue(ast.low) && val <= extractValue(ast.high);
            case 'IN':
                var v = getAttrValue(doc, extractPath(ast.attr));
                var inVals = ast.values.map(function(x) { return extractValue(x); });
                return inVals.indexOf(v) >= 0;
            case 'ATTR_EXISTS':
                return hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_NOT_EXISTS':
                return !hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_TYPE':
                return checkAttrType(doc, extractPath(ast.attr), ast.attrType);
            case 'BEGINS_WITH':
                var str = getAttrValue(doc, extractPath(ast.attr));
                return typeof str === 'string' && str.indexOf(extractValue(ast.prefix)) === 0;
            case 'CONTAINS':
                var container = getAttrValue(doc, extractPath(ast.attr));
                var containsVal = extractValue(ast.value);
                if (typeof container === 'string') return container.indexOf(containsVal) >= 0;
                if (Array.isArray(container)) return container.indexOf(containsVal) >= 0;
                return false;
            case 'SIZE':
                var size = getSize(doc, extractPath(ast.attr));
                return evaluateCompareValue(size, ast.op, extractValue(ast.sizeValue));
            default:
                return true; // Unknown node type - pass through
        }
    }
    
    function evaluateCompare(ast, doc) {
        var left = extractOperandValue(doc, ast.attr);
        var right = extractOperandValue(doc, ast.value);
        switch (ast.op) {
            case '=': case 'EQ': return left === right;
            case '<>': case 'NE': return left !== right;
            case '<': case 'LT': return left < right;
            case '<=': case 'LE': return left <= right;
            case '>': case 'GT': return left > right;
            case '>=': case 'GE': return left >= right;
            default: return false;
        }
    }
    
    // Extract path string from {path:"..."} operand object
    function extractPath(operand) {
        if (operand && typeof operand === 'object' && operand.path) return operand.path;
        return operand; // fallback
    }
    
    // Extract value from operand (literal or path)
    function extractValue(operand) {
        if (operand && typeof operand === 'object') {
            if ('path' in operand) return undefined; // path operand - should use extractOperandValue
            // literal value
            return operand;
        }
        return operand;
    }
    
    // Extract value from operand: if path, look it up in doc; if literal, return the value
    function extractOperandValue(doc, operand) {
        if (operand && typeof operand === 'object' && operand.path) {
            return getAttrValue(doc, operand.path);
        }
        // literal value
        return operand;
    }
    
    function evaluateCompareValue(left, op, right) {
        switch (op) {
            case '=': case 'EQ': return left === right;
            case '<>': case 'NE': return left !== right;
            case '<': case 'LT': return left < right;
            case '<=': case 'LE': return left <= right;
            case '>': case 'GT': return left > right;
            case '>=': case 'GE': return left >= right;
            default: return false;
        }
    }
    
    function getAttrValue(doc, path) {
        if (!doc) return undefined;
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length; i++) {
            if (cur === null || cur === undefined) return undefined;
            cur = cur[parts[i]];
        }
        return cur;
    }
    
    function hasAttr(doc, path) {
        if (!doc) return false;
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length; i++) {
            if (cur === null || cur === undefined) return false;
            if (!cur.hasOwnProperty(parts[i])) return false;
            cur = cur[parts[i]];
        }
        return true;
    }
    
    function getSize(doc, path) {
        var val = getAttrValue(doc, path);
        if (typeof val === 'string') return val.length;
        if (Array.isArray(val)) return val.length;
        if (val && typeof val === 'object') return Object.keys(val).length;
        return 0;
    }
    
    function checkAttrType(doc, path, expectedType) {
        var val = getAttrValue(doc, path);
        switch (expectedType) {
            case 'S': return typeof val === 'string';
            case 'N': return typeof val === 'number';
            case 'B': return false; // Binary not supported
            case 'BOOL': return typeof val === 'boolean';
            case 'NULL': return val === null;
            case 'L': return Array.isArray(val);
            case 'M': return val && typeof val === 'object' && !Array.isArray(val);
            case 'SS': case 'NS': case 'BS': return Array.isArray(val);
            default: return false;
        }
    }
    
    // Update executor: applies UpdateExpression AST to a document
    function applyUpdate(doc, updateAst) {
        if (!updateAst) return doc;
        
        // SET actions
        if (updateAst.set) {
            for (var i = 0; i < updateAst.set.length; i++) {
                var s = updateAst.set[i];
                setAttr(doc, s.path, resolveSetValue(doc, s.value));
            }
        }
        
        // REMOVE actions
        if (updateAst.remove) {
            for (var i = 0; i < updateAst.remove.length; i++) {
                removeAttr(doc, updateAst.remove[i]);
            }
        }
        
        // ADD actions (numeric increment or set add)
        if (updateAst.add) {
            for (var i = 0; i < updateAst.add.length; i++) {
                var a = updateAst.add[i];
                var cur = getAttrValue(doc, a.path);
                if (typeof cur === 'number' && typeof a.value === 'number') {
                    setAttr(doc, a.path, cur + a.value);
                } else if (Array.isArray(cur)) {
                    // Add to set (unique values)
                    if (cur.indexOf(a.value) < 0) cur.push(a.value);
                } else if (cur === undefined) {
                    setAttr(doc, a.path, a.value);
                }
            }
        }
        
        // DELETE actions (set remove)
        if (updateAst.delete) {
            for (var i = 0; i < updateAst.delete.length; i++) {
                var d = updateAst.delete[i];
                var arr = getAttrValue(doc, d.path);
                if (Array.isArray(arr)) {
                    var idx = arr.indexOf(d.value);
                    if (idx >= 0) arr.splice(idx, 1);
                }
            }
        }
        
        return doc;
    }

    // Resolves a tagged SET-value operand ($k discriminator from
    // SprocAstSerializer.WriteValueOperand) against the current document.
    function resolveSetValue(doc, v) {
        if (v === null || typeof v !== 'object' || !('$k' in v)) return v;
        switch (v.$k) {
            case 'lit':
                return v.v;
            case 'path':
                return getAttrValue(doc, v.p);
            case 'op':
                var l = resolveSetValue(doc, v.l);
                var r = resolveSetValue(doc, v.r);
                return v.o === '+' ? (l + r) : (l - r);
            case 'ifne':
                var cur = getAttrValue(doc, v.p);
                return (cur !== undefined && cur !== null) ? cur : resolveSetValue(doc, v.f);
            case 'lap':
                var ll = resolveSetValue(doc, v.l);
                if (!Array.isArray(ll)) ll = [];
                var rr = resolveSetValue(doc, v.r);
                if (!Array.isArray(rr)) rr = [];
                return ll.concat(rr);
            default:
                return v;
        }
    }

    function setAttr(doc, path, value) {
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length - 1; i++) {
            if (!cur[parts[i]]) cur[parts[i]] = {};
            cur = cur[parts[i]];
        }
        cur[parts[parts.length - 1]] = value;
    }
    
    function removeAttr(doc, path) {
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length - 1; i++) {
            if (!cur[parts[i]]) return;
            cur = cur[parts[i]];
        }
        delete cur[parts[parts.length - 1]];
    }
}
""";

    /// <summary>
    /// Multi-operation stored procedure for <c>TransactWriteItems</c>. Executes
    /// a list of PUT / DELETE / CHECK operations atomically within a single
    /// logical partition. Algorithm (rollback-safe):
    /// <list type="number">
    ///   <item>Read every target document.</item>
    ///   <item>Evaluate every operation's condition. If ANY fails, emit
    ///   <c>{success:false, reasons:[...]}</c> and perform NO writes.</item>
    ///   <item>Otherwise perform every write (PUT=upsert, DELETE=delete,
    ///   CHECK=no-op). A write error throws, aborting the whole sproc
    ///   transaction so nothing partial is committed.</item>
    /// </list>
    /// Only the condition evaluator is shared with <c>atomicWrite</c>; there is
    /// deliberately no update executor here — atomic <c>Update</c> is rejected
    /// by the handler and documented as a gap.
    /// </summary>
    internal const string TransactSprocBody = """
function atomicTransactWrite(operations) {
    var ctx = getContext();
    var coll = ctx.getCollection();
    var resp = ctx.getResponse();
    var selfLink = coll.getSelfLink();
    var n = operations.length;
    var existing = new Array(n);

    readNext(0);

    function readNext(i) {
        if (i >= n) { evaluateAndWrite(); return; }
        var op = operations[i];
        // getSelfLink() is RID-based, so a constructed 'docs/<userId>' link is
        // an invalid mixed link that real Cosmos rejects with "Error creating
        // request message". Read by id with a partition-local query instead —
        // every operation shares the sproc's single logical partition.
        var query = {
            query: 'SELECT * FROM c WHERE c.id = @id',
            parameters: [{ name: '@id', value: op.id }]
        };
        var accepted = coll.queryDocuments(selfLink, query, {}, function(err, docs) {
            if (err) throw err;
            existing[i] = (docs && docs.length > 0) ? docs[0] : null;
            readNext(i + 1);
        });
        if (!accepted) throw new Error('queryDocuments not accepted at operation ' + i);
    }

    function evaluateAndWrite() {
        var reasons = new Array(n);
        var anyFail = false;
        for (var i = 0; i < n; i++) {
            var cond = operations[i].condition;
            var pass = (cond === null || cond === undefined) ? true : evaluateCondition(cond, existing[i]);
            reasons[i] = pass ? { code: 'None' } : { code: 'ConditionalCheckFailed' };
            if (!pass) anyFail = true;
        }
        if (anyFail) { resp.setBody({ success: false, reasons: reasons }); return; }
        writeNext(0);
    }

    function writeNext(i) {
        if (i >= n) { resp.setBody({ success: true }); return; }
        var op = operations[i];
        if (op.type === 'PUT') {
            var accP = coll.upsertDocument(selfLink, op.doc, function(err) {
                if (err) throw err;
                writeNext(i + 1);
            });
            if (!accP) throw new Error('upsertDocument not accepted at operation ' + i);
        } else if (op.type === 'DELETE') {
            if (existing[i]) {
                // Delete via the document's own RID-based self link (from the
                // query result) — a constructed id link would be rejected.
                var accD = coll.deleteDocument(existing[i]._self, function(err) {
                    if (err) throw err;
                    writeNext(i + 1);
                });
                if (!accD) throw new Error('deleteDocument not accepted at operation ' + i);
            } else {
                writeNext(i + 1);
            }
        } else {
            // CHECK: read-only, no write.
            writeNext(i + 1);
        }
    }

    // Condition evaluator: interprets the AST from C# ConditionExpressionParser.
    // Mirrors the evaluator in the atomicWrite sproc.
    function evaluateCondition(ast, doc) {
        if (!ast) return true;
        switch (ast.type) {
            case 'AND': return evaluateCondition(ast.left, doc) && evaluateCondition(ast.right, doc);
            case 'OR': return evaluateCondition(ast.left, doc) || evaluateCondition(ast.right, doc);
            case 'NOT': return !evaluateCondition(ast.operand, doc);
            case 'COMPARE': return evaluateCompare(ast, doc);
            case 'BETWEEN':
                var val = getAttrValue(doc, extractPath(ast.value));
                return val >= extractValue(ast.low) && val <= extractValue(ast.high);
            case 'IN':
                var v = getAttrValue(doc, extractPath(ast.attr));
                var inVals = ast.values.map(function(x) { return extractValue(x); });
                return inVals.indexOf(v) >= 0;
            case 'ATTR_EXISTS': return hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_NOT_EXISTS': return !hasAttr(doc, extractPath(ast.attr));
            case 'ATTR_TYPE': return checkAttrType(doc, extractPath(ast.attr), ast.attrType);
            case 'BEGINS_WITH':
                var str = getAttrValue(doc, extractPath(ast.attr));
                return typeof str === 'string' && str.indexOf(extractValue(ast.prefix)) === 0;
            case 'CONTAINS':
                var container = getAttrValue(doc, extractPath(ast.attr));
                var containsVal = extractValue(ast.value);
                if (typeof container === 'string') return container.indexOf(containsVal) >= 0;
                if (Array.isArray(container)) return container.indexOf(containsVal) >= 0;
                return false;
            case 'SIZE':
                var size = getSize(doc, extractPath(ast.attr));
                return evaluateCompareValue(size, ast.op, extractValue(ast.sizeValue));
            default:
                return true;
        }
    }

    function evaluateCompare(ast, doc) {
        var left = extractOperandValue(doc, ast.attr);
        var right = extractOperandValue(doc, ast.value);
        switch (ast.op) {
            case '=': case 'EQ': return left === right;
            case '<>': case 'NE': return left !== right;
            case '<': case 'LT': return left < right;
            case '<=': case 'LE': return left <= right;
            case '>': case 'GT': return left > right;
            case '>=': case 'GE': return left >= right;
            default: return false;
        }
    }

    function extractPath(operand) {
        if (operand && typeof operand === 'object' && operand.path) return operand.path;
        return operand;
    }

    function extractValue(operand) {
        if (operand && typeof operand === 'object') {
            if ('path' in operand) return undefined;
            return operand;
        }
        return operand;
    }

    function extractOperandValue(doc, operand) {
        if (operand && typeof operand === 'object') {
            if (operand.path) return getAttrValue(doc, operand.path);
            if (operand.size) return getSize(doc, operand.size);
        }
        return operand;
    }

    function evaluateCompareValue(left, op, right) {
        switch (op) {
            case '=': case 'EQ': return left === right;
            case '<>': case 'NE': return left !== right;
            case '<': case 'LT': return left < right;
            case '<=': case 'LE': return left <= right;
            case '>': case 'GT': return left > right;
            case '>=': case 'GE': return left >= right;
            default: return false;
        }
    }

    function getAttrValue(doc, path) {
        if (!doc) return undefined;
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length; i++) {
            if (cur === null || cur === undefined) return undefined;
            cur = cur[parts[i]];
        }
        return cur;
    }

    function hasAttr(doc, path) {
        if (!doc) return false;
        var parts = path.split('.');
        var cur = doc;
        for (var i = 0; i < parts.length; i++) {
            if (cur === null || cur === undefined) return false;
            if (!cur.hasOwnProperty(parts[i])) return false;
            cur = cur[parts[i]];
        }
        return true;
    }

    function getSize(doc, path) {
        var val = getAttrValue(doc, path);
        if (typeof val === 'string') return val.length;
        if (Array.isArray(val)) return val.length;
        if (val && typeof val === 'object') return Object.keys(val).length;
        return 0;
    }

    function checkAttrType(doc, path, expectedType) {
        var val = getAttrValue(doc, path);
        switch (expectedType) {
            case 'S': return typeof val === 'string';
            case 'N': return typeof val === 'number';
            case 'B': return false;
            case 'BOOL': return typeof val === 'boolean';
            case 'NULL': return val === null;
            case 'L': return Array.isArray(val);
            case 'M': return val && typeof val === 'object' && !Array.isArray(val);
            case 'SS': case 'NS': case 'BS': return Array.isArray(val);
            default: return false;
        }
    }
}
""";
}

internal enum SprocOperation
{
    Put,
    Update,
    Delete,
}

internal sealed class SprocExecuteResult
{
    public bool Success { get; init; }
    public bool ConditionFailed { get; init; }
    public int StatusCode { get; init; }
    public string? ErrorBody { get; init; }
    public string? ResponseBody { get; init; }
}

/// <summary>
/// Result of an <c>atomicTransactWrite</c> sproc execution.
/// </summary>
internal sealed class SprocTransactResult
{
    /// <summary>Whether the sproc was actually invoked.</summary>
    public bool Attempted { get; init; }

    /// <summary>All conditions passed and all writes committed.</summary>
    public bool Success { get; init; }

    /// <summary>At least one condition failed; no writes were performed.
    /// <see cref="ResponseBody"/> carries the positional <c>reasons</c> array.</summary>
    public bool ConditionFailed { get; init; }

    /// <summary>HTTP status when the sproc call itself failed (non-2xx).</summary>
    public int StatusCode { get; init; }

    public string? ErrorBody { get; init; }
    public string? ResponseBody { get; init; }
}

internal sealed class SprocCreateBody
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SprocCreateBody))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
internal sealed partial class SprocJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
