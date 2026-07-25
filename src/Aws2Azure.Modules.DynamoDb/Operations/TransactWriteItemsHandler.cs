using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aws2Azure.Modules.DynamoDb.Expressions;
using Aws2Azure.Modules.DynamoDb.Internal;
using Aws2Azure.Modules.DynamoDb.Persistence;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Operations;

/// <summary>
/// DynamoDB <c>TransactWriteItems</c> translated to one versioned Cosmos stored
/// procedure transaction. Only Put, Delete, and ConditionCheck are supported,
/// and every target must belong to one table and one logical partition.
/// </summary>
internal static partial class TransactWriteItemsHandler
{
    private const int MaxItemsPerCall = 100;
    internal const int MaxSprocRequestBodyBytes = 2 * 1024 * 1024;
    private const int MaxSerializerContiguousWriteBytes =
        (6 * DynamoDbItemSize.MaximumBytes) + 4096;

    private static readonly JsonDocumentOptions TransactItemParseOptions = new()
    {
        AllowTrailingCommas = true,
    };

    internal enum OpKind
    {
        Put,
        Delete,
        Check,
    }

    internal readonly record struct PreparedOp(
        OpKind Kind,
        string Id,
        byte[]? DocBytes,
        string? ConditionJson);

    private readonly record struct InputOp(
        OpKind Kind,
        JsonRange Range,
        string Name,
        long BaseItemSize,
        ConditionNode? Condition);

    private readonly record struct PreparedRequestOp(
        OpKind Kind,
        string Id,
        JsonRange Range,
        string PartitionKey,
        int? TtlSeconds,
        OrderKeyField[]? OrderKeys,
        ConditionNode? Condition);

    private readonly record struct PreparedIdempotency(
        string RecordId,
        string PartitionKey);

    public static async Task HandleTransactWriteItemsAsync(
        HttpContext ctx,
        byte[] body,
        CosmosClient cosmos,
        SprocContext? sprocContext,
        CancellationToken ct)
    {
        TransactWriteItemsRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(
                body,
                TransactWriteItemsJsonContext.Default.TransactWriteItemsRequest);
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
                "TransactItems is required and must contain at least one entry.")
                .ConfigureAwait(false);
            return;
        }
        if (request.TransactItems.Count > MaxItemsPerCall)
        {
            await RejectAsync(
                ctx,
                $"TransactWriteItems supports at most {MaxItemsPerCall} items per request.")
                .ConfigureAwait(false);
            return;
        }
        if (!TryValidateClientRequestToken(
                request.ClientRequestToken,
                out var clientRequestTokenError))
        {
            await RejectAsync(ctx, clientRequestTokenError)
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
                "ReturnConsumedCapacity is not supported for TransactWriteItems; omit it or use NONE.")
                .ConfigureAwait(false);
            return;
        }
        if (request.ReturnItemCollectionMetrics is not null
            && !string.Equals(
                request.ReturnItemCollectionMetrics,
                "NONE",
                StringComparison.Ordinal))
        {
            await RejectAsync(
                ctx,
                "ReturnItemCollectionMetrics is not supported for TransactWriteItems; omit it or use NONE.")
                .ConfigureAwait(false);
            return;
        }

        var inputs = new InputOp[request.TransactItems.Count];
        string? tableName = null;
        for (var index = 0; index < request.TransactItems.Count; index++)
        {
            var item = request.TransactItems[index];
            if (item is null)
            {
                await RejectAsync(ctx, $"TransactItems[{index}] is required.")
                    .ConfigureAwait(false);
                return;
            }
            if (item.AdditionalProperties is { Count: > 0 } extras)
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

            var hasPut = IsPresentObject(body, item.Put);
            var hasDelete = IsPresentObject(body, item.Delete);
            var hasCheck = IsPresentObject(body, item.ConditionCheck);
            if (item.Update.IsPresent)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Update is not supported. Atomic UpdateExpression execution is outside the certified transaction subset; use Put to replace the full item.")
                    .ConfigureAwait(false);
                return;
            }
            if (item.Put.IsPresent && !hasPut)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Put must be an object.")
                    .ConfigureAwait(false);
                return;
            }
            if (item.Delete.IsPresent && !hasDelete)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].Delete must be an object.")
                    .ConfigureAwait(false);
                return;
            }
            if (item.ConditionCheck.IsPresent && !hasCheck)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].ConditionCheck must be an object.")
                    .ConfigureAwait(false);
                return;
            }

            var present =
                (item.Put.IsPresent ? 1 : 0)
                + (item.Delete.IsPresent ? 1 : 0)
                + (item.ConditionCheck.IsPresent ? 1 : 0);
            if (present != 1)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}] must contain exactly one of Put, Delete, or ConditionCheck.")
                    .ConfigureAwait(false);
                return;
            }

            var kind = hasPut
                ? OpKind.Put
                : hasDelete
                    ? OpKind.Delete
                    : OpKind.Check;
            var range = hasPut
                ? item.Put
                : hasDelete
                    ? item.Delete
                    : item.ConditionCheck;
            var name = hasPut ? "Put" : hasDelete ? "Delete" : "ConditionCheck";

            using var operationDocument = JsonDocument.Parse(
                body.AsMemory(range.Start, range.Length),
                TransactItemParseOptions);
            var operation = operationDocument.RootElement;
            if (!TryValidateOperationMembers(
                    operation,
                    kind,
                    out var memberError))
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].{name} {memberError}")
                    .ConfigureAwait(false);
                return;
            }
            if (!operation.TryGetProperty("TableName", out var tableElement)
                || tableElement.ValueKind != JsonValueKind.String)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].{name}.TableName is required.")
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
                    "TransactWriteItems via aws2azure requires every operation to target the same table because a Cosmos stored-procedure transaction is scoped to one container.")
                    .ConfigureAwait(false);
                return;
            }

            var keyProperty = kind == OpKind.Put ? "Item" : "Key";
            if (!operation.TryGetProperty(keyProperty, out var keyBearer)
                || keyBearer.ValueKind != JsonValueKind.Object)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].{name}.{keyProperty} is required and must be an object.")
                    .ConfigureAwait(false);
                return;
            }

            long baseItemSize = 0;
            if (kind == OpKind.Put)
            {
                if (!ItemHandlers.ValidateItemShape(keyBearer, out var shapeError))
                {
                    await RejectAsync(ctx, shapeError).ConfigureAwait(false);
                    return;
                }
                if (!DynamoDbItemSize.TryCalculate(
                        keyBearer,
                        out baseItemSize,
                        out var itemSizeError))
                {
                    await RejectAsync(ctx, itemSizeError).ConfigureAwait(false);
                    return;
                }
                if (baseItemSize > DynamoDbItemSize.MaximumBytes)
                {
                    await RejectAsync(
                        ctx,
                        $"TransactItems[{index}].Put.Item is {baseItemSize} bytes; " +
                        $"DynamoDB items must not exceed {DynamoDbItemSize.MaximumBytes} bytes (400 KiB).")
                        .ConfigureAwait(false);
                    return;
                }
            }
            else if (!ItemHandlers.ValidateKeyShape(keyBearer, out var keyShapeError))
            {
                await RejectAsync(ctx, keyShapeError).ConfigureAwait(false);
                return;
            }

            if (operation.TryGetProperty(
                    "ExpressionAttributeValues",
                    out var expressionValues)
                && !ItemHandlers.ValidateExpressionAttributeValues(
                    expressionValues,
                    out var expressionValueError))
            {
                await RejectAsync(ctx, expressionValueError).ConfigureAwait(false);
                return;
            }

            ConditionNode? condition;
            try
            {
                condition = ParseCondition(operation, out var conditionError);
                if (conditionError is not null)
                {
                    await RejectAsync(ctx, conditionError).ConfigureAwait(false);
                    return;
                }
            }
            catch (ExpressionSyntaxException exception)
            {
                await RejectAsync(
                    ctx,
                    $"Invalid ConditionExpression (offset {exception.Position}): {exception.Message}")
                    .ConfigureAwait(false);
                return;
            }

            if (kind == OpKind.Check && condition is null)
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].ConditionCheck.ConditionExpression is required.")
                    .ConfigureAwait(false);
                return;
            }
            if (!SprocEligibility.TryValidateTransactionCondition(
                    condition,
                    out var eligibilityError))
            {
                await RejectAsync(
                    ctx,
                    $"TransactItems[{index}].{name}.ConditionExpression is outside the supported transaction subset: {eligibilityError}")
                    .ConfigureAwait(false);
                return;
            }

            inputs[index] = new InputOp(
                kind,
                range,
                name,
                baseItemSize,
                condition);
        }

        if (sprocContext is not { IsSprocEnabled: true } || sprocContext.Manager is null)
        {
            await RejectAsync(
                ctx,
                "TransactWriteItems requires stored procedures, which are disabled in this deployment. Set the DynamoDB stored-procedure mode to Preferred or Required.")
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
        var prepared = new PreparedRequestOp[inputs.Length];
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        string? partitionKey = null;

        for (var index = 0; index < inputs.Length; index++)
        {
            var input = inputs[index];
            using var operationDocument = JsonDocument.Parse(
                body.AsMemory(input.Range.Start, input.Range.Length),
                TransactItemParseOptions);
            var operation = operationDocument.RootElement;
            var keyBearer = operation.GetProperty(
                input.Kind == OpKind.Put ? "Item" : "Key");

            if (input.Kind == OpKind.Put)
            {
                if (!DynamoDbItemSize.TryCalculateWithLocalSecondaryIndexes(
                        keyBearer,
                        metadata,
                        input.BaseItemSize,
                        out var combinedSize,
                        out var combinedSizeError))
                {
                    await RejectAsync(ctx, combinedSizeError)
                        .ConfigureAwait(false);
                    return;
                }
                if (combinedSize > DynamoDbItemSize.MaximumBytes)
                {
                    await RejectAsync(
                        ctx,
                        $"TransactItems[{index}].Put.Item plus its local " +
                        $"secondary index entries is {combinedSize} bytes; " +
                        $"the combined DynamoDB limit is " +
                        $"{DynamoDbItemSize.MaximumBytes} bytes (400 KiB).")
                        .ConfigureAwait(false);
                    return;
                }
            }

            foreach (var keyDefinition in metadata.KeySchema)
            {
                if (!keyBearer.TryGetProperty(keyDefinition.Name, out var attribute))
                {
                    await RejectAsync(
                        ctx,
                        $"TransactItems[{index}].{input.Name} is missing required key attribute '{keyDefinition.Name}'.")
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

            string candidatePartitionKey;
            string documentId;
            string keyError;
            var keyOk = input.Kind == OpKind.Put
                ? ItemKeyFormatter.TryBuildFromItem(
                    keyBearer,
                    metadata,
                    out candidatePartitionKey,
                    out documentId,
                    out keyError)
                : ItemKeyFormatter.TryBuild(
                    keyBearer,
                    metadata,
                    out candidatePartitionKey,
                    out documentId,
                    out keyError);
            if (!keyOk)
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
                    "TransactWriteItems via aws2azure requires every operation to share the same partition-key value because a Cosmos stored-procedure transaction is scoped to one logical partition.")
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

            int? ttlSeconds = null;
            OrderKeyField[]? orderKeys = null;
            if (input.Kind == OpKind.Put)
            {
                try
                {
                    ttlSeconds = TtlTranslation.ComputeItemTtlSeconds(
                        keyBearer,
                        metadata.TimeToLive,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    orderKeys = SecondaryIndexOrderKeys.Compute(
                        metadata,
                        keyBearer);
                }
                catch (ArgumentException exception)
                {
                    await RejectAsync(ctx, exception.Message).ConfigureAwait(false);
                    return;
                }
            }

            prepared[index] = new PreparedRequestOp(
                input.Kind,
                documentId,
                input.Range,
                candidatePartitionKey,
                ttlSeconds,
                orderKeys,
                input.Condition);
        }

        PreparedIdempotency? idempotency = null;
        BoundedPooledByteBufferWriter parameters;
        try
        {
            if (request.ClientRequestToken is not null)
            {
                idempotency = new PreparedIdempotency(
                    BuildIdempotencyRecordId(request.ClientRequestToken),
                    partitionKey!);
            }
            parameters = BuildTransactRequestParamsBody(
                tableName!,
                body,
                prepared,
                idempotency);
        }
        catch (BoundedBufferWriterLimitException)
        {
            await RejectAsync(
                ctx,
                $"The serialized TransactWriteItems stored-procedure request exceeds the Cosmos DB 2 MiB ({MaxSprocRequestBodyBytes}-byte) limit. DynamoDB permits up to 4 MiB, so split this transaction.")
                .ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or FormatException)
        {
            await RejectAsync(
                ctx,
                "One or more ExpressionAttributeValues are not valid DynamoDB attribute values.")
                .ConfigureAwait(false);
            return;
        }

        SprocTransactResult result;
        using (parameters)
        {
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

            var ready = await sprocContext.Manager.EnsureTransactSprocAsync(
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
                    "TransactWriteItems stored procedure could not be provisioned or did not match its versioned body.")
                    .ConfigureAwait(false);
                return;
            }

            result = await sprocContext.Manager.ExecuteTransactAsync(
                cosmos,
                tableName!,
                partitionKey!,
                parameters.WrittenMemory,
                routeResolution.Route,
                ct).ConfigureAwait(false);
            if (idempotency is not null
                && IsRetryableIdempotentConflict(result))
            {
                result = await sprocContext.Manager.ExecuteTransactAsync(
                    cosmos,
                    tableName!,
                    partitionKey!,
                    parameters.WrittenMemory,
                    routeResolution.Route,
                    ct).ConfigureAwait(false);
            }
        }

        if (result.Success)
        {
            await CosmosOpsShared.WriteJsonAsync(
                ctx,
                StatusCodes.Status200OK,
                new TransactWriteItemsResponse(),
                TransactWriteItemsJsonContext.Default.TransactWriteItemsResponse)
                .ConfigureAwait(false);
            return;
        }
        if (result.ValidationFailed)
        {
            await RejectAsync(
                ctx,
                string.IsNullOrWhiteSpace(result.ValidationError)
                    ? "Transaction condition evaluation failed validation."
                    : result.ValidationError)
                .ConfigureAwait(false);
            return;
        }
        if (result.IdempotencyMismatch)
        {
            await CosmosOpsShared.WriteErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "IdempotentParameterMismatchException",
                "The request cannot be retried because its parameters do not match the original request associated with this ClientRequestToken.")
                .ConfigureAwait(false);
            return;
        }
        if (result.ConditionFailed)
        {
            if (!TryReadCancellationReasons(
                    result.ResponseBody,
                    prepared,
                    out var reasons))
            {
                await CosmosOpsShared.WriteErrorAsync(
                    ctx,
                    StatusCodes.Status500InternalServerError,
                    "InternalServerError",
                    "Transaction stored procedure returned malformed or misaligned cancellation reasons.")
                    .ConfigureAwait(false);
                return;
            }

            await WriteTransactionCanceledAsync(ctx, reasons!)
                .ConfigureAwait(false);
            return;
        }

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
        await CosmosOpsShared.WriteErrorAsync(
            ctx,
            status,
            code,
            string.IsNullOrEmpty(result.ErrorBody)
                ? "TransactWriteItems failed; the transaction was rolled back."
                : result.ErrorBody).ConfigureAwait(false);
    }

    private static bool IsPresentObject(byte[] body, JsonRange range)
        => range.IsPresent && body[range.Start] == (byte)'{';

    private static bool TryValidateOperationMembers(
        JsonElement operation,
        OpKind kind,
        out string? error)
    {
        foreach (var property in operation.EnumerateObject())
        {
            if (property.Name is "Expected" or "ConditionalOperator")
            {
                error =
                    $"uses legacy condition member '{property.Name}', which is not supported; use ConditionExpression.";
                return false;
            }
            if (property.Name == "ReturnValuesOnConditionCheckFailure")
            {
                error =
                    "uses ReturnValuesOnConditionCheckFailure, which is not supported because cancellation items are not returned.";
                return false;
            }

            var allowed = property.Name is
                "TableName"
                or "ConditionExpression"
                or "ExpressionAttributeNames"
                or "ExpressionAttributeValues"
                || (kind == OpKind.Put && property.Name == "Item")
                || (kind != OpKind.Put && property.Name == "Key");
            if (!allowed)
            {
                error = $"contains unsupported member '{property.Name}'.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static ConditionNode? ParseCondition(
        JsonElement operation,
        out string? error)
    {
        error = null;
        string? expression = null;
        var hasExpression = operation.TryGetProperty(
            "ConditionExpression",
            out var expressionElement);
        if (hasExpression)
        {
            if (expressionElement.ValueKind != JsonValueKind.String)
            {
                error = "ConditionExpression must be a string.";
                return null;
            }
            expression = expressionElement.GetString();
            if (string.IsNullOrWhiteSpace(expression))
            {
                error = "ConditionExpression must not be empty or whitespace.";
                return null;
            }
        }

        IReadOnlyDictionary<string, string>? names = null;
        if (operation.TryGetProperty(
                "ExpressionAttributeNames",
                out var namesElement))
        {
            if (namesElement.ValueKind != JsonValueKind.Object)
            {
                error = "ExpressionAttributeNames must be a JSON object.";
                return null;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in namesElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    error =
                        $"ExpressionAttributeNames['{property.Name}'] must be a string.";
                    return null;
                }
                values[property.Name] = property.Value.GetString()!;
            }
            names = values;
        }

        IReadOnlyDictionary<string, JsonElement>? expressionValues = null;
        if (operation.TryGetProperty(
                "ExpressionAttributeValues",
                out var valuesElement))
        {
            if (valuesElement.ValueKind != JsonValueKind.Object)
            {
                error = "ExpressionAttributeValues must be a JSON object.";
                return null;
            }
            var values = new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
            foreach (var property in valuesElement.EnumerateObject())
            {
                values[property.Name] = property.Value.Clone();
            }
            expressionValues = values;
        }

        if (!hasExpression)
        {
            if (names is { Count: > 0 } || expressionValues is { Count: > 0 })
            {
                error =
                    "ExpressionAttributeNames/Values were supplied but no ConditionExpression references them.";
                return null;
            }
            return null;
        }

        var parsed = ConditionExpressionParser.ParseWithUsage(
            expression!,
            names,
            expressionValues);
        if (TryFindUnused(names, parsed.ConsumedNames, out var unusedName))
        {
            error =
                $"Value provided in ExpressionAttributeNames unused in expressions: {unusedName}.";
            return null;
        }
        if (TryFindUnused(
                expressionValues,
                parsed.ConsumedValues,
                out var unusedValue))
        {
            error =
                $"Value provided in ExpressionAttributeValues unused in expressions: {unusedValue}.";
            return null;
        }
        return parsed.Node;
    }

    private static bool TryFindUnused<T>(
        IReadOnlyDictionary<string, T>? declared,
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

    private static BoundedPooledByteBufferWriter BuildTransactRequestParamsBody(
        string tableName,
        byte[] body,
        PreparedRequestOp[] operations,
        PreparedIdempotency? idempotency = null)
    {
        var buffer = new BoundedPooledByteBufferWriter(
            MaxSprocRequestBodyBytes,
            initialCapacity: 512,
            maximumScratchSizeHint: MaxSerializerContiguousWriteBytes);
        CanonicalFingerprintWriter? fingerprint = null;
        try
        {
            if (idempotency is not null)
            {
                fingerprint = new CanonicalFingerprintWriter();
                fingerprint.WriteRaw("{\"version\":"u8);
                fingerprint.WriteJsonString(FingerprintVersion);
                fingerprint.WriteRaw(",\"table\":"u8);
                fingerprint.WriteJsonString(tableName);
                fingerprint.WriteRaw(",\"partition\":"u8);
                fingerprint.WriteJsonString(idempotency.Value.PartitionKey);
                fingerprint.WriteRaw(",\"operations\":["u8);
            }

            WriteRaw(buffer, "[["u8);
            for (var index = 0; index < operations.Length; index++)
            {
                if (index > 0)
                {
                    WriteByte(buffer, (byte)',');
                }

                var operation = operations[index];
                if (fingerprint is not null)
                {
                    if (index > 0)
                    {
                        fingerprint.WriteByte((byte)',');
                    }
                    fingerprint.WriteRaw("{\"type\":"u8);
                    fingerprint.WriteJsonString(
                        operation.Kind switch
                        {
                            OpKind.Put => "PUT",
                            OpKind.Delete => "DELETE",
                            _ => "CHECK",
                        });
                    fingerprint.WriteRaw(",\"id\":"u8);
                    fingerprint.WriteJsonString(operation.Id);
                }

                WriteRaw(
                    buffer,
                    operation.Kind switch
                    {
                        OpKind.Put => "{\"type\":\"PUT\",\"id\":"u8,
                        OpKind.Delete => "{\"type\":\"DELETE\",\"id\":"u8,
                        _ => "{\"type\":\"CHECK\",\"id\":"u8,
                    });
                WriteJsonString(buffer, operation.Id);
                if (operation.Kind == OpKind.Put)
                {
                    WriteRaw(buffer, ",\"doc\":"u8);
                    using var operationDocument = JsonDocument.Parse(
                        body.AsMemory(operation.Range.Start, operation.Range.Length),
                        TransactItemParseOptions);
                    InferredAttributeStorage.WriteCosmosDocument(
                        buffer,
                        operation.Id,
                        operation.PartitionKey,
                        operationDocument.RootElement.GetProperty("Item"),
                        operation.TtlSeconds,
                        operation.OrderKeys);
                    if (fingerprint is not null)
                    {
                        fingerprint.WriteRaw(",\"item\":"u8);
                        WriteCanonicalAttributeMap(
                            fingerprint,
                            operationDocument.RootElement.GetProperty("Item"));
                    }
                }

                WriteRaw(buffer, ",\"condition\":"u8);
                fingerprint?.WriteRaw(",\"condition\":"u8);
                if (operation.Condition is null)
                {
                    WriteRaw(buffer, "null"u8);
                    fingerprint?.WriteRaw("null"u8);
                }
                else
                {
                    SprocAstSerializer.WriteCondition(
                        buffer,
                        operation.Condition,
                        fingerprint?.Hash);
                }
                WriteByte(buffer, (byte)'}');
                fingerprint?.WriteByte((byte)'}');
            }
            WriteRaw(buffer, "],"u8);
            if (idempotency is { } token)
            {
                fingerprint!.WriteRaw("]}"u8);
                var digest = fingerprint.Complete();
                WriteRaw(buffer, "{\"id\":"u8);
                WriteJsonString(buffer, token.RecordId);
                WriteRaw(buffer, ",\"pk\":"u8);
                WriteJsonString(buffer, token.PartitionKey);
                WriteRaw(buffer, ",\"fingerprint\":"u8);
                WriteJsonString(buffer, digest);
                WriteRaw(
                    buffer,
                    ",\"windowMs\":600000,\"cleanupTtlSeconds\":660}"u8);
            }
            else
            {
                WriteRaw(buffer, "null"u8);
            }
            WriteByte(buffer, (byte)']');
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        finally
        {
            fingerprint?.Dispose();
        }
    }

    internal static BoundedPooledByteBufferWriter BuildTransactParamsBody(
        PreparedOp[] operations)
    {
        var buffer = new BoundedPooledByteBufferWriter(
            MaxSprocRequestBodyBytes,
            initialCapacity: 512,
            maximumScratchSizeHint: MaxSerializerContiguousWriteBytes);
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartArray();
            WriteOperationsArray(writer, operations);
            writer.WriteEndArray();
            writer.Flush();
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        return buffer;
    }

    private static void WriteOperationsArray(
        Utf8JsonWriter writer,
        PreparedOp[] operations)
    {
        writer.WriteStartArray();
        foreach (var operation in operations)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "type",
                operation.Kind switch
                {
                    OpKind.Put => "PUT",
                    OpKind.Delete => "DELETE",
                    _ => "CHECK",
                });
            writer.WriteString("id", operation.Id);
            if (operation.DocBytes is not null)
            {
                writer.WritePropertyName("doc");
                writer.WriteRawValue(operation.DocBytes);
            }
            writer.WritePropertyName("condition");
            if (operation.ConditionJson is not null)
            {
                writer.WriteRawValue(operation.ConditionJson);
            }
            else
            {
                writer.WriteNullValue();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteJsonString(
        IBufferWriter<byte> output,
        string value)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStringValue(value);
        writer.Flush();
    }

    private static void WriteByte(
        IBufferWriter<byte> output,
        byte value)
    {
        var span = output.GetSpan(1);
        span[0] = value;
        output.Advance(1);
    }

    private static void WriteRaw(
        IBufferWriter<byte> output,
        ReadOnlySpan<byte> value)
    {
        var span = output.GetSpan(value.Length);
        value.CopyTo(span);
        output.Advance(value.Length);
    }

    internal static bool IsWithinTransactRequestBodyLimit(int byteCount)
        => byteCount <= MaxSprocRequestBodyBytes;

    private static bool IsRetryableIdempotentConflict(
        SprocTransactResult result)
        => !result.Success
           && !result.ConditionFailed
           && !result.ValidationFailed
           && !result.IdempotencyMismatch
           && result.StatusCode is 409 or 412 or 449;

    internal static string BuildOperationsJson(PreparedOp[] operations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteOperationsArray(writer, operations);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryReadCancellationReasons(
        string? body,
        PreparedRequestOp[] operations,
        out string[]? reasons)
    {
        reasons = null;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("reasons", out var reasonArray)
                || reasonArray.ValueKind != JsonValueKind.Array
                || reasonArray.GetArrayLength() != operations.Length)
            {
                return false;
            }

            var parsed = new string[operations.Length];
            var index = 0;
            var failed = false;
            foreach (var reason in reasonArray.EnumerateArray())
            {
                if (reason.ValueKind != JsonValueKind.Object
                    || !reason.TryGetProperty("code", out var codeElement)
                    || codeElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var code = codeElement.GetString();
                if (code is not ("None" or "ConditionalCheckFailed"))
                {
                    return false;
                }
                if (code == "ConditionalCheckFailed")
                {
                    if (operations[index].Condition is null)
                    {
                        return false;
                    }
                    failed = true;
                }
                parsed[index] = code;
                index++;
            }
            if (!failed)
            {
                return false;
            }

            reasons = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task WriteTransactionCanceledAsync(
        HttpContext ctx,
        string[] reasons)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "__type",
                "com.amazonaws.dynamodb.v20120810#TransactionCanceledException");
            writer.WriteString(
                "Message",
                "Transaction cancelled, please refer cancellation reasons for specific reasons ["
                + string.Join(", ", reasons)
                + "].");
            writer.WritePropertyName("CancellationReasons");
            writer.WriteStartArray();
            foreach (var code in reasons)
            {
                writer.WriteStartObject();
                writer.WriteString("Code", code);
                if (code == "ConditionalCheckFailed")
                {
                    writer.WriteString(
                        "Message",
                        "The conditional request failed");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "application/x-amz-json-1.0";
        var bytes = stream.ToArray();
        await ctx.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
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

}
