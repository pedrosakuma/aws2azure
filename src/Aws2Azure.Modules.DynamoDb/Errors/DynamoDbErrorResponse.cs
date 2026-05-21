using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.DynamoDb.Errors;

/// <summary>
/// Renders DynamoDB-shaped errors. DynamoDB has only one wire format
/// (AWS JSON 1.0) so every error is the flat
/// <c>{"__type":"&lt;Service&gt;#&lt;Code&gt;", "message":"..."}</c>
/// envelope all AWS SDKs accept. The HTTP status code carries the
/// retry hint (4xx Sender, 5xx Receiver) — DynamoDB does not emit a
/// separate Type field the way SQS-Query does.
///
/// <para>The <c>__type</c> namespace must be exactly
/// <c>com.amazonaws.dynamodb.v20120810#</c> so SDKs map the error to
/// the matching exception class. The legacy alias
/// <c>com.amazon.coral.service#</c> covers protocol-level errors that
/// the service layer raises before the operation dispatcher
/// (e.g. <c>UnknownOperationException</c>).</para>
/// </summary>
public static class DynamoDbErrorResponse
{
    public const string ServiceTypePrefix = "com.amazonaws.dynamodb.v20120810#";
    public const string CoralTypePrefix = "com.amazon.coral.service#";

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool isProtocolLevel = false)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers["x-amzn-requestid"] = ResolveRequestId(context);
        context.Response.ContentType = "application/x-amz-json-1.0";

        var prefix = isProtocolLevel ? CoralTypePrefix : ServiceTypePrefix;
        var payload = JsonSerializer.Serialize(
            new DynamoDbJsonError(prefix + code, message),
            DynamoDbErrorJsonContext.Default.DynamoDbJsonError);
        await context.Response.WriteAsync(payload).ConfigureAwait(false);
    }

    private static string ResolveRequestId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue("x-amzn-requestid", out var existing)
            && existing.Count > 0 && !string.IsNullOrEmpty(existing[0]))
        {
            return existing[0]!;
        }
        return context.TraceIdentifier;
    }
}

internal sealed record DynamoDbJsonError(
    [property: JsonPropertyName("__type")] string Type,
    [property: JsonPropertyName("message")] string Message);

[JsonSerializable(typeof(DynamoDbJsonError))]
internal sealed partial class DynamoDbErrorJsonContext : JsonSerializerContext
{
}
