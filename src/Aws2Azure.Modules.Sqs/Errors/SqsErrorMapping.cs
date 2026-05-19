using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sqs.Errors;

/// <summary>
/// Common SQS error code helpers. Each entry tracks the HTTP status, the
/// SQS error code clients pattern-match, and the Sender/Receiver fault
/// hint used for retry semantics.
/// </summary>
public static class SqsErrorMapping
{
    public readonly record struct Mapping(
        int StatusCode,
        string Code,
        string Message,
        SqsErrorResponse.FaultType FaultType = SqsErrorResponse.FaultType.Sender);

    public static Mapping NotImplemented(SqsOperation op) =>
        new(StatusCodes.Status501NotImplemented, "NotImplemented",
            $"aws2azure: SQS operation '{op}' is not yet implemented.",
            SqsErrorResponse.FaultType.Receiver);

    public static Mapping MissingCredentials() =>
        new(StatusCodes.Status403Forbidden, "AccessDenied",
            "aws2azure: no Azure Service Bus credentials configured for this access key.");

    public static Mapping InvalidAction(string message) =>
        new(StatusCodes.Status400BadRequest, "InvalidAction", message);

    public static Mapping InvalidRequest(string message) =>
        new(StatusCodes.Status400BadRequest, "InvalidRequest", message);

    public static Mapping MalformedQueryString(string message) =>
        new(StatusCodes.Status400BadRequest, "MalformedQueryString", message);

    public static Mapping InternalError(string message) =>
        new(StatusCodes.Status500InternalServerError, "InternalFailure", message,
            SqsErrorResponse.FaultType.Receiver);
}
