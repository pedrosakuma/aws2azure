namespace Aws2Azure.Modules.DynamoDb.WireProtocol;

/// <summary>
/// Outcome of parsing a DynamoDB request. DynamoDB has only one
/// wire form (AWS JSON 1.0) so the result mirrors a successful
/// X-Amz-Target dispatch plus the bytes of the JSON body for per-op
/// handlers to consume directly. The body is held as a byte array so
/// handlers can re-parse with their own typed contexts without paying
/// for another copy.
/// </summary>
/// <param name="Operation">Operation resolved from <c>X-Amz-Target</c>.</param>
/// <param name="Target">Raw target header value (for diagnostics + error responses).</param>
/// <param name="Body">Raw JSON request body (possibly empty for ops with no body, e.g. ListTables defaults).</param>
/// <param name="Error">Non-null if the request could not be classified.</param>
public sealed record DynamoDbParseResult(
    DynamoDbOperation Operation,
    string Target,
    byte[] Body,
    DynamoDbParseError? Error);

/// <summary>
/// Parse-time error surfaced as a DynamoDB-shaped error response.
/// <see cref="Code"/> is the AWS error code (e.g.
/// <c>UnknownOperationException</c>, <c>SerializationException</c>);
/// <see cref="Message"/> is the human-readable message.
/// </summary>
public sealed record DynamoDbParseError(int StatusCode, string Code, string Message);
