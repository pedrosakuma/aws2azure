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
    public bool ValidationFailed { get; init; }
    public string? ValidationError { get; init; }
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

    /// <summary>
    /// The stored procedure detected a DynamoDB condition operand/type validation
    /// error before issuing any writes.
    /// </summary>
    public bool ValidationFailed { get; init; }

    public string? ValidationError { get; init; }

    /// <summary>
    /// The token exists inside the active idempotency window but its canonical
    /// request fingerprint differs from this request.
    /// </summary>
    public bool IdempotencyMismatch { get; init; }

    /// <summary>HTTP status when the sproc call itself failed (non-2xx).</summary>
    public int StatusCode { get; init; }

    public string? ErrorBody { get; init; }
    public string? ResponseBody { get; init; }
}

/// <summary>
/// Result of an <c>atomicTransactGet</c> snapshot execution.
/// </summary>
internal sealed class SprocTransactGetResult
{
    public bool Attempted { get; init; }
    public bool Success { get; init; }
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
