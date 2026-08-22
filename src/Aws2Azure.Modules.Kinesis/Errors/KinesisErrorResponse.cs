using System.Threading.Tasks;
using Aws2Azure.Core.Modules;
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
    private const string RequestIdHeaderName = "x-amzn-requestid";
    private const string ExtendedRequestIdHeaderName = "x-amz-id-2";

    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        context.Response.Headers[ExtendedRequestIdHeaderName] = context.TraceIdentifier;
        return AwsErrorResponse.WriteAsync(
            context,
            AwsErrorFormat.Json,
            statusCode,
            code,
            message,
            jsonContentType: ContentType,
            requestIdHeaderName: RequestIdHeaderName);
    }

    /// <summary>
    /// Writes the AWS-JSON frontend-rejection envelope
    /// <c>{"__type":"&lt;code&gt;"}</c> with no <c>message</c> field. Real AWS
    /// Kinesis emits this shape for <c>SerializationException</c> and
    /// <c>UnknownOperationException</c> raised by the AWS-JSON parser before
    /// dispatch (issue #854).
    /// </summary>
    public static Task WriteFrontendRejectionAsync(
        HttpContext context,
        int statusCode,
        string code)
    {
        context.Response.Headers[ExtendedRequestIdHeaderName] = context.TraceIdentifier;
        return AwsErrorResponse.WriteJsonWithoutMessageAsync(
            context,
            statusCode,
            code,
            jsonContentType: ContentType,
            requestIdHeaderName: RequestIdHeaderName);
    }
}
