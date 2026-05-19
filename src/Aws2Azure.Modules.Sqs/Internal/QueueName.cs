using System;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// SQS queue-name validator. Mirrors the AWS-published rules so the proxy
/// rejects bad names before it ever opens an Azure Service Bus connection.
/// </summary>
/// <remarks>
/// AWS rules (see CreateQueue API reference):
/// <list type="bullet">
///   <item>Up to 80 characters.</item>
///   <item>Alphanumeric, hyphens (-), underscores (_).</item>
///   <item>FIFO queues MUST end in <c>.fifo</c>; the suffix counts toward
///         the 80-char limit. The prefix portion still follows the
///         alnum/-/_ rule.</item>
/// </list>
/// Service Bus queue names allow more characters (slashes, periods) but the
/// proxy enforces SQS rules so URLs built by <c>GetQueueUrl</c> round-trip
/// through every AWS SDK without surprise.
/// </remarks>
internal static class QueueName
{
    public const int MaxLength = 80;
    public const string FifoSuffix = ".fifo";

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Length > MaxLength) return false;

        var fifo = name.EndsWith(FifoSuffix, StringComparison.Ordinal);
        var prefix = fifo ? name.AsSpan(0, name.Length - FifoSuffix.Length) : name.AsSpan();
        if (prefix.Length == 0) return false;

        foreach (var c in prefix)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsFifo(string name) =>
        !string.IsNullOrEmpty(name) && name.EndsWith(FifoSuffix, StringComparison.Ordinal);
}
