using System;
using Aws2Azure.Modules.Sqs.Errors;

namespace Aws2Azure.Modules.Sqs.Operations;

internal static class SqsFifoSendValidator
{
    internal const int MaxMessageGroupIdLength = 128;

    internal static SqsErrorMapping.Mapping? Validate(string queueName, string? groupId, string? deduplicationId)
    {
        var isFifoQueue = queueName.EndsWith(".fifo", StringComparison.Ordinal);
        if (isFifoQueue && string.IsNullOrEmpty(groupId))
            return SqsErrorMapping.MissingParameter("MessageGroupId");

        if (groupId is not null && !IsValidMessageGroupId(groupId))
            return SqsErrorMapping.InvalidParameterValue("MessageGroupId",
                "MessageGroupId must be 1 to 128 characters containing only alphanumeric characters and punctuation.");

        if (isFifoQueue && string.IsNullOrEmpty(deduplicationId))
            return SqsErrorMapping.MissingParameter("MessageDeduplicationId");

        if (!isFifoQueue && !string.IsNullOrEmpty(deduplicationId))
            return SqsErrorMapping.InvalidParameterValue("MessageDeduplicationId",
                "MessageDeduplicationId is only valid on FIFO queues.");

        return null;
    }

    private static bool IsValidMessageGroupId(string groupId)
    {
        if (groupId.Length is 0 or > MaxMessageGroupIdLength)
            return false;

        foreach (var character in groupId)
        {
            if (character is < '!' or > '~')
                return false;
        }

        return true;
    }
}
