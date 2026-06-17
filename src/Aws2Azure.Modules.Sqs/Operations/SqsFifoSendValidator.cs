using System;
using Aws2Azure.Modules.Sqs.Errors;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsFifoSendValidator
{
    internal static SqsErrorMapping.Mapping? Validate(string queueName, string? groupId, string? deduplicationId)
    {
        var isFifoQueue = queueName.EndsWith(".fifo", StringComparison.Ordinal);
        if (isFifoQueue && string.IsNullOrEmpty(groupId))
            return SqsErrorMapping.MissingParameter("MessageGroupId");

        if (isFifoQueue && string.IsNullOrEmpty(deduplicationId))
            return SqsErrorMapping.MissingParameter("MessageDeduplicationId");

        if (!isFifoQueue && !string.IsNullOrEmpty(groupId))
            return SqsErrorMapping.InvalidParameterValue("MessageGroupId",
                "MessageGroupId is only valid on FIFO queues.");

        if (!isFifoQueue && !string.IsNullOrEmpty(deduplicationId))
            return SqsErrorMapping.InvalidParameterValue("MessageDeduplicationId",
                "MessageDeduplicationId is only valid on FIFO queues.");

        return null;
    }
}
