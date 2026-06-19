using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Aws2Azure.Modules.Sns.Operations;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Errors;

internal static class SnsPublishErrorMapper
{
    public static SnsPublishOutcome ToPublishOutcome(SnsAmqpException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.Kind == SnsAmqpFailureKind.Auth)
        {
            return SnsPublishOutcome.Failure(
                StatusCodes.Status403Forbidden,
                errorType: "Sender",
                errorCode: "AuthorizationError",
                errorMessage: "Access denied when sending to Azure Service Bus Topics over AMQP.");
        }

        if (exception.Kind == SnsAmqpFailureKind.Throttled)
        {
            // Mirror the SNS REST/Event Grid throttle shape (HTTP 429 / "Throttled"
            // / Sender fault) so the AWS SDK retries with back-off.
            return SnsPublishOutcome.Failure(
                StatusCodes.Status429TooManyRequests,
                errorType: "Sender",
                errorCode: "Throttled",
                errorMessage: "Azure Service Bus Topics throttled the publish request; retry with back-off.");
        }

        return SnsPublishOutcome.Failure(
            StatusCodes.Status500InternalServerError,
            errorType: "Receiver",
            errorCode: "InternalFailure",
            errorMessage: SnsAmqpFailureMessages.Build(exception));
    }

    public static SnsPublishOutcome ToPublishOutcome(EventGridPublishException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return SnsPublishOutcome.Failure(
            exception.Failure.SnsStatusCode,
            errorType: exception.Failure.SenderFault ? "Sender" : "Receiver",
            errorCode: exception.Failure.ErrorCode,
            errorMessage: exception.Failure.ErrorMessage);
    }

    public static Task WriteSendErrorAsync(HttpContext context, SnsPublishOutcome outcome)
        => SnsErrorResponse.WriteErrorAsync(
            context,
            outcome.SnsStatusCode,
            errorType: outcome.ErrorType,
            errorCode: outcome.ErrorCode,
            message: outcome.ErrorMessage);

    public static Task WriteSendErrorAsync(HttpContext context, SnsAmqpException exception)
        => WriteSendErrorAsync(context, ToPublishOutcome(exception));

    public static Task WriteSendErrorAsync(HttpContext context, EventGridPublishException exception)
        => WriteSendErrorAsync(context, ToPublishOutcome(exception));

    public static SnsBatchSendOutcome CreateBatchFailure(SnsAmqpException exception)
    {
        if (exception.Kind == SnsAmqpFailureKind.Auth)
        {
            return new SnsBatchSendOutcome(
                false,
                "AuthorizationError",
                "Access denied when sending to Azure Service Bus Topics over AMQP.",
                SenderFault: false);
        }

        if (exception.Kind == SnsAmqpFailureKind.Throttled)
        {
            return new SnsBatchSendOutcome(
                false,
                "Throttled",
                "Azure Service Bus Topics throttled the publish request; retry with back-off.",
                SenderFault: true);
        }

        return new SnsBatchSendOutcome(
            false,
            "InternalFailure",
            SnsAmqpFailureMessages.Build(exception),
            SenderFault: false);
    }
}
