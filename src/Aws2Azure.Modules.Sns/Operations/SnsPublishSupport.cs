using System.Text;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Modules.Sns.Amqp;

namespace Aws2Azure.Modules.Sns.Operations;

internal static class SnsPublishSupport
{
    internal const int MaxBatchEntries = 10;
    internal const string SubjectPropertyName = "aws.sns.Subject";
    internal const string DeduplicationPropertyName = "x-opt-deduplication-id";

    internal static bool TryParsePublishRequest(
        IReadOnlyDictionary<string, string> parameters,
        out PublishRequest request,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (!TryGetRequiredNonEmptyParameter(parameters, "TopicArn", out var topicArn, out error)
            || !TryParsePublishTopicArn(topicArn, out var topicName, out error)
            || !TryGetRequiredNonEmptyParameter(parameters, "Message", out var message, out error)
            || !TryReadMessageAttributes(parameters, "MessageAttributes.entry.", out var attributes, out error))
        {
            request = default!;
            return false;
        }

        parameters.TryGetValue("Subject", out var subject);
        parameters.TryGetValue("MessageStructure", out var messageStructure);
        parameters.TryGetValue("MessageGroupId", out var messageGroupId);
        parameters.TryGetValue("MessageDeduplicationId", out var messageDeduplicationId);

        request = new PublishRequest(
            topicName,
            message,
            subject,
            messageStructure,
            messageGroupId,
            messageDeduplicationId,
            attributes);
        return true;
    }

    internal static bool TryParsePublishBatchRequest(
        IReadOnlyDictionary<string, string> parameters,
        out string topicName,
        out List<PublishBatchRequestEntry> entries,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        topicName = string.Empty;
        entries = [];

        if (!TryGetRequiredNonEmptyParameter(parameters, "TopicArn", out var topicArn, out error)
            || !TryParsePublishTopicArn(topicArn, out topicName, out error))
        {
            return false;
        }

        var indexes = new SortedSet<int>();
        foreach (var key in parameters.Keys)
        {
            if (TryExtractEntryIndex(key, "PublishBatchRequestEntries.member.", out var index))
            {
                indexes.Add(index);
            }
        }

        if (indexes.Count == 0)
        {
            error = "PublishBatchRequestEntries must contain at least one member.";
            return false;
        }

        if (indexes.Count > MaxBatchEntries)
        {
            error = $"PublishBatchRequestEntries must contain at most {MaxBatchEntries} members.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            var prefix = $"PublishBatchRequestEntries.member.{index}.";
            if (!TryGetRequiredNonEmptyParameter(parameters, prefix + "Id", out var id, out error))
            {
                error = $"Entry {index}: {error}";
                return false;
            }

            if (!ids.Add(id))
            {
                error = $"PublishBatchRequestEntries contains duplicate Id '{id}'.";
                return false;
            }

            if (!TryGetRequiredNonEmptyParameter(parameters, prefix + "Message", out var message, out error))
            {
                error = $"Entry {index}: {error}";
                return false;
            }

            if (!TryReadMessageAttributes(parameters, prefix + "MessageAttributes.entry.", out var attributes, out error))
            {
                error = $"Entry {index}: {error}";
                return false;
            }

            parameters.TryGetValue(prefix + "Subject", out var subject);
            parameters.TryGetValue(prefix + "MessageStructure", out var messageStructure);
            parameters.TryGetValue(prefix + "MessageGroupId", out var messageGroupId);
            parameters.TryGetValue(prefix + "MessageDeduplicationId", out var messageDeduplicationId);

            entries.Add(new PublishBatchRequestEntry(
                id,
                message,
                subject,
                messageStructure,
                messageGroupId,
                messageDeduplicationId,
                attributes));
        }

        error = null;
        return true;
    }

    internal static SnsAmqpSendMessage CreateAmqpMessage(PublishRequest request, Guid messageId)
        => CreateAmqpMessage(
            request.Message,
            request.Subject,
            request.MessageGroupId,
            request.MessageDeduplicationId,
            request.MessageAttributes,
            messageId);

    internal static SnsAmqpSendMessage CreateAmqpMessage(PublishBatchRequestEntry request, Guid messageId)
        => CreateAmqpMessage(
            request.Message,
            request.Subject,
            request.MessageGroupId,
            request.MessageDeduplicationId,
            request.MessageAttributes,
            messageId);

    internal static bool TryParsePublishTopicArn(string topicArn, out string topicName, out string? error)
    {
        topicName = string.Empty;
        error = null;

        var parts = topicArn.Split(':', 6, StringSplitOptions.None);
        if (parts.Length != 6
            || !string.Equals(parts[0], "arn", StringComparison.Ordinal)
            || !string.Equals(parts[1], "aws", StringComparison.Ordinal)
            || !string.Equals(parts[2], "sns", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[3])
            || string.IsNullOrWhiteSpace(parts[4])
            || string.IsNullOrWhiteSpace(parts[5]))
        {
            error = "TopicArn must be a valid SNS topic ARN of the form 'arn:aws:sns:{region}:{accountId}:{topicName}'.";
            return false;
        }

        if (!IsValidPublishTopicName(parts[5]))
        {
            error = "TopicArn contained an invalid topic name.";
            return false;
        }

        topicName = parts[5];
        return true;
    }

    private static SnsAmqpSendMessage CreateAmqpMessage(
        string message,
        string? subject,
        string? messageGroupId,
        string? messageDeduplicationId,
        IReadOnlyList<SnsMessageAttribute> messageAttributes,
        Guid messageId)
    {
        Dictionary<string, object?>? applicationProperties = null;
        if (messageAttributes.Count > 0
            || !string.IsNullOrWhiteSpace(subject)
            || !string.IsNullOrWhiteSpace(messageDeduplicationId))
        {
            applicationProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < messageAttributes.Count; i++)
            {
                var attribute = messageAttributes[i];
                applicationProperties[attribute.Name] = attribute.StringValue ?? attribute.BinaryValue ?? string.Empty;
                applicationProperties[attribute.Name + ".DataType"] = attribute.DataType;
            }

            if (!string.IsNullOrWhiteSpace(subject))
            {
                applicationProperties[SubjectPropertyName] = subject;
            }

            if (!string.IsNullOrWhiteSpace(messageDeduplicationId))
            {
                applicationProperties[DeduplicationPropertyName] = messageDeduplicationId;
            }
        }

        return new SnsAmqpSendMessage(
            Encoding.UTF8.GetBytes(message),
            new AmqpProperties
            {
                MessageId = messageId.ToString(),
                Subject = string.IsNullOrWhiteSpace(subject) ? null : subject,
                GroupId = string.IsNullOrWhiteSpace(messageGroupId) ? null : messageGroupId,
            },
            applicationProperties);
    }

    private static bool TryReadMessageAttributes(
        IReadOnlyDictionary<string, string> parameters,
        string prefix,
        out List<SnsMessageAttribute> attributes,
        out string? error)
    {
        var indexes = new SortedSet<int>();
        foreach (var key in parameters.Keys)
        {
            if (TryExtractEntryIndex(key, prefix, out var index))
            {
                indexes.Add(index);
            }
        }

        attributes = [];
        foreach (var index in indexes)
        {
            var attributePrefix = prefix + index + ".";
            if (!TryGetRequiredNonEmptyParameter(parameters, attributePrefix + "Name", out var name, out error)
                || !TryGetRequiredNonEmptyParameter(parameters, attributePrefix + "Value.DataType", out var dataType, out error))
            {
                return false;
            }

            parameters.TryGetValue(attributePrefix + "Value.StringValue", out var stringValue);
            parameters.TryGetValue(attributePrefix + "Value.BinaryValue", out var binaryValue);
            if (string.IsNullOrEmpty(stringValue) && string.IsNullOrEmpty(binaryValue))
            {
                error = $"Message attribute '{name}' must include Value.StringValue or Value.BinaryValue.";
                return false;
            }

            attributes.Add(new SnsMessageAttribute(name, dataType, stringValue, binaryValue));
        }

        error = null;
        return true;
    }

    private static bool TryGetRequiredNonEmptyParameter(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out string value,
        out string? error)
    {
        if (!parameters.TryGetValue(name, out value!) || string.IsNullOrEmpty(value))
        {
            value = string.Empty;
            error = $"Parameter '{name}' is required and must not be empty.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryExtractEntryIndex(string key, string prefix, out int index)
    {
        index = 0;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remaining = key.AsSpan(prefix.Length);
        var separator = remaining.IndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        return int.TryParse(remaining[..separator], out index);
    }

    private static bool IsValidPublishTopicName(string topicName)
        => SnsTopicSupport.IsValidTopicName(topicName)
            || (topicName.EndsWith(".fifo", StringComparison.Ordinal)
                && topicName.Length > 5
                && SnsTopicSupport.IsValidTopicName(topicName[..^5]));
}

internal sealed record PublishRequest(
    string TopicName,
    string Message,
    string? Subject,
    string? MessageStructure,
    string? MessageGroupId,
    string? MessageDeduplicationId,
    IReadOnlyList<SnsMessageAttribute> MessageAttributes);

internal sealed record PublishBatchRequestEntry(
    string Id,
    string Message,
    string? Subject,
    string? MessageStructure,
    string? MessageGroupId,
    string? MessageDeduplicationId,
    IReadOnlyList<SnsMessageAttribute> MessageAttributes);

internal sealed record SnsMessageAttribute(string Name, string DataType, string? StringValue, string? BinaryValue);

internal sealed record PublishBatchSuccess(string Id, string MessageId);

internal sealed record PublishBatchFailure(string Id, string Code, string Message, bool SenderFault);

internal sealed record PublishBatchResult(IReadOnlyList<PublishBatchSuccess> Successful, IReadOnlyList<PublishBatchFailure> Failed);
