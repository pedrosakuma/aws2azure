using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Core.Configuration;
using Aws2Azure.Modules.DynamoDb.Expressions;
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
        string cosmosDocJson,
        ConditionNode? condition,
        CancellationToken ct)
    {
        if (!ctx.IsSprocEnabled || ctx.Manager is null)
        {
            return SprocWriteResult.NotAttempted;
        }

        // Ensure sproc exists
        var sprocReady = await ctx.Manager.EnsureSprocAsync(cosmos, containerName, ct).ConfigureAwait(false);
        if (!sprocReady)
        {
            return ctx.IsSprocRequired
                ? SprocWriteResult.Failed("Stored procedure creation failed and mode=Required")
                : SprocWriteResult.NotAttempted;
        }

        // Serialize condition AST
        var conditionAst = SprocAstSerializer.SerializeCondition(condition);

        // Execute sproc
        var result = await ctx.Manager.ExecuteAsync(
            cosmos,
            containerName,
            partitionKey,
            SprocOperation.Put,
            docId,
            cosmosDocJson,
            conditionAst,
            updateAst: null,
            ct).ConfigureAwait(false);

        if (result.Success)
        {
            return SprocWriteResult.Succeeded();
        }

        if (result.ConditionFailed)
        {
            return SprocWriteResult.ConditionNotMet();
        }

        // Sproc execution failed
        return ctx.IsSprocRequired
            ? SprocWriteResult.Failed($"Sproc execution failed: {result.ErrorBody}")
            : SprocWriteResult.NotAttempted; // Fallback to GET→PUT
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
        string keyAttributesJson,
        ConditionNode? condition,
        UpdateExpressionAst? updateAst,
        CancellationToken ct)
    {
        if (!ctx.IsSprocEnabled || ctx.Manager is null)
        {
            return SprocWriteResult.NotAttempted;
        }

        // Ensure sproc exists
        var sprocReady = await ctx.Manager.EnsureSprocAsync(cosmos, containerName, ct).ConfigureAwait(false);
        if (!sprocReady)
        {
            return ctx.IsSprocRequired
                ? SprocWriteResult.Failed("Stored procedure creation failed and mode=Required")
                : SprocWriteResult.NotAttempted;
        }

        // Serialize ASTs
        var conditionAstJson = SprocAstSerializer.SerializeCondition(condition);
        var updateAstJson = SprocAstSerializer.SerializeUpdate(updateAst);

        // Execute sproc
        var result = await ctx.Manager.ExecuteAsync(
            cosmos,
            containerName,
            partitionKey,
            SprocOperation.Update,
            docId,
            keyAttributesJson,
            conditionAstJson,
            updateAstJson,
            ct).ConfigureAwait(false);

        if (result.Success)
        {
            // Parse old item from response if needed
            return SprocWriteResult.Succeeded(result.ResponseBody);
        }

        if (result.ConditionFailed)
        {
            return SprocWriteResult.ConditionNotMet();
        }

        // Sproc execution failed
        return ctx.IsSprocRequired
            ? SprocWriteResult.Failed($"Sproc execution failed: {result.ErrorBody}")
            : SprocWriteResult.NotAttempted;
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
        if (!ctx.IsSprocEnabled || ctx.Manager is null)
        {
            return SprocWriteResult.NotAttempted;
        }

        // Ensure sproc exists
        var sprocReady = await ctx.Manager.EnsureSprocAsync(cosmos, containerName, ct).ConfigureAwait(false);
        if (!sprocReady)
        {
            return ctx.IsSprocRequired
                ? SprocWriteResult.Failed("Stored procedure creation failed and mode=Required")
                : SprocWriteResult.NotAttempted;
        }

        // Serialize condition AST
        var conditionAst = SprocAstSerializer.SerializeCondition(condition);

        // Execute sproc
        var result = await ctx.Manager.ExecuteAsync(
            cosmos,
            containerName,
            partitionKey,
            SprocOperation.Delete,
            docId,
            payload: null,
            conditionAst,
            updateAst: null,
            ct).ConfigureAwait(false);

        if (result.Success)
        {
            return SprocWriteResult.Succeeded(result.ResponseBody);
        }

        if (result.ConditionFailed)
        {
            return SprocWriteResult.ConditionNotMet();
        }

        // Sproc execution failed
        return ctx.IsSprocRequired
            ? SprocWriteResult.Failed($"Sproc execution failed: {result.ErrorBody}")
            : SprocWriteResult.NotAttempted;
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

    public static SprocWriteResult NotAttempted => new() { Attempted = false };
    public static SprocWriteResult Succeeded(string? responseBody = null) => new() { Attempted = true, Success = true, ResponseBody = responseBody };
    public static SprocWriteResult ConditionNotMet() => new() { Attempted = true, Success = false, ConditionFailed = true };
    public static SprocWriteResult Failed(string error) => new() { Attempted = true, Success = false, Error = error };
}
