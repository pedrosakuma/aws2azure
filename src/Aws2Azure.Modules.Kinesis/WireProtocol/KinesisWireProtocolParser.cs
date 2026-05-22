using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.WireProtocol;

/// <summary>
/// Parses an incoming Kinesis request into a
/// <see cref="KinesisParseResult"/>. Kinesis callers use AWS JSON 1.1
/// — a <c>POST /</c> with <c>X-Amz-Target: Kinesis_20131202.&lt;Op&gt;</c>
/// and a JSON object body.
///
/// <para>Per-op handlers consume <see cref="KinesisParseResult.Body"/>
/// with their own source-gen contexts so the parser stays
/// allocation-light and op-agnostic.</para>
/// </summary>
public static class KinesisWireProtocolParser
{
    /// <summary>
    /// Upper bound on the request body the parser will buffer.
    /// Kinesis PutRecords aggregates up to 5 MiB across the batch; the
    /// per-record cap is 1 MiB. We allow 6 MiB so borderline-legal
    /// payloads reach the handler for a clean
    /// <c>ValidationException</c>.
    /// </summary>
    public const int MaxBodyBytes = 6 * 1024 * 1024;

    private const string TargetHeader = "X-Amz-Target";

    public static async ValueTask<KinesisParseResult> ParseAsync(HttpContext context, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return new KinesisParseResult(
                KinesisOperation.Unknown, Target: string.Empty, Body: Array.Empty<byte>(),
                Error: new KinesisParseError(
                    StatusCodes.Status400BadRequest,
                    "InvalidAction",
                    "Kinesis requests must use HTTP POST."));
        }

        var target = context.Request.Headers[TargetHeader].ToString();
        if (string.IsNullOrEmpty(target))
        {
            return new KinesisParseResult(
                KinesisOperation.Unknown, Target: string.Empty, Body: Array.Empty<byte>(),
                Error: new KinesisParseError(
                    StatusCodes.Status400BadRequest,
                    "MissingActionException",
                    "X-Amz-Target header is required for Kinesis requests."));
        }

        var op = KinesisOperationNames.FromTarget(target);
        if (op == KinesisOperation.Unknown)
        {
            return new KinesisParseResult(
                KinesisOperation.Unknown, target, Body: Array.Empty<byte>(),
                Error: new KinesisParseError(
                    StatusCodes.Status400BadRequest,
                    "UnknownOperationException",
                    $"Unknown Kinesis operation: {target}"));
        }

        if (context.Request.ContentLength is long cl && cl > MaxBodyBytes)
        {
            return new KinesisParseResult(
                op, target, Body: Array.Empty<byte>(),
                Error: new KinesisParseError(
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
            return new KinesisParseResult(
                op, target, Body: Array.Empty<byte>(),
                Error: new KinesisParseError(
                    StatusCodes.Status413PayloadTooLarge,
                    "RequestEntityTooLarge",
                    ex.Message));
        }

        if (body.Length > 0 && !LooksLikeJsonObject(body))
        {
            return new KinesisParseResult(
                op, target, Body: Array.Empty<byte>(),
                Error: new KinesisParseError(
                    StatusCodes.Status400BadRequest,
                    "SerializationException",
                    "Request body must be a JSON object."));
        }

        return new KinesisParseResult(op, target, body, Error: null);
    }

    /// <summary>
    /// Pre-body header-only sniff used by error rendering when SigV4 /
    /// auth fails before the body has been read.
    /// </summary>
    public static KinesisOperation SniffOperation(HttpContext context)
    {
        var target = context.Request.Headers[TargetHeader].ToString();
        return KinesisOperationNames.FromTarget(target);
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
        int i = 0;
        while (i < body.Length && (body[i] == (byte)' ' || body[i] == (byte)'\t'
            || body[i] == (byte)'\r' || body[i] == (byte)'\n')) i++;
        if (i >= body.Length || body[i] != (byte)'{') return false;

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
        : base($"Kinesis request body exceeds {limitBytes} bytes.")
    {
        LimitBytes = limitBytes;
    }

    public int LimitBytes { get; }
}
