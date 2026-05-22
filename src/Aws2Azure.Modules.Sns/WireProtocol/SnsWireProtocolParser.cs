using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.WireProtocol;

/// <summary>
/// Parses SNS Query requests (<c>application/x-www-form-urlencoded</c>) into
/// an operation + flat parameter map. Slice 1 intentionally rejects the
/// newer SNS JSON protocol so the module stays focused on the AWS Query shape.
/// </summary>
public static class SnsWireProtocolParser
{
    public const int MaxBodyBytes = 5 * 1024 * 1024;

    private const string QueryContentType = "application/x-www-form-urlencoded";

    public static async ValueTask<SnsParseResult> ParseAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return Error(
                SnsParseErrorType.InvalidMethod,
                code: "InvalidParameter",
                message: "SNS requests must use HTTP POST.");
        }

        var contentType = context.Request.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return Error(
                SnsParseErrorType.InvalidContentType,
                code: "InvalidParameter",
                message: "Content-Type must be application/x-www-form-urlencoded for SNS Query requests.");
        }

        if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                SnsParseErrorType.UnsupportedJsonProtocol,
                code: "InvalidParameter",
                message: "SNS JSON protocol is not supported yet; use the AWS Query protocol with application/x-www-form-urlencoded.");
        }

        if (!contentType.StartsWith(QueryContentType, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                SnsParseErrorType.InvalidContentType,
                code: "InvalidParameter",
                message: "Content-Type must be application/x-www-form-urlencoded for SNS Query requests.");
        }

        if (context.Request.ContentLength is long contentLength && contentLength > MaxBodyBytes)
        {
            return Error(
                SnsParseErrorType.InvalidRequest,
                code: "InvalidParameter",
                message: $"SNS request body exceeds the {MaxBodyBytes}-byte parser limit.");
        }

        byte[] body;
        try
        {
            body = await ReadBoundedBodyAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return Error(
                SnsParseErrorType.InvalidRequest,
                code: "InvalidParameter",
                message: ex.Message);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            ParseFormUrlEncoded(body, parameters);
        }
        catch (InvalidDataException ex)
        {
            return Error(
                SnsParseErrorType.InvalidRequest,
                code: "InvalidParameter",
                message: ex.Message);
        }

        if (!parameters.Remove("Action", out var action) || string.IsNullOrEmpty(action))
        {
            return new SnsParseResult(
                SnsOperation.Unknown,
                parameters,
                new SnsParseError(
                    SnsParseErrorType.MissingAction,
                    "MissingAction",
                    "Request is missing the required Action parameter."));
        }

        var operation = SnsOperationNames.Resolve(action);
        if (operation == SnsOperation.Unknown)
        {
            return new SnsParseResult(
                SnsOperation.Unknown,
                parameters,
                new SnsParseError(
                    SnsParseErrorType.UnknownOperation,
                    "InvalidAction",
                    $"Unsupported SNS action: '{action}'."));
        }

        return new SnsParseResult(operation, parameters, Error: null);
    }

    private static SnsParseResult Error(SnsParseErrorType type, string code, string message) =>
        new(SnsOperation.Unknown, EmptyParameters, new SnsParseError(type, code, message));

    private static void ParseFormUrlEncoded(ReadOnlySpan<byte> body, IDictionary<string, string> parameters)
    {
        if (body.Length == 0)
        {
            return;
        }

        var text = System.Text.Encoding.UTF8.GetString(body);
        var span = text.AsSpan();
        while (span.Length > 0)
        {
            var amp = span.IndexOf('&');
            ReadOnlySpan<char> pair;
            if (amp < 0)
            {
                pair = span;
                span = ReadOnlySpan<char>.Empty;
            }
            else
            {
                pair = span[..amp];
                span = span[(amp + 1)..];
            }

            if (pair.Length == 0)
            {
                continue;
            }

            var eq = pair.IndexOf('=');
            string key;
            string value;
            try
            {
                if (eq < 0)
                {
                    key = Uri.UnescapeDataString(pair.ToString().Replace('+', ' '));
                    value = string.Empty;
                }
                else
                {
                    key = Uri.UnescapeDataString(pair[..eq].ToString().Replace('+', ' '));
                    value = Uri.UnescapeDataString(pair[(eq + 1)..].ToString().Replace('+', ' '));
                }
            }
            catch (UriFormatException ex)
            {
                throw new InvalidDataException("SNS request body is not valid application/x-www-form-urlencoded data.", ex);
            }

            if (!string.IsNullOrEmpty(key))
            {
                parameters[key] = value;
            }
        }
    }

    private static async ValueTask<byte[]> ReadBoundedBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is 0)
        {
            return Array.Empty<byte>();
        }

        using var ms = new MemoryStream(context.Request.ContentLength is > 0 and <= MaxBodyBytes
            ? (int)context.Request.ContentLength.Value
            : 16 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            int read;
            var total = 0;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBodyBytes)
                {
                    throw new InvalidDataException($"SNS request body exceeds the {MaxBodyBytes}-byte parser limit.");
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

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
