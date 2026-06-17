using Aws2Azure.Modules.Sns.Amqp;
using Aws2Azure.Modules.Sns.EventGrid;
using Microsoft.AspNetCore.Http;

namespace Aws2Azure.Modules.Sns.Errors;

internal static class SnsPublishErrorMapper
{
    public static Task WriteSendErrorAsync(HttpContext context, SnsAmqpException exception)
    {
        if (exception.Kind == SnsAmqpFailureKind.Auth)
        {
            return SnsErrorResponse.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                errorType: "Sender",
                errorCode: "AuthorizationError",
                message: "Access denied when sending to Azure Service Bus Topics over AMQP.");
        }

        if (exception.Kind == SnsAmqpFailureKind.Throttled)
        {
            // Mirror the SNS REST/Event Grid throttle shape (HTTP 429 / "Throttled"
            // / Sender fault) so the AWS SDK retries with back-off.
            return SnsErrorResponse.WriteErrorAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                errorType: "Sender",
                errorCode: "Throttled",
                message: "Azure Service Bus Topics throttled the publish request; retry with back-off.");
        }

        return SnsErrorResponse.WriteErrorAsync(
            context,
            StatusCodes.Status500InternalServerError,
            errorType: "Receiver",
            errorCode: "InternalFailure",
            message: SnsAmqpFailureMessages.Build(exception));
    }

    public static Task WriteSendErrorAsync(HttpContext context, EventGridPublishException exception)
        => SnsErrorResponse.WriteErrorAsync(
            context,
            exception.Failure.SnsStatusCode,
            errorType: exception.Failure.SenderFault ? "Sender" : "Receiver",
            errorCode: exception.Failure.ErrorCode,
            message: exception.Failure.ErrorMessage);

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
