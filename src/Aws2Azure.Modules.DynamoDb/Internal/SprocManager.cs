using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    public const string SprocId = "atomicWrite";

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
        var cacheKey = $"{cosmos.DatabaseName}:{containerName}";

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
        string? payload,
        string? conditionAst,
        string? updateAst,
        CancellationToken ct)
    {
        // POST /dbs/{db}/colls/{coll}/sprocs/atomicWrite
        var sprocLink = $"dbs/{cosmos.DatabaseName}/colls/{containerName}/sprocs/{SprocId}";
        var requestUri = $"/{sprocLink}";

        // Build the JSON array of parameters: [op, docId, payload, conditionAst, updateAst]
        var paramsJson = BuildParamsJson(operation, docId, payload, conditionAst, updateAst);

        using var content = new StringContent(paramsJson, Encoding.UTF8, "application/json");

        var headers = new[]
        {
            new KeyValuePair<string, string>("x-ms-documentdb-partitionkey", $"[\"{EscapeJsonString(partitionKey)}\"]"),
        };

        var response = await cosmos.SendAsync(
            HttpMethod.Post,
            "sprocs",
            sprocLink,
            requestUri,
            content,
            headers,
            ct).ConfigureAwait(false);

        return await ParseSprocResponseAsync(response, ct).ConfigureAwait(false);
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

    private static string BuildParamsJson(SprocOperation op, string docId, string? payload, string? conditionAst, string? updateAst)
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
    var docLink = selfLink + 'docs/' + docId;
    
    // Read existing document (may not exist)
    var accepted = coll.readDocument(docLink, {}, function(err, existing) {
        if (err && err.number !== 404) {
            throw err;
        }
        
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
                // payload is already an object (not JSON string)
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
                if (existing) {
                    coll.deleteDocument(docLink, function(e) { if (e) throw e; });
                }
                resp.setBody({ success: true, operation: 'DELETE', oldItem: oldItemClone });
                break;
                
            default:
                throw { code: 400, body: 'Unknown operation: ' + op };
        }
    });
    
    if (!accepted) throw { code: 429, body: 'Request not accepted' };
    
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
                setAttr(doc, s.path, s.value);
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
