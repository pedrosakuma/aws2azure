using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.WireProtocol;

/// <summary>
/// Parses an incoming DynamoDB request into a
/// <see cref="DynamoDbParseResult"/>. DynamoDB callers always use the
/// AWS JSON 1.0 protocol — a <c>POST /</c> with
/// <c>X-Amz-Target: DynamoDB_20120810.&lt;Op&gt;</c> and a JSON object
/// body (which may be empty <c>{}</c> for parameterless ops). The
/// parser:
///
/// <list type="bullet">
///   <item><description>
///     Validates the request method (must be POST), the target header,
///     and that the op name maps to a known
///     <see cref="DynamoDbOperation"/>.
///   </description></item>
///   <item><description>
///     Reads the body into a pooled buffer, bounded at
///     <see cref="MaxBodyBytes"/> — Phase 3 handlers enforce their own
///     tighter limits (DynamoDB item-level cap is 400 KiB; BatchWrite
///     is 16 MiB).
///   </description></item>
///   <item><description>
///     Verifies the body parses as a JSON <c>object</c> (or is empty —
///     some SDKs send no body on parameterless requests).
///   </description></item>
/// </list>
///
/// Per-op handlers consume <see cref="DynamoDbParseResult.Body"/> with
/// their own typed source-gen contexts so the parser itself stays
/// allocation-light and op-agnostic.
/// </summary>
public static class DynamoDbWireProtocolParser
{
    /// <summary>
    /// Upper bound on the request body the parser will buffer. AWS
    /// DynamoDB's published item-level cap is 400 KiB and
    /// BatchWriteItem aggregates up to 16 MiB. We allow 17 MiB so
    /// borderline-legal payloads still reach the per-op handler which
    /// can return a clean <c>ValidationException</c>.
    /// </summary>
    public const int MaxBodyBytes = 17 * 1024 * 1024;

    private const string TargetHeader = "X-Amz-Target";

    public static async ValueTask<DynamoDbParseResult> ParseAsync(HttpContext context, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return new DynamoDbParseResult(
                DynamoDbOperation.Unknown, Target: string.Empty, Body: Array.Empty<byte>(),
                Error: new DynamoDbParseError(
                    StatusCodes.Status400BadRequest,
                    "InvalidAction",
                    "DynamoDB requests must use HTTP POST."));
        }

        var target = context.Request.Headers[TargetHeader].ToString();
        if (string.IsNullOrEmpty(target))
        {
            return new DynamoDbParseResult(
                DynamoDbOperation.Unknown, Target: string.Empty, Body: Array.Empty<byte>(),
                Error: new DynamoDbParseError(
                    StatusCodes.Status400BadRequest,
                    "MissingActionException",
                    "X-Amz-Target header is required for DynamoDB requests."));
        }

        var op = DynamoDbOperationNames.FromTarget(target);
        if (op == DynamoDbOperation.Unknown)
        {
            return new DynamoDbParseResult(
                DynamoDbOperation.Unknown, target, Body: Array.Empty<byte>(),
                Error: new DynamoDbParseError(
                    StatusCodes.Status400BadRequest,
                    "UnknownOperationException",
                    $"Unknown DynamoDB operation: {target}"));
        }

        // Reject up front when ContentLength advertises a body bigger than
        // we'll buffer — avoids accepting then truncating, and matches the
        // pattern the registry's pre-validation buffer uses.
        if (context.Request.ContentLength is long cl && cl > MaxBodyBytes)
        {
            return new DynamoDbParseResult(
                op, target, Body: Array.Empty<byte>(),
                Error: new DynamoDbParseError(
                    StatusCodes.Status413PayloadTooLarge,
                    "RequestEntityTooLarge",
                    $"Request body exceeds the {MaxBodyBytes}-byte limit."));
        }

        byte[] body;
        try
        {
            body = await ReadBoundedBodyAsync(context, ct).ConfigureAwait(false);
        }
        catch (RequestBodyTooLargeException ex)
        {
            return new DynamoDbParseResult(
                op, target, Body: Array.Empty<byte>(),
                Error: new DynamoDbParseError(
                    StatusCodes.Status413PayloadTooLarge,
                    "RequestEntityTooLarge",
                    ex.Message));
        }

        if (body.Length > 0 && !LooksLikeJsonObject(body))
        {
            return new DynamoDbParseResult(
                op, target, Body: Array.Empty<byte>(),
                Error: new DynamoDbParseError(
                    StatusCodes.Status400BadRequest,
                    "SerializationException",
                    "Request body must be a JSON object."));
        }

        return new DynamoDbParseResult(op, target, body, Error: null);
    }

    /// <summary>
    /// Pre-body header-only sniff used by error rendering when SigV4 /
    /// auth fails before the body has been read. Currently returns the
    /// resolved operation (or <see cref="DynamoDbOperation.Unknown"/>)
    /// without touching the body — the error response itself doesn't
    /// vary by op, but the diagnostics do.
    /// </summary>
    public static DynamoDbOperation SniffOperation(HttpContext context)
    {
        var target = context.Request.Headers[TargetHeader].ToString();
        return DynamoDbOperationNames.FromTarget(target);
    }

    private static async ValueTask<byte[]> ReadBoundedBodyAsync(HttpContext context, CancellationToken ct)
    {
        var contentLength = context.Request.ContentLength;
        if (contentLength is 0) return Array.Empty<byte>();

        var capacity = contentLength is > 0 && contentLength <= MaxBodyBytes
            ? (int)contentLength.Value
            : MaxBodyBytes;

        using var ms = new MemoryStream(capacity);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            int total = 0;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBodyBytes)
                {
                    // Drain the rest so the connection can be reused; the per-op
                    // handler will get an empty body + size-exceeded marker.
                    throw new RequestBodyTooLargeException(MaxBodyBytes);
                }
                ms.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ms.ToArray();
    }

    private static bool LooksLikeJsonObject(byte[] body)
    {
        // Cheap structural check before paying for a full Utf8JsonReader pass.
        // The first non-whitespace byte must be '{' and the last must be '}'.
        int i = 0;
        while (i < body.Length && (body[i] == (byte)' ' || body[i] == (byte)'\t'
            || body[i] == (byte)'\r' || body[i] == (byte)'\n')) i++;
        if (i >= body.Length || body[i] != (byte)'{') return false;

        // Full pass: validate the JSON structurally — guards against truncated
        // bodies, embedded NULs, etc. Done with Utf8JsonReader to stay
        // allocation-free.
        try
        {
            var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);
            while (reader.Read()) { /* validates as it goes */ }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class RequestBodyTooLargeException : System.Exception
{
    public RequestBodyTooLargeException(int limitBytes)
        : base($"DynamoDB request body exceeds {limitBytes} bytes.")
    {
        LimitBytes = limitBytes;
    }

    public int LimitBytes { get; }
}
