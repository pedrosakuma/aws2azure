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

    public static Mapping QueueTagUpdateConflict() =>
        new(StatusCodes.Status503ServiceUnavailable, "ServiceUnavailable",
            "aws2azure: concurrent Azure Service Bus queue updates prevented the tag change; retry the request.",
            SqsErrorResponse.FaultType.Receiver);

    // ---- Send-path mappings (Slice 2) ----

    public static Mapping MessageTooLong() =>
        new(StatusCodes.Status400BadRequest, "InvalidParameterValue",
            "One or more parameters are invalid. " +
            "Reason: Message must be shorter than 1048576 bytes.");

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
            $"Batch requests cannot be longer than 1048576 bytes. You have sent {totalBytes} bytes.");

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

    // ---- Slice 4 mappings (batches, set-attributes, purge) ----

    public static Mapping PurgeQueueInProgress(string queueName) =>
        new(StatusCodes.Status403Forbidden, "AWS.SimpleQueueService.PurgeQueueInProgress",
            $"Only one PurgeQueue operation on {queueName} is allowed every 60 seconds.");

    public static Mapping PurgeStateCapacityExceeded() =>
        new(StatusCodes.Status503ServiceUnavailable, "ServiceUnavailable",
            "aws2azure: the bounded PurgeQueue cooldown tracker is full; retry after the 60-second cooldown window.",
            SqsErrorResponse.FaultType.Receiver);

    public static Mapping InvalidIdFormat(string id) =>
        new(StatusCodes.Status400BadRequest, "InvalidIdFormat",
            $"The receipt handle '{id}' is not a valid format.");

    public static Mapping InvalidAttributeNameForUpdate(string name) =>
        new(StatusCodes.Status400BadRequest, "InvalidAttributeName",
            $"Unknown Attribute {name}.");

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
            // Throttling (HTTP 429) is surfaced as a retryable SQS server error,
            // mirroring the AMQP throttle path (FromAmqp). The shared
            // AzureHttpClient passes 429 straight through without internal retry,
            // so the AWS SDK owns the back-off — a generic InternalError here
            // would not be classified as throttling by the client.
            429 => new Mapping(StatusCodes.Status503ServiceUnavailable, "ServiceUnavailable",
                "aws2azure: Azure Service Bus throttled the request; retry with back-off.",
                SqsErrorResponse.FaultType.Receiver),
            >= 500 => new Mapping(StatusCodes.Status502BadGateway, "ServiceUnavailable",
                "aws2azure: upstream Azure Service Bus returned HTTP " + status + ".",
                SqsErrorResponse.FaultType.Receiver),
            _ => InternalError("aws2azure: upstream Azure Service Bus returned HTTP " + status + "."),
        };
    }

    /// <summary>
    /// Maps an AMQP failure (link / connection exception or settle
    /// outcome on the SB AMQP transport) onto an SQS-shaped error.
    /// Uses the spec-defined <see cref="Aws2Azure.Amqp.Framing.AmqpErrorKind"/>
    /// classification plus a small set of condition-specific overrides
    /// for cases SQS clients pattern-match (lock-lost →
    /// <c>MessageNotInflight</c>, not-found → <c>NonExistentQueue</c>,
    /// throttling / server-fatal → <c>ServiceUnavailable</c>).
    ///
    /// <para>The peer's <c>description</c> is intentionally not leaked
    /// into the SQS message body — it can include broker-internal
    /// diagnostics. Operators retain the full context in the structured
    /// log emitted by the handler.</para>
    /// </summary>
    internal static Mapping FromAmqp(
        Aws2Azure.Amqp.Framing.AmqpErrorKind kind,
        string? condition,
        string operation)
    {
        // Condition-level overrides first — these match what real SQS
        // clients expect for the same logical failures.
        switch (condition)
        {
            case Aws2Azure.Amqp.Framing.AmqpErrorCondition.NotFound:
                return QueueDoesNotExist();

            case Aws2Azure.Amqp.Framing.AmqpErrorCondition.MessageLockLost:
            case Aws2Azure.Amqp.Framing.AmqpErrorCondition.SessionLockLost:
                return MessageNotInflight();

            case Aws2Azure.Amqp.Framing.AmqpErrorCondition.EntityDisabled:
                return new Mapping(StatusCodes.Status403Forbidden, "AccessDenied",
                    "aws2azure: the upstream Service Bus entity is disabled.");

            case Aws2Azure.Amqp.Framing.AmqpErrorCondition.ResourceDeleted:
                return QueueDoesNotExist();
        }

        return kind switch
        {
            Aws2Azure.Amqp.Framing.AmqpErrorKind.Auth =>
                new Mapping(StatusCodes.Status403Forbidden, "AccessDenied",
                    "aws2azure: Azure Service Bus rejected the AMQP credentials."),

            Aws2Azure.Amqp.Framing.AmqpErrorKind.Throttled =>
                new Mapping(StatusCodes.Status503ServiceUnavailable, "ServiceUnavailable",
                    $"aws2azure: Azure Service Bus throttled the {operation} request; retry with back-off.",
                    SqsErrorResponse.FaultType.Receiver),

            Aws2Azure.Amqp.Framing.AmqpErrorKind.Transient =>
                new Mapping(StatusCodes.Status503ServiceUnavailable, "ServiceUnavailable",
                    $"aws2azure: transient upstream failure during {operation}; retry.",
                    SqsErrorResponse.FaultType.Receiver),

            Aws2Azure.Amqp.Framing.AmqpErrorKind.LockLost =>
                MessageNotInflight(),

            Aws2Azure.Amqp.Framing.AmqpErrorKind.ClientFatal =>
                new Mapping(StatusCodes.Status400BadRequest, "InvalidParameterValue",
                    $"aws2azure: Azure Service Bus rejected the {operation} request as malformed."),

            Aws2Azure.Amqp.Framing.AmqpErrorKind.ServerFatal =>
                new Mapping(StatusCodes.Status502BadGateway, "ServiceUnavailable",
                    $"aws2azure: Azure Service Bus reported a server-side failure during {operation}.",
                    SqsErrorResponse.FaultType.Receiver),

            Aws2Azure.Amqp.Framing.AmqpErrorKind.Redirect =>
                new Mapping(StatusCodes.Status502BadGateway, "ServiceUnavailable",
                    "aws2azure: Azure Service Bus requested an AMQP redirect; not yet supported.",
                    SqsErrorResponse.FaultType.Receiver),

            _ => InternalError($"aws2azure: AMQP {operation} failed."),
        };
    }
}
