namespace Aws2Azure.Modules.Kinesis.WireProtocol;

/// <param name="Operation">Operation resolved from <c>X-Amz-Target</c>.</param>
/// <param name="Target">Raw target header value (for diagnostics + error responses).</param>
/// <param name="Body">Raw JSON request body (empty for ops with no body).</param>
/// <param name="Error">Non-null if the request could not be classified.</param>
public sealed record KinesisParseResult(
    KinesisOperation Operation,
    string Target,
    byte[] Body,
    KinesisParseError? Error);

/// <summary>
/// Parse-time error surfaced as a Kinesis-shaped error response.
/// </summary>
public sealed record KinesisParseError(int StatusCode, string Code, string Message);
