using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>TransactGetItems</c> implemented as one read-only Cosmos stored
/// procedure. The handler rejects cross-table, cross-partition, and duplicate
/// targets before invoking the snapshot operation.
/// </summary>
internal static class TransactGetItemsHandler
{
    private const int MaxItemsPerCall = 100;

    public static async Task HandleTransactGetItemsAsync(
        HttpContext ctx,
        byte[] body,
        CosmosClient cosmos,
        SprocContext? sprocContext,
        CancellationToken ct)
    {
        TransactGetItemsRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(
                body,
                TransactGetItemsJsonContext.Default.TransactGetItemsRequest);
        }
        catch (JsonException exception)
        {
            await CosmosOpsShared.WriteErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "SerializationException",
                "Malformed JSON: " + exception.Message).ConfigureAwait(false);
            return;
        }

        if (request?.TransactItems is null || request.TransactItems.Count == 0)
        {
            await RejectAsync(
                ctx,
                "TransactItems is required and must contain at least one Get entry.")
                .ConfigureAwait(false);
            return;
        }
        if (request.TransactItems.Count > MaxItemsPerCall)
        {
            await RejectAsync(
                ctx,
                $"TransactGetItems supports at most {MaxItemsPerCall} items per request.")
                .ConfigureAwait(false);
            return;
        }
        if (request.ReturnConsumedCapacity is not null
            && !string.Equals(
                request.ReturnConsumedCapacity,
                "NONE",
                StringComparison.Ordinal))
        {
            await RejectAsync(
                ctx,
                "ReturnConsumedCapacity is not supported for TransactGetItems; omit it or use NONE.")
                .ConfigureAwait(false);
            return;
        }

        string? tableName = null;
        var projections = new Projection?[request.TransactItems.Count];
        for (var index = 0; index < request.TransactItems.Count; index++)
        {
            var entry = request.TransactItems[index];
            if (entry?.AdditionalProperties is { Count: > 0 } extras)
            {
                foreach (var extra in extras)
                {
                    await RejectAsync(
                        ctx,
                        $"TransactItems[{index}] contains unsupported action '{extra.Key}'.")
                        .ConfigureAwait(false);
                    return;
                }
            }
            if (entry is null || entry.Get.ValueKind != JsonValueKind.Object)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Get is required and must be an object.")
                    .ConfigureAwait(false);
                return;
            }
            if (!TryValidateGetMembers(entry.Get, out var memberError))
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Get {memberError}").ConfigureAwait(false);
                return;
            }
            if (!entry.Get.TryGetProperty("TableName", out var tableElement)
                || tableElement.ValueKind != JsonValueKind.String)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Get.TableName is required.")
                    .ConfigureAwait(false);
                return;
            }

            var candidateTable = tableElement.GetString()!;
            if (!DynamoDbNames.IsValidTableName(candidateTable))
            {
                await RejectAsync(ctx, $"Invalid TableName '{candidateTable}'.")
                    .ConfigureAwait(false);
                return;
            }
            if (tableName is null)
            {
                tableName = candidateTable;
            }
            else if (!string.Equals(
                         tableName,
                         candidateTable,
                         StringComparison.Ordinal))
            {
                await RejectAsync(
                    ctx,
                    "TransactGetItems via aws2azure requires every Get to target the same table because the snapshot is scoped to one Cosmos container.")
                    .ConfigureAwait(false);
                return;
            }

            if (!entry.Get.TryGetProperty("Key", out var key)
                || key.ValueKind != JsonValueKind.Object)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Get.Key is required and must be an object.")
                    .ConfigureAwait(false);
                return;
            }

            var hasProjection = entry.Get.TryGetProperty(
                "ProjectionExpression",
                out var projectionElement);
            var hasNames = entry.Get.TryGetProperty(
                "ExpressionAttributeNames",
                out var namesElement);
            if (hasProjection)
            {
                if (projectionElement.ValueKind != JsonValueKind.String)
                {
                    await RejectAsync(
                        ctx,
                        $"TransactItems[{index}].Get.ProjectionExpression must be a string.")
                        .ConfigureAwait(false);
                    return;
                }

                IReadOnlyDictionary<string, string>? names = null;
                if (hasNames
                    && !TryReadExpressionAttributeNames(
                        namesElement,
                        out names,
                        out var namesError))
                {
                    await RejectAsync(
                        ctx,
                        $"TransactItems[{index}].Get.{namesError}")
                        .ConfigureAwait(false);
                    return;
                }

                try
                {
                    var parsed = ProjectionExpressionParser.ParseWithUsage(
                        projectionElement.GetString()!,
                        names);
                    if (TryFindUnused(
                            names,
                            parsed.ConsumedNames,
                            out var unusedName))
                    {
                        await RejectAsync(
                            ctx,
                            $"Value provided in ExpressionAttributeNames unused in expressions: {unusedName}.")
                            .ConfigureAwait(false);
                        return;
                    }
                    projections[index] = parsed.Projection;
                }
                catch (ExpressionSyntaxException exception)
                {
                    await RejectAsync(
                        ctx,
                        $"Invalid ProjectionExpression (offset {exception.Position}): {exception.Message}")
                        .ConfigureAwait(false);
                    return;
                }
            }
            else if (hasNames)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Get.ExpressionAttributeNames requires ProjectionExpression.")
                    .ConfigureAwait(false);
                return;
            }
        }

        if (sprocContext is not { IsSprocEnabled: true } || sprocContext.Manager is null)
        {
            await RejectAsync(
                ctx,
                "TransactGetItems requires stored procedures so all positions are read from one server-side snapshot. Set the DynamoDB stored-procedure mode to Preferred or Required.")
                .ConfigureAwait(false);
            return;
        }

        using var metadataRead = await CosmosOpsShared.TryReadTableMetadataAsync(
            cosmos,
            tableName!,
            ct).ConfigureAwait(false);
        if (metadataRead.Status == CosmosOpsShared.TableMetadataReadStatus.CosmosError)
        {
            await CosmosOpsShared.WriteCosmosErrorAsync(
                ctx,
                metadataRead.ErrorResponse!,
                ct).ConfigureAwait(false);
            return;
        }
        if (metadataRead.Status == CosmosOpsShared.TableMetadataReadStatus.NotFound)
        {
            await CosmosOpsShared.WriteErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "ResourceNotFoundException",
                $"Table not found: {tableName}").ConfigureAwait(false);
            return;
        }

        var metadata = metadataRead.Metadata!;
        var work = new WorkUnit[request.TransactItems.Count];
        var documentIds = new string[request.TransactItems.Count];
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        string? partitionKey = null;

        for (var index = 0; index < request.TransactItems.Count; index++)
        {
            var get = request.TransactItems[index]!.Get;
            var key = get.GetProperty("Key");
            foreach (var keyDefinition in metadata.KeySchema)
            {
                if (!key.TryGetProperty(keyDefinition.Name, out var attribute))
                {
                    await RejectAsync(
                        ctx,
                        $"TransactItems[{index}].Get.Key is missing required attribute '{keyDefinition.Name}'.")
                        .ConfigureAwait(false);
                    return;
                }
                if (!ItemKeyFormatter.ValidateKeyAttributeType(
                        attribute,
                        metadata,
                        keyDefinition.Name,
                        out var typeError))
                {
                    await RejectAsync(ctx, typeError).ConfigureAwait(false);
                    return;
                }
            }
            if (!ItemKeyFormatter.TryBuild(
                    key,
                    metadata,
                    out var candidatePartitionKey,
                    out var documentId,
                    out var keyError))
            {
                await RejectAsync(ctx, keyError).ConfigureAwait(false);
                return;
            }

            if (partitionKey is null)
            {
                partitionKey = candidatePartitionKey;
            }
            else if (!string.Equals(
                         partitionKey,
                         candidatePartitionKey,
                         StringComparison.Ordinal))
            {
                await RejectAsync(
                    ctx,
                    "TransactGetItems via aws2azure requires every Get to share the same partition-key value because the snapshot is scoped to one Cosmos logical partition.")
                    .ConfigureAwait(false);
                return;
            }
            if (!seenTargets.Add(documentId))
            {
                await RejectAsync(
                    ctx,
                    "Transaction request cannot include multiple operations on one item.")
                    .ConfigureAwait(false);
                return;
            }

            work[index] = new WorkUnit(documentId, projections[index]);
            documentIds[index] = documentId;
        }

        var routeResolution = await cosmos.ResolveTransactionRouteAsync(
                tableName!,
                ct)
            .ConfigureAwait(false);
        if (routeResolution.Status
            != CosmosTransactionRouteResolutionStatus.Ready)
        {
            await WriteRoutingFailureAsync(ctx, routeResolution)
                .ConfigureAwait(false);
            return;
        }

        var ready = await sprocContext.Manager.EnsureTransactGetSprocAsync(
            cosmos,
            tableName!,
            routeResolution.Route,
            ct).ConfigureAwait(false);
        if (!ready)
        {
            await CosmosOpsShared.WriteErrorAsync(
                ctx,
                StatusCodes.Status500InternalServerError,
                "InternalServerError",
                "TransactGetItems snapshot stored procedure could not be provisioned or did not match its versioned body.")
                .ConfigureAwait(false);
            return;
        }

        var result = await sprocContext.Manager.ExecuteTransactGetAsync(
            cosmos,
            tableName!,
            partitionKey!,
            documentIds,
            routeResolution.Route,
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteExecutionFailureAsync(ctx, result).ConfigureAwait(false);
            return;
        }

        if (!TryBuildResponse(result.ResponseBody, work, out var response, out var responseError))
        {
            await CosmosOpsShared.WriteErrorAsync(
                ctx,
                StatusCodes.Status500InternalServerError,
                "InternalServerError",
                responseError).ConfigureAwait(false);
            return;
        }

        await CosmosOpsShared.WriteJsonAsync(
            ctx,
            StatusCodes.Status200OK,
            response!,
            TransactGetItemsJsonContext.Default.TransactGetItemsResponse)
            .ConfigureAwait(false);
    }

    private static bool TryValidateGetMembers(JsonElement get, out string? error)
    {
        foreach (var property in get.EnumerateObject())
        {
            if (property.Name is not (
                    "TableName"
                    or "Key"
                    or "ProjectionExpression"
                    or "ExpressionAttributeNames"))
            {
                error = $"contains unsupported member '{property.Name}'.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool TryReadExpressionAttributeNames(
        JsonElement element,
        out IReadOnlyDictionary<string, string>? names,
        out string? error)
    {
        names = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "ExpressionAttributeNames must be a JSON object.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error =
                    $"ExpressionAttributeNames['{property.Name}'] must be a string.";
                return false;
            }
            values[property.Name] = property.Value.GetString()!;
        }

        names = values;
        error = null;
        return true;
    }

    private static bool TryFindUnused(
        IReadOnlyDictionary<string, string>? declared,
        IReadOnlySet<string> consumed,
        out string? unused)
    {
        if (declared is not null)
        {
            foreach (var key in declared.Keys)
            {
                if (!consumed.Contains(key))
                {
                    unused = key;
                    return true;
                }
            }
        }

        unused = null;
        return false;
    }

    private static bool TryBuildResponse(
        string? body,
        WorkUnit[] work,
        out TransactGetItemsResponse? response,
        out string error)
    {
        response = null;
        error = "Snapshot stored procedure returned a malformed positional response.";
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() != work.Length)
            {
                return false;
            }

            var responses = new List<TransactGetItemResponse>(work.Length);
            var index = 0;
            foreach (var itemElement in items.EnumerateArray())
            {
                if (itemElement.ValueKind == JsonValueKind.Null)
                {
                    responses.Add(new TransactGetItemResponse());
                    index++;
                    continue;
                }
                if (itemElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var item = InferredAttributeStorage.ExtractItem(itemElement);
                if (item is null)
                {
                    return false;
                }
                if (work[index].Projection is { } projection)
                {
                    item = projection.Apply(item);
                }
                responses.Add(new TransactGetItemResponse { Item = item });
                index++;
            }

            response = new TransactGetItemsResponse { Responses = responses };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Task WriteExecutionFailureAsync(
        HttpContext ctx,
        SprocTransactGetResult result)
    {
        var code = result.StatusCode switch
        {
            StatusCodes.Status429TooManyRequests =>
                "ProvisionedThroughputExceededException",
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden =>
                "AccessDeniedException",
            _ => "InternalServerError",
        };
        var status = code == "InternalServerError"
            ? StatusCodes.Status500InternalServerError
            : StatusCodes.Status400BadRequest;
        return CosmosOpsShared.WriteErrorAsync(
            ctx,
            status,
            code,
            string.IsNullOrEmpty(result.ErrorBody)
                ? "TransactGetItems snapshot execution failed."
                : result.ErrorBody);
    }

    private static Task RejectAsync(HttpContext ctx, string message)
        => CosmosOpsShared.WriteErrorAsync(
            ctx,
            StatusCodes.Status400BadRequest,
            "ValidationException",
            message);

    private static Task WriteRoutingFailureAsync(
        HttpContext ctx,
        CosmosTransactionRouteResolution resolution)
        => resolution.Status
            == CosmosTransactionRouteResolutionStatus.InvalidConfiguration
            ? RejectAsync(ctx, resolution.Error)
            : resolution.BackendStatus is { } backendStatus
                ? CosmosOpsShared.WriteCosmosStatusErrorAsync(
                    ctx,
                    backendStatus,
                    resolution.Error)
                : CosmosOpsShared.WriteErrorAsync(
                    ctx,
                    StatusCodes.Status500InternalServerError,
                    "InternalServerError",
                    resolution.Error);

    private readonly record struct WorkUnit(string DocumentId, Projection? Projection);
}
