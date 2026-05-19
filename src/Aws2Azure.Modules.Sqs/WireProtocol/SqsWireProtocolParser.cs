using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.WireProtocol;

/// <summary>
/// Detects which on-the-wire SQS form a request uses and parses it into a
/// uniform <see cref="SqsParseResult"/>.
///
/// <para>Detection rules (first match wins):</para>
/// <list type="number">
///   <item><description>
///     <c>X-Amz-Target</c> header present (and starts with <c>AmazonSQS.</c>)
///     → <see cref="SqsWireProtocol.AwsJson"/>. Body parsed as JSON.
///   </description></item>
///   <item><description>
///     <c>Content-Type</c> starts with <c>application/x-amz-json</c> (any
///     version) → <see cref="SqsWireProtocol.AwsJson"/>. The operation
///     comes from the <c>Action</c> JSON property as a fallback.
///   </description></item>
///   <item><description>
///     Otherwise → <see cref="SqsWireProtocol.Query"/>. Operation comes
///     from the <c>Action</c> form/query parameter.
///   </description></item>
/// </list>
///
/// <para>The body is bounded at <see cref="MaxBodyBytes"/> to keep a hostile
/// caller from pinning memory before per-op handlers have a chance to
/// enforce their tighter limits (e.g. SendMessage's 1 MiB body cap).</para>
/// </summary>
public static class SqsWireProtocolParser
{
    /// <summary>
    /// Upper bound on the request body the parser will buffer. Per-op
    /// handlers enforce their own (tighter) limits.
    /// </summary>
    public const int MaxBodyBytes = 1 * 1024 * 1024;

    private const string AwsJsonTargetPrefix = "AmazonSQS.";
    private const string AwsJsonContentTypePrefix = "application/x-amz-json";

    public static async ValueTask<SqsParseResult> ParseAsync(HttpContext context, CancellationToken ct)
    {
        var protocol = Sniff(context);

        if (protocol == SqsWireProtocol.AwsJson)
        {
            var target = context.Request.Headers["X-Amz-Target"].ToString();
            return await ParseAwsJsonAsync(context, target, ct).ConfigureAwait(false);
        }

        return await ParseQueryAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the wire protocol the caller is using based on headers
    /// alone (no body read). Use this when emitting errors before the body
    /// has been parsed (e.g. pre-dispatch auth failures) so the envelope
    /// matches what the SDK will actually try to deserialize.
    /// </summary>
    public static SqsWireProtocol Sniff(HttpContext context)
    {
        var headers = context.Request.Headers;
        var target = headers["X-Amz-Target"].ToString();
        var contentType = headers.ContentType.ToString();

        var isAwsJson =
            (!string.IsNullOrEmpty(target) && target.StartsWith(AwsJsonTargetPrefix, StringComparison.Ordinal))
            || contentType.StartsWith(AwsJsonContentTypePrefix, StringComparison.OrdinalIgnoreCase);

        return isAwsJson ? SqsWireProtocol.AwsJson : SqsWireProtocol.Query;
    }

    private static async ValueTask<SqsParseResult> ParseAwsJsonAsync(
        HttpContext context, string target, CancellationToken ct)
    {
        var (body, tooLarge) = await ReadBoundedBodyAsync(context, ct).ConfigureAwait(false);
        if (tooLarge)
        {
            return new SqsParseResult(
                SqsWireProtocol.AwsJson, SqsOperation.Unknown,
                EmptyParams, JsonBody: null,
                new SqsParseError("InvalidRequest", "Request body exceeds the protocol parser limit."));
        }

        string action = string.Empty;
        if (!string.IsNullOrEmpty(target) && target.StartsWith(AwsJsonTargetPrefix, StringComparison.Ordinal))
        {
            action = target.Substring(AwsJsonTargetPrefix.Length);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        string? jsonText = null;
        if (body.Length > 0)
        {
            jsonText = Encoding.UTF8.GetString(body);
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // Project top-level scalar properties into the flat
                    // parameter dict so simple ops (CreateQueue, GetQueueUrl,
                    // …) can read them without re-parsing JSON.
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        switch (prop.Value.ValueKind)
                        {
                            case JsonValueKind.String:
                                parameters[prop.Name] = prop.Value.GetString() ?? string.Empty;
                                break;
                            case JsonValueKind.Number:
                                parameters[prop.Name] = prop.Value.GetRawText();
                                break;
                            case JsonValueKind.True:
                                parameters[prop.Name] = "true";
                                break;
                            case JsonValueKind.False:
                                parameters[prop.Name] = "false";
                                break;
                        }
                    }

                    // JSON callers may also use the legacy "Action" property
                    // when X-Amz-Target is missing (e.g. proxies that strip
                    // headers). Trust the header when both are present.
                    if (string.IsNullOrEmpty(action) && parameters.TryGetValue("Action", out var a))
                    {
                        action = a;
                    }
                }
            }
            catch (JsonException ex)
            {
                return new SqsParseResult(
                    SqsWireProtocol.AwsJson, SqsOperation.Unknown,
                    EmptyParams, jsonText,
                    new SqsParseError("MalformedQueryString", "Malformed JSON body: " + ex.Message));
            }
        }

        var op = SqsOperationNames.Resolve(action);
        if (op == SqsOperation.Unknown)
        {
            if (string.IsNullOrEmpty(action))
            {
                return new SqsParseResult(
                    SqsWireProtocol.AwsJson, op, parameters, jsonText,
                    new SqsParseError("MissingAction",
                        "Request is missing the required X-Amz-Target header (or Action property)."));
            }
            return new SqsParseResult(
                SqsWireProtocol.AwsJson, op, parameters, jsonText,
                new SqsParseError("InvalidAction", $"Unsupported SQS action: '{action}'."));
        }
        return new SqsParseResult(SqsWireProtocol.AwsJson, op, parameters, jsonText, Error: null);
    }

    private static async ValueTask<SqsParseResult> ParseQueryAsync(HttpContext context, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        // Some callers (notably AWS CLI v1) put parameters on the query
        // string instead of the body. Both forms are equivalent in the
        // SQS Query protocol, so we read whichever the caller used.
        foreach (var kv in context.Request.Query)
        {
            if (kv.Value.Count > 0 && !string.IsNullOrEmpty(kv.Value[0]))
            {
                parameters[kv.Key] = kv.Value[0]!;
            }
        }

        // Per AWS spec the Query protocol accepts parameters on the query
        // string OR in a form-url-encoded body (or both). We always read the
        // body through the bounded reader rather than ASP.NET's form parser
        // so we can enforce a uniform MaxBodyBytes cap and translate failures
        // into SQS-shaped errors instead of letting framework exceptions
        // escape as raw 500s.
        if (context.Request.ContentLength is > 0 || HasUnknownLengthBody(context))
        {
            var (body, tooLarge) = await ReadBoundedBodyAsync(context, ct).ConfigureAwait(false);
            if (tooLarge)
            {
                return new SqsParseResult(
                    SqsWireProtocol.Query, SqsOperation.Unknown,
                    EmptyParams, JsonBody: null,
                    new SqsParseError("InvalidRequest", "Request body exceeds the protocol parser limit."));
            }
            if (body.Length > 0)
            {
                ParseFormUrlEncoded(Encoding.UTF8.GetString(body), parameters);
            }
        }

        if (!parameters.TryGetValue("Action", out var action) || string.IsNullOrEmpty(action))
        {
            return new SqsParseResult(
                SqsWireProtocol.Query, SqsOperation.Unknown, parameters, JsonBody: null,
                new SqsParseError("MissingAction", "Request is missing the required Action parameter."));
        }

        var op = SqsOperationNames.Resolve(action);
        if (op == SqsOperation.Unknown)
        {
            return new SqsParseResult(
                SqsWireProtocol.Query, op, parameters, JsonBody: null,
                new SqsParseError("InvalidAction", $"Unsupported SQS action: '{action}'."));
        }
        return new SqsParseResult(SqsWireProtocol.Query, op, parameters, JsonBody: null, Error: null);
    }

    private static void ParseFormUrlEncoded(string body, IDictionary<string, string> into)
    {
        var span = body.AsSpan();
        while (span.Length > 0)
        {
            var amp = span.IndexOf('&');
            ReadOnlySpan<char> pair;
            if (amp < 0) { pair = span; span = ReadOnlySpan<char>.Empty; }
            else { pair = span[..amp]; span = span[(amp + 1)..]; }

            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            string key, value;
            if (eq < 0) { key = Uri.UnescapeDataString(pair.ToString().Replace('+', ' ')); value = string.Empty; }
            else
            {
                key = Uri.UnescapeDataString(pair[..eq].ToString().Replace('+', ' '));
                value = Uri.UnescapeDataString(pair[(eq + 1)..].ToString().Replace('+', ' '));
            }
            if (!string.IsNullOrEmpty(key))
            {
                into[key] = value;
            }
        }
    }

    private static bool HasUnknownLengthBody(HttpContext context)
    {
        // Chunked-transfer-encoded callers omit Content-Length; we still
        // need to attempt a bounded read so we don't silently drop body
        // parameters from such requests.
        if (context.Request.ContentLength is not null) return false;
        return context.Request.Headers.TryGetValue("Transfer-Encoding", out var te)
            && te.Count > 0
            && !string.IsNullOrEmpty(te[0]);
    }

    private static async ValueTask<(byte[] body, bool tooLarge)> ReadBoundedBodyAsync(
        HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength is { } len && len > MaxBodyBytes)
        {
            return (Array.Empty<byte>(), true);
        }

        using var ms = new MemoryStream();
        var buffer = new byte[8 * 1024];
        long total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxBodyBytes)
            {
                return (Array.Empty<byte>(), true);
            }
            ms.Write(buffer, 0, read);
        }
        return (ms.ToArray(), false);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParams =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
