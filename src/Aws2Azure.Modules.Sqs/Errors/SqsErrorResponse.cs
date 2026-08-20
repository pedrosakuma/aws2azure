using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;
using Aws2Azure.Core.Xml;
using Aws2Azure.Modules.Sqs.WireProtocol;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Errors;

/// <summary>
/// Renders an SQS-shaped error in the protocol the caller used. Query
/// callers get the classic XML <c>&lt;ErrorResponse&gt;</c> envelope;
/// AWS-JSON callers get <c>{"__type":"...","message":"..."}</c>. SQS-style
/// errors carry a <c>Type</c> (<c>Sender</c> or <c>Receiver</c>) which
/// SDKs use to decide retry behaviour.
/// </summary>
public static class SqsErrorResponse
{
    public enum FaultType { Sender, Receiver }

    public static async Task WriteAsync(
        HttpContext context,
        SqsWireProtocol protocol,
        int statusCode,
        string code,
        string message,
        FaultType faultType = FaultType.Sender)
    {
        context.Response.StatusCode = statusCode;
        var requestId = ResolveRequestId(context);
        context.Response.Headers["x-amzn-requestid"] = requestId;

        if (protocol == SqsWireProtocol.AwsJson)
        {
            context.Response.ContentType = "application/x-amz-json-1.0";
            // AWS-JSON SQS errors use a flat {"__type":"<Service>#<Code>",
            // "message":"..."} envelope. The HTTP status code carries the
            // Sender/Receiver hint (4xx = Sender, 5xx = Receiver), so we
            // don't need to emit Type separately.
            //
            // The AWS-JSON protocol's error code is the Smithy exception
            // shape name (e.g. "QueueDoesNotExist"), which for several SQS
            // errors differs from the legacy Query-protocol code string
            // (e.g. "AWS.SimpleQueueService.NonExistentQueue"). The modern
            // AWS SDKs default SQS clients to the JSON protocol, so an
            // unmapped code here silently degrades every SDK client to a
            // generic, untyped exception. Translate known legacy codes to
            // their JSON-protocol shape name; codes with no entry (already
            // shape-name-shaped, or without a dedicated modeled exception)
            // pass through unchanged.
            var jsonCode = SqsJsonErrorCodes.LegacyCodeToJsonShapeName.GetValueOrDefault(code, code);
            var payload = JsonSerializer.Serialize(
                new SqsJsonError("com.amazonaws.sqs#" + jsonCode, message),
                SqsErrorJsonContext.Default.SqsJsonError);
            await context.Response.WriteAsync(payload).ConfigureAwait(false);
            return;
        }

        // Default to the SQS query-protocol XML envelope: this matches
        // every SQS SDK error parser before the JSON migration.
        context.Response.ContentType = "text/xml; charset=utf-8";
        await context.Response.WriteAsync(
            BuildQueryXml(code, message, faultType, requestId)).ConfigureAwait(false);
    }

    public static string BuildQueryXml(string code, string message, FaultType faultType, string requestId)
    {
        // XmlWriter.Create(StringBuilder, …) always emits encoding="utf-16"
        // in the XML declaration regardless of the requested Encoding setting,
        // because StringBuilder is a UTF-16 sink. We then write the response
        // bytes as UTF-8, so strict XML parsers can choke on the declared/
        // actual encoding mismatch. Route the writer through a UTF-8-aware
        // StringWriter so the declaration matches the wire encoding.
        var sb = new StringBuilder();
        using (var sw = new Utf8StringWriter(sb))
        using (var w = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = Encoding.UTF8,
            CloseOutput = false,
        }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("ErrorResponse", "http://queue.amazonaws.com/doc/2012-11-05/");
            w.WriteStartElement("Error");
            w.WriteElementString("Type", faultType.ToString());
            w.WriteElementString("Code", code);
            w.WriteElementString("Message", message);
            w.WriteEndElement(); // Error
            w.WriteElementString("RequestId", requestId);
            w.WriteEndElement(); // ErrorResponse
            w.WriteEndDocument();
            w.Flush();
        }
        return sb.ToString();
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

internal static class SqsJsonErrorCodes
{
    // Confirmed against the registered exception-code checks in
    // AWSSDK.SQS's response unmarshallers (JSON/"AwsJson" protocol): the
    // wire error code there is the Smithy exception shape name, which for
    // these entries differs from the legacy Query-protocol code emitted by
    // SqsErrorMapping.
    public static readonly FrozenDictionary<string, string> LegacyCodeToJsonShapeName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AWS.SimpleQueueService.NonExistentQueue"] = "QueueDoesNotExist",
            ["AWS.SimpleQueueService.BatchEntryIdsNotDistinct"] = "BatchEntryIdsNotDistinct",
            ["AWS.SimpleQueueService.BatchRequestTooLong"] = "BatchRequestTooLong",
            ["AWS.SimpleQueueService.EmptyBatchRequest"] = "EmptyBatchRequest",
            ["AWS.SimpleQueueService.InvalidBatchEntryId"] = "InvalidBatchEntryId",
            ["AWS.SimpleQueueService.PurgeQueueInProgress"] = "PurgeQueueInProgress",
            ["AWS.SimpleQueueService.TooManyEntriesInBatchRequest"] = "TooManyEntriesInBatchRequest",
            ["QueueAlreadyExists"] = "QueueNameExists",
        }.ToFrozenDictionary(StringComparer.Ordinal);
}

internal sealed record SqsJsonError(
    [property: JsonPropertyName("__type")] string Type,
    [property: JsonPropertyName("message")] string Message);

[JsonSerializable(typeof(SqsJsonError))]
internal sealed partial class SqsErrorJsonContext : JsonSerializerContext
{
}
