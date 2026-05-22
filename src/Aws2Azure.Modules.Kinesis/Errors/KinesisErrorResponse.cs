using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Kinesis.Errors;

/// <summary>
/// Renders Kinesis-shaped errors. Kinesis uses AWS JSON 1.1 so every
/// error is the flat <c>{"__type":"&lt;Code&gt;", "message":"..."}</c>
/// envelope. The HTTP status code carries the retry hint (4xx Sender,
/// 5xx Receiver).
///
/// <para>Unlike DynamoDB, Kinesis SDKs accept the bare error code in
/// <c>__type</c> (no <c>com.amazonaws.kinesis.v20131202#</c> namespace
/// prefix) and the AWS SDKs we target (boto3, AWSSDK.NET, Java v2)
/// all parse the flat form correctly. Protocol-level errors raised
/// before the operation dispatcher (e.g.
/// <c>UnknownOperationException</c>) are wire-identical to op-level
/// errors at the parse layer.</para>
/// </summary>
public static class KinesisErrorResponse
{
    public const string ContentType = "application/x-amz-json-1.1";

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers["x-amzn-requestid"] = ResolveRequestId(context);
        context.Response.ContentType = ContentType;

        var payload = JsonSerializer.Serialize(
            new KinesisJsonError(code, message),
            KinesisErrorJsonContext.Default.KinesisJsonError);
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

internal sealed record KinesisJsonError(
    [property: JsonPropertyName("__type")] string Type,
    [property: JsonPropertyName("message")] string Message);

[JsonSerializable(typeof(KinesisJsonError))]
internal sealed partial class KinesisErrorJsonContext : JsonSerializerContext
{
}
