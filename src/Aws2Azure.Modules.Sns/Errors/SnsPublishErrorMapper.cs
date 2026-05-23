using Aws2Azure.Amqp.Framing;
using Aws2Azure.Modules.Sns.Amqp;
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

        return SnsErrorResponse.WriteErrorAsync(
            context,
            StatusCodes.Status500InternalServerError,
            errorType: "Receiver",
            errorCode: "InternalFailure",
            message: BuildFailureMessage(exception));
    }

    public static SnsBatchSendOutcome CreateBatchFailure(SnsAmqpException exception)
        => exception.Kind == SnsAmqpFailureKind.Auth
            ? new SnsBatchSendOutcome(
                false,
                "AuthorizationError",
                "Access denied when sending to Azure Service Bus Topics over AMQP.",
                SenderFault: false)
            : new SnsBatchSendOutcome(
                false,
                "InternalFailure",
                BuildFailureMessage(exception),
                SenderFault: false);

    private static string BuildFailureMessage(SnsAmqpException exception)
    {
        var message = string.Equals(exception.Condition, AmqpErrorCondition.Timeout, StringComparison.Ordinal)
            ? "Azure Service Bus Topics AMQP send timed out."
            : "Azure Service Bus Topics AMQP send failed.";
        if (!string.IsNullOrWhiteSpace(exception.Description))
        {
            message += " " + exception.Description;
        }

        return message;
    }
}
