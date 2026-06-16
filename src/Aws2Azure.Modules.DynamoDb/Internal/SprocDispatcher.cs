using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.Extensions.Logging;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Context for sproc-based atomic writes. Passed to handlers that support
/// sproc dispatch (PutItem, UpdateItem, DeleteItem with conditions).
/// </summary>
internal sealed class SprocContext
{
    public StoredProcedureMode Mode { get; }
    public SprocManager? Manager { get; }

    public SprocContext(StoredProcedureMode mode, SprocManager? manager)
    {
        Mode = mode;
        Manager = manager;
    }

    /// <summary>
    /// Returns true if sprocs are enabled and the manager is available.
    /// </summary>
    public bool IsSprocEnabled => Mode != StoredProcedureMode.Disabled && Manager is not null;

    /// <summary>
    /// Returns true if sproc failure should be a hard error (mode=Required).
    /// </summary>
    public bool IsSprocRequired => Mode == StoredProcedureMode.Required;
}

/// <summary>
/// Helper for executing atomic writes via stored procedure.
/// Handles sproc creation, AST serialization, and result interpretation.
/// </summary>
internal static class SprocDispatcher
{
    private const string UnsupportedFeatureMessage =
        "The conditional write uses an expression feature the atomic stored procedure cannot " +
        "execute faithfully (sets, binary, high-precision numbers, list-index paths, ADD/DELETE " +
        "clauses, size(), or contains()). Use stored-procedure mode Preferred or Disabled for " +
        "these operations.";

    /// <summary>
    /// Returns a short-circuit result when the request falls outside the slice
    /// of the expression surface the sproc executes faithfully (see
    /// <see cref="SprocEligibility"/>): <c>NotAttempted</c> under Preferred so
    /// the caller takes the GET → modify → PUT fallback, or a loud failure under
    /// Required so atomicity is never silently degraded. Returns <c>null</c> when
    /// the request is eligible and dispatch should proceed.
    /// </summary>
    private static SprocWriteResult? GateEligibility(
        SprocContext ctx, ConditionNode? condition, UpdateExpressionAst? update)
    {
        if (SprocEligibility.IsEligible(condition, update))
        {
            return null;
        }

        return ctx.IsSprocRequired
            ? SprocWriteResult.Failed(UnsupportedFeatureMessage)
            : SprocWriteResult.NotAttempted;
    }

    private static async Task<(bool Attempted, SprocExecuteResult? Result, SprocWriteResult ShortCircuit)>
        TryExecuteSingleWriteAsync(
            SprocContext ctx,
            CosmosClient cosmos,
            string containerName,
            string partitionKey,
            string docId,
            SprocOperation operation,
            ReadOnlyMemory<byte>? payload,
            ConditionNode? condition,
            UpdateExpressionAst? updateAst,
            CancellationToken ct)
    {
        if (!ctx.IsSprocEnabled || ctx.Manager is null)
        {
            return (false, null, SprocWriteResult.NotAttempted);
        }

        if (GateEligibility(ctx, condition, updateAst) is { } gate)
        {
            return (false, null, gate);
        }

        var sprocReady = await ctx.Manager.EnsureSprocAsync(cosmos, containerName, ct).ConfigureAwait(false);
        if (!sprocReady)
        {
            return (false, null, ctx.IsSprocRequired
                ? SprocWriteResult.Failed("Stored procedure creation failed and mode=Required")
                : SprocWriteResult.NotAttempted);
        }

        var conditionAstJson = SprocAstSerializer.SerializeCondition(condition);
        var updateAstJson = SprocAstSerializer.SerializeUpdate(updateAst);
        var result = await ctx.Manager.ExecuteAsync(
            cosmos,
            containerName,
            partitionKey,
            operation,
            docId,
            payload,
            conditionAstJson,
            updateAstJson,
            ct).ConfigureAwait(false);

        return (true, result, default);
    }

    private static SprocWriteResult MapExecutionFailure(SprocContext ctx, SprocExecuteResult result)
        => ctx.IsSprocRequired
            ? SprocWriteResult.Failed($"Sproc execution failed: {result.ErrorBody}")
            : SprocWriteResult.NotAttempted;

    /// <summary>
    /// Attempts to execute a conditional PutItem via stored procedure.
    /// Returns (success, conditionFailed, error) tuple.
    /// </summary>
    public static async Task<SprocWriteResult> TryPutItemAsync(
        SprocContext ctx,
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        string docId,
        ReadOnlyMemory<byte> cosmosDocJson,
        ConditionNode? condition,
        CancellationToken ct)
    {
        var dispatch = await TryExecuteSingleWriteAsync(
            ctx, cosmos, containerName, partitionKey, docId, SprocOperation.Put,
            cosmosDocJson, condition, updateAst: null, ct).ConfigureAwait(false);
        if (!dispatch.Attempted)
        {
            return dispatch.ShortCircuit;
        }

        var result = dispatch.Result!;

        if (result.Success)
        {
            return SprocWriteResult.Succeeded();
        }

        if (result.ConditionFailed)
        {
            return SprocWriteResult.ConditionNotMet();
        }

        return MapExecutionFailure(ctx, result); // Preferred falls back to GET→PUT.
    }

    /// <summary>
    /// Attempts to execute an UpdateItem via stored procedure.
    /// Returns (success, conditionFailed, oldItem, error) tuple.
    /// </summary>
    public static async Task<SprocWriteResult> TryUpdateItemAsync(
        SprocContext ctx,
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        string docId,
        ReadOnlyMemory<byte> keyAttributesJson,
        ConditionNode? condition,
        UpdateExpressionAst? updateAst,
        string returnValues,
        string returnValuesOnConditionCheckFailure,
        CancellationToken ct)
    {
        var dispatch = await TryExecuteSingleWriteAsync(
            ctx, cosmos, containerName, partitionKey, docId, SprocOperation.Update,
            keyAttributesJson, condition, updateAst, ct).ConfigureAwait(false);
        if (!dispatch.Attempted)
        {
            return dispatch.ShortCircuit;
        }

        var result = dispatch.Result!;

        if (result.Success)
        {
            // Only parse oldItem/newItem when ReturnValues needs them (avoid hot-path allocations)
            var needOld = returnValues is "ALL_OLD" or "UPDATED_OLD";
            var needNew = returnValues is "ALL_NEW" or "UPDATED_NEW";
            if (needOld || needNew)
            {
                var (oldItem, newItem) = ParseSprocResponse(result.ResponseBody);
                return SprocWriteResult.Succeeded(result.ResponseBody, needOld ? oldItem : null, needNew ? newItem : null);
            }
            return SprocWriteResult.Succeeded(result.ResponseBody, null, null);
        }

        if (result.ConditionFailed)
        {
            // Only parse oldItem when ReturnValuesOnConditionCheckFailure=ALL_OLD
            if (returnValuesOnConditionCheckFailure == "ALL_OLD")
            {
                var (oldItem, _) = ParseSprocResponse(result.ResponseBody);
                return SprocWriteResult.ConditionNotMet(oldItem);
            }
            return SprocWriteResult.ConditionNotMet(null);
        }

        return MapExecutionFailure(ctx, result);
    }

    /// <summary>
    /// Attempts to execute a conditional DeleteItem via stored procedure.
    /// Returns (success, conditionFailed, oldItem, error) tuple.
    /// </summary>
    public static async Task<SprocWriteResult> TryDeleteItemAsync(
        SprocContext ctx,
        CosmosClient cosmos,
        string containerName,
        string partitionKey,
        string docId,
        ConditionNode? condition,
        CancellationToken ct)
    {
        var dispatch = await TryExecuteSingleWriteAsync(
            ctx, cosmos, containerName, partitionKey, docId, SprocOperation.Delete,
            payload: null, condition, updateAst: null, ct).ConfigureAwait(false);
        if (!dispatch.Attempted)
        {
            return dispatch.ShortCircuit;
        }

        var result = dispatch.Result!;

        if (result.Success)
        {
            return SprocWriteResult.Succeeded(result.ResponseBody);
        }

        if (result.ConditionFailed)
        {
            return SprocWriteResult.ConditionNotMet();
        }

        return MapExecutionFailure(ctx, result);
    }

    /// <summary>
    /// Parses the sproc response body to extract oldItem and newItem.
    /// Returns decoded DDB AttributeValue format items.
    /// </summary>
    private static (Dictionary<string, JsonElement>? OldItem, Dictionary<string, JsonElement>? NewItem) 
        ParseSprocResponse(string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            Dictionary<string, JsonElement>? oldItem = null;
            Dictionary<string, JsonElement>? newItem = null;

            if (root.TryGetProperty("oldItem", out var oldItemProp) && 
                oldItemProp.ValueKind == JsonValueKind.Object)
            {
                oldItem = InferredAttributeStorage.ExtractItem(oldItemProp);
            }

            if (root.TryGetProperty("newItem", out var newItemProp) && 
                newItemProp.ValueKind == JsonValueKind.Object)
            {
                newItem = InferredAttributeStorage.ExtractItem(newItemProp);
            }

            return (oldItem, newItem);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Result of a sproc write attempt.
/// </summary>
internal readonly struct SprocWriteResult
{
    public bool Attempted { get; init; }
    public bool Success { get; init; }
    public bool ConditionFailed { get; init; }
    public string? Error { get; init; }
    public string? ResponseBody { get; init; }
    
    /// <summary>
    /// The item as it existed before the operation (for ReturnValues=ALL_OLD/UPDATED_OLD).
    /// Already decoded from Cosmos format to DDB AttributeValue format.
    /// </summary>
    public Dictionary<string, JsonElement>? OldItem { get; init; }
    
    /// <summary>
    /// The item after the operation (for ReturnValues=ALL_NEW/UPDATED_NEW).
    /// Already decoded from Cosmos format to DDB AttributeValue format.
    /// </summary>
    public Dictionary<string, JsonElement>? NewItem { get; init; }

    public static SprocWriteResult NotAttempted => new() { Attempted = false };
    
    public static SprocWriteResult Succeeded(
        string? responseBody = null,
        Dictionary<string, JsonElement>? oldItem = null,
        Dictionary<string, JsonElement>? newItem = null) => new()
    {
        Attempted = true,
        Success = true,
        ResponseBody = responseBody,
        OldItem = oldItem,
        NewItem = newItem
    };
    
    public static SprocWriteResult ConditionNotMet(Dictionary<string, JsonElement>? oldItem = null) => new()
    {
        Attempted = true,
        Success = false,
        ConditionFailed = true,
        OldItem = oldItem
    };
    
    public static SprocWriteResult Failed(string error) => new() { Attempted = true, Success = false, Error = error };
}
