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

    public static Mapping InvalidParameterValue(string parameter, string message) =>
        new(StatusCodes.Status400BadRequest, "InvalidParameterValue",
            $"Value for parameter {parameter} is invalid. Reason: {message}");

    public static Mapping InvalidAttributeName(string attributeName) =>
        new(StatusCodes.Status400BadRequest, "InvalidAttributeName",
            $"Unknown Attribute {attributeName}.");

    public static Mapping InvalidAttributeValue(string attributeName, string message) =>
        new(StatusCodes.Status400BadRequest, "InvalidAttributeValue",
            $"Attribute {attributeName} value is invalid: {message}");

    public static Mapping QueueNameInvalid() =>
        new(StatusCodes.Status400BadRequest, "InvalidParameterValue",
            "Value for parameter QueueName is invalid. Reason: " +
            "Can only include alphanumeric characters, hyphens, or underscores. " +
            "1 to 80 characters; FIFO queue names must end with '.fifo'.");

    public static Mapping QueueDoesNotExist() =>
        new(StatusCodes.Status400BadRequest, "AWS.SimpleQueueService.NonExistentQueue",
            "The specified queue does not exist for this wsdl version.");

    public static Mapping QueueAlreadyExists(string message) =>
        new(StatusCodes.Status400BadRequest, "QueueAlreadyExists", message);

    // ---- Send-path mappings (Slice 2) ----

    public static Mapping MessageTooLong() =>
        new(StatusCodes.Status400BadRequest, "InvalidParameterValue",
            "One or more parameters are invalid. " +
            "Reason: Message must be shorter than 262144 bytes.");

    public static Mapping MissingParameter(string name) =>
        new(StatusCodes.Status400BadRequest, "MissingParameter",
            $"The request must contain the parameter {name}.");

    public static Mapping EmptyBatchRequest() =>
        new(StatusCodes.Status400BadRequest, "AWS.SimpleQueueService.EmptyBatchRequest",
            "There should be at least one SendMessageBatchRequestEntry in the request.");

    public static Mapping TooManyEntriesInBatchRequest(int count) =>
        new(StatusCodes.Status400BadRequest, "AWS.SimpleQueueService.TooManyEntriesInBatchRequest",
            $"Maximum number of entries per request are 10. You have sent {count}.");

    public static Mapping BatchEntryIdsNotDistinct(string id) =>
        new(StatusCodes.Status400BadRequest, "AWS.SimpleQueueService.BatchEntryIdsNotDistinct",
            $"Id {id} repeated.");

    public static Mapping InvalidBatchEntryId(string id) =>
        new(StatusCodes.Status400BadRequest, "AWS.SimpleQueueService.InvalidBatchEntryId",
            $"A batch entry id can only contain alphanumeric characters, hyphens and underscores. " +
            $"It can be at most 80 letters long. Got: '{id}'.");

    public static Mapping BatchRequestTooLong(int totalBytes) =>
        new(StatusCodes.Status400BadRequest, "AWS.SimpleQueueService.BatchRequestTooLong",
            $"Batch requests cannot be longer than 262144 bytes. You have sent {totalBytes} bytes.");

    // ---- Receive-path mappings (Slice 3) ----

    public static Mapping ReceiveLimitInvalid() =>
        new(StatusCodes.Status400BadRequest, "InvalidParameterValue",
            "Value for parameter MaxNumberOfMessages is invalid. Reason: must be an integer between 1 and 10.");

    public static Mapping ReceiveWaitTimeInvalid() =>
        new(StatusCodes.Status400BadRequest, "InvalidParameterValue",
            "Value for parameter WaitTimeSeconds is invalid. Reason: must be an integer between 0 and 20.");

    public static Mapping VisibilityTimeoutInvalid() =>
        new(StatusCodes.Status400BadRequest, "InvalidParameterValue",
            "Value for parameter VisibilityTimeout is invalid. Reason: must be an integer between 0 and 43200.");

    public static Mapping ReceiptHandleInvalid() =>
        new(StatusCodes.Status404NotFound, "ReceiptHandleIsInvalid",
            "The input receipt handle is invalid.");

    public static Mapping MessageNotInflight() =>
        new(StatusCodes.Status400BadRequest, "MessageNotInflight",
            "The message referred to is not in flight.");

    /// <summary>
    /// Surfaces a non-2xx Service Bus REST response as a best-effort SQS
    /// error. Each per-op handler can decide whether to call this generic
    /// helper or supply a more specific code.
    /// </summary>
    public static Mapping FromServiceBus(System.Net.Http.HttpResponseMessage sb)
    {
        var status = (int)sb.StatusCode;
        return status switch
        {
            401 or 403 => new Mapping(StatusCodes.Status403Forbidden, "AccessDenied",
                "aws2azure: Azure Service Bus rejected the request (HTTP " + status + ")."),
            404 or 410 => QueueDoesNotExist(),
            409 => QueueAlreadyExists(
                "aws2azure: Azure Service Bus reports the queue already exists with different attributes."),
            >= 500 => new Mapping(StatusCodes.Status502BadGateway, "ServiceUnavailable",
                "aws2azure: upstream Azure Service Bus returned HTTP " + status + ".",
                SqsErrorResponse.FaultType.Receiver),
            _ => InternalError("aws2azure: upstream Azure Service Bus returned HTTP " + status + "."),
        };
    }
}
