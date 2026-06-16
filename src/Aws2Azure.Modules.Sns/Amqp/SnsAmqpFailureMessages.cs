using System;
using Aws2Azure.Amqp.Framing;

namespace Aws2Azure.Modules.Sns.Amqp;

internal static class SnsAmqpFailureMessages
{
    internal static string Build(SnsAmqpException exception)
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
