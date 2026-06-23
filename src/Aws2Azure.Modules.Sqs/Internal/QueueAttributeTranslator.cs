using System;
using System.Collections.Generic;
using System.Globalization;

namespace Aws2Azure.Modules.Sqs.Internal;

/// <summary>
/// Two-way translation between SQS queue attributes (flat string→string)
/// and the Service Bus <c>QueueDescription</c> property set.
///
/// <para>Only the Slice-1 subset is wired here; full FIFO and DLQ
/// translation lands in Slice 5. Unknown SQS attributes are surfaced as
/// <see cref="QueueAttributeError.UnknownAttribute"/> so handlers can
/// emit <c>InvalidAttributeName</c> in the caller's wire protocol.</para>
/// </summary>
internal static class QueueAttributeTranslator
{
    // SQS spec defaults (see CreateQueue / SetQueueAttributes API reference).
    public const int DefaultVisibilityTimeoutSeconds = 30;
    public const int DefaultMessageRetentionPeriodSeconds = 4 * 24 * 60 * 60; // 4 days
    public const int DefaultMaximumMessageSizeBytes = 1048576; // 1 MiB (SQS default since Aug 2025; previously 256 KiB)
    public const int DefaultDelaySeconds = 0;
    public const int DefaultReceiveMessageWaitTimeSeconds = 0;

    public enum QueueAttributeError
    {
        None,
        UnknownAttribute,
        InvalidValue,
        UnsupportedFifoConfiguration,
    }

    public readonly record struct AttributeError(QueueAttributeError Kind, string AttributeName, string Message)
    {
        public bool IsError => Kind != QueueAttributeError.None;
        public static readonly AttributeError Ok = default;
    }

    /// <summary>
    /// Translates the SQS attribute bag into the SB
    /// <c>QueueDescription</c> property bag the Atom writer will emit.
    /// Unrecognised, but harmless, attributes (<c>FifoQueue=true</c> on a
    /// name that already ends in <c>.fifo</c>) are silently accepted.
    /// </summary>
    public static AttributeError ToServiceBusProperties(
        string queueName,
        IReadOnlyDictionary<string, string> sqsAttributes,
        out QueueDescriptionProperties props)
    {
        props = new QueueDescriptionProperties();

        var fifoExpected = QueueName.IsFifo(queueName);
        var fifoRequested = false;

        foreach (var kv in sqsAttributes)
        {
            switch (kv.Key)
            {
                case "VisibilityTimeout":
                    if (!TryParseSeconds(kv.Value, 0, 43200, out var vt))
                        return new AttributeError(QueueAttributeError.InvalidValue, kv.Key,
                            "VisibilityTimeout must be 0..43200 seconds.");
                    props.LockDuration = FormatIso8601Seconds(vt);
                    break;
                case "MessageRetentionPeriod":
                    if (!TryParseSeconds(kv.Value, 60, 1209600, out var mrp))
                        return new AttributeError(QueueAttributeError.InvalidValue, kv.Key,
                            "MessageRetentionPeriod must be 60..1209600 seconds.");
                    props.DefaultMessageTimeToLive = FormatIso8601Seconds(mrp);
                    break;
                case "MaximumMessageSize":
                    if (!TryParseSeconds(kv.Value, 1024, 1048576, out var mms))
                        return new AttributeError(QueueAttributeError.InvalidValue, kv.Key,
                            "MaximumMessageSize must be 1024..1048576 bytes.");
                    // SB Standard tier caps at 256 KiB per message regardless
                    // of what we set; SB Premium honours up to 100 MiB. The
                    // proxy persists the requested value verbatim — if it
                    // exceeds the backing tier's hard cap, SB will reject
                    // oversized payloads on send. Documented in gap docs.
                    props.MaxMessageSizeBytes = mms;
                    break;
                case "DelaySeconds":
                    if (!TryParseSeconds(kv.Value, 0, 900, out var ds))
                        return new AttributeError(QueueAttributeError.InvalidValue, kv.Key,
                            "DelaySeconds must be 0..900 seconds.");
                    props.DelaySeconds = ds;
                    break;
                case "ReceiveMessageWaitTimeSeconds":
                    if (!TryParseSeconds(kv.Value, 0, 20, out var rmwt))
                        return new AttributeError(QueueAttributeError.InvalidValue, kv.Key,
                            "ReceiveMessageWaitTimeSeconds must be 0..20 seconds.");
                    props.ReceiveMessageWaitTimeSeconds = rmwt;
                    break;
                case "FifoQueue":
                    fifoRequested = string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase);
                    if (fifoRequested && !fifoExpected)
                    {
                        return new AttributeError(QueueAttributeError.UnsupportedFifoConfiguration, kv.Key,
                            "FifoQueue=true requires the queue name to end in '.fifo'.");
                    }
                    break;
                case "ContentBasedDeduplication":
                    props.RequiresDuplicateDetection = string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "RedrivePolicy":
                    // SQS lets clients clear an existing RedrivePolicy by
                    // sending an empty string or the empty-object JSON `{}`
                    // on SetQueueAttributes. Surface that as an explicit
                    // "clear" signal so Merge can unset the SB fields
                    // rather than treating null as "keep existing".
                    if (string.IsNullOrWhiteSpace(kv.Value) ||
                        kv.Value.Trim() is "{}" or "\"\"")
                    {
                        props.ClearDeadLetter = true;
                        break;
                    }
                    if (!TryParseRedrivePolicy(kv.Value, queueName, out var dlqName, out var maxRecv, out var dlqError))
                    {
                        return new AttributeError(QueueAttributeError.InvalidValue, kv.Key, dlqError);
                    }
                    props.ForwardDeadLetteredMessagesTo = dlqName;
                    props.MaxDeliveryCount = maxRecv;
                    break;
                case "Policy":
                case "RedriveAllowPolicy":
                case "KmsMasterKeyId":
                case "KmsDataKeyReusePeriodSeconds":
                case "SqsManagedSseEnabled":
                case "DeduplicationScope":
                case "FifoThroughputLimit":
                    // No SB equivalent — accept silently so a single CreateQueue
                    // call carrying these does not error. Documented as
                    // unsupported in the per-attribute gap docs.
                    break;
                default:
                    return new AttributeError(QueueAttributeError.UnknownAttribute, kv.Key,
                        $"Unknown queue attribute: '{kv.Key}'.");
            }
        }

        if (fifoExpected)
        {
            // FIFO requires sessions; full FIFO support lands in Slice 5.
            props.RequiresSession = true;
            props.RequiresDuplicateDetection = true;
        }
        return AttributeError.Ok;
    }

    /// <summary>
    /// Converts a parsed Atom <c>QueueDescription</c> back into the SQS
    /// flat attribute bag returned by <c>GetQueueAttributes</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToSqsAttributes(QueueDescriptionProperties props)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);

        d["VisibilityTimeout"] = ((int)(props.LockDurationSeconds ?? DefaultVisibilityTimeoutSeconds))
            .ToString(CultureInfo.InvariantCulture);
        d["MessageRetentionPeriod"] = ((int)(props.DefaultMessageTimeToLiveSeconds ?? DefaultMessageRetentionPeriodSeconds))
            .ToString(CultureInfo.InvariantCulture);
        d["MaximumMessageSize"] = (props.MaxMessageSizeBytes ?? DefaultMaximumMessageSizeBytes)
            .ToString(CultureInfo.InvariantCulture);
        d["DelaySeconds"] = (props.DelaySeconds ?? DefaultDelaySeconds)
            .ToString(CultureInfo.InvariantCulture);
        d["ReceiveMessageWaitTimeSeconds"] = (props.ReceiveMessageWaitTimeSeconds ?? DefaultReceiveMessageWaitTimeSeconds)
            .ToString(CultureInfo.InvariantCulture);

        if (props.IsFifoCandidate)
        {
            d["FifoQueue"] = "true";
            if (props.RequiresDuplicateDetection == true)
            {
                d["ContentBasedDeduplication"] = "true";
            }
        }

        if (!string.IsNullOrEmpty(props.ForwardDeadLetteredMessagesTo))
        {
            // SQS RedrivePolicy is a JSON-shaped string attribute; the arn
            // is synthetic (no real AWS account) but matches the structure
            // boto3 / AWSSDK expect, so clients that parse it round-trip
            // without surprises. maxReceiveCount falls back to SB's default
            // when not stored on the queue.
            var arn = "arn:aws-azure:sqs:azure-sb::" + props.ForwardDeadLetteredMessagesTo;
            var max = (props.MaxDeliveryCount ?? 10).ToString(CultureInfo.InvariantCulture);
            d["RedrivePolicy"] = "{\"deadLetterTargetArn\":\"" + arn + "\",\"maxReceiveCount\":" + max + "}";
        }

        // SQS exposes ApproximateNumberOfMessages, CreatedTimestamp,
        // LastModifiedTimestamp, QueueArn. CreatedTimestamp / LastModifiedTimestamp
        // come from the Atom <entry> envelope, set by the caller.
        if (props.ApproximateNumberOfMessages is { } cnt)
        {
            d["ApproximateNumberOfMessages"] = cnt.ToString(CultureInfo.InvariantCulture);
        }
        return d;
    }

    /// <summary>
    /// Builds a <see cref="QueueDescriptionProperties"/> that represents
    /// <paramref name="existing"/> with any non-null property from
    /// <paramref name="patch"/> overlaid on top. Used by SetQueueAttributes:
    /// the partial patch comes from <see cref="ToServiceBusProperties"/>
    /// against the caller's attribute bag (which only sets the keys
    /// actually present), and Service Bus requires a full QueueDescription
    /// on update (PUT semantics, not PATCH).
    /// </summary>
    public static QueueDescriptionProperties Merge(
        QueueDescriptionProperties existing, QueueDescriptionProperties patch)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(patch);
        return new QueueDescriptionProperties
        {
            LockDuration                       = patch.LockDuration                       ?? existing.LockDuration,
            LockDurationSeconds                = patch.LockDurationSeconds                ?? existing.LockDurationSeconds,
            DefaultMessageTimeToLive           = patch.DefaultMessageTimeToLive           ?? existing.DefaultMessageTimeToLive,
            DefaultMessageTimeToLiveSeconds    = patch.DefaultMessageTimeToLiveSeconds    ?? existing.DefaultMessageTimeToLiveSeconds,
            MaxMessageSizeBytes                = patch.MaxMessageSizeBytes                ?? existing.MaxMessageSizeBytes,
            DelaySeconds                       = patch.DelaySeconds                       ?? existing.DelaySeconds,
            ReceiveMessageWaitTimeSeconds      = patch.ReceiveMessageWaitTimeSeconds      ?? existing.ReceiveMessageWaitTimeSeconds,
            RequiresSession                    = patch.RequiresSession                    ?? existing.RequiresSession,
            RequiresDuplicateDetection         = patch.RequiresDuplicateDetection         ?? existing.RequiresDuplicateDetection,
            ApproximateNumberOfMessages        = existing.ApproximateNumberOfMessages,
            CreatedAt                          = existing.CreatedAt,
            UpdatedAt                          = existing.UpdatedAt,
            UserMetadata                       = existing.UserMetadata,
            ForwardDeadLetteredMessagesTo      = patch.ClearDeadLetter ? null
                                                 : (patch.ForwardDeadLetteredMessagesTo ?? existing.ForwardDeadLetteredMessagesTo),
            MaxDeliveryCount                   = patch.ClearDeadLetter ? null
                                                 : (patch.MaxDeliveryCount ?? existing.MaxDeliveryCount),
        };
    }

    /// <summary>
    /// Parses an SQS RedrivePolicy JSON document of the form
    /// <c>{"deadLetterTargetArn":"arn:aws:sqs:region:account:queueName","maxReceiveCount":N}</c>.
    /// Returns the DLQ queue name (last segment of the ARN) and maxReceiveCount.
    /// Enforces ARN structure (6 colon-separated parts with <c>arn</c> /
    /// <c>sqs</c> markers) and the SQS rule that the DLQ must match the
    /// source queue's type (both <c>.fifo</c> or both standard) — passing
    /// the source queue name lets us reject the cross-type misconfiguration
    /// before it reaches SB. On failure, sets <paramref name="error"/> and
    /// returns false.
    /// </summary>
    internal static bool TryParseRedrivePolicy(string value, string sourceQueueName,
        out string dlqQueueName, out int maxReceiveCount, out string error)
    {
        dlqQueueName = string.Empty;
        maxReceiveCount = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "RedrivePolicy JSON must not be empty.";
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(value);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                error = "RedrivePolicy must be a JSON object.";
                return false;
            }
            if (!root.TryGetProperty("deadLetterTargetArn", out var arnEl) ||
                arnEl.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                error = "RedrivePolicy is missing required field 'deadLetterTargetArn'.";
                return false;
            }
            if (!root.TryGetProperty("maxReceiveCount", out var maxEl))
            {
                error = "RedrivePolicy is missing required field 'maxReceiveCount'.";
                return false;
            }

            var arn = arnEl.GetString() ?? string.Empty;
            // Required SQS ARN shape: arn:<partition>:sqs:<region>:<account>:<queueName>
            // Six colon-separated segments; segment[0]=="arn", segment[2]=="sqs".
            var segments = arn.Split(':');
            if (segments.Length != 6 ||
                !string.Equals(segments[0], "arn", StringComparison.Ordinal) ||
                !string.Equals(segments[2], "sqs", StringComparison.Ordinal) ||
                segments[1].Length == 0)
            {
                error = "deadLetterTargetArn is not a valid SQS ARN " +
                        "(expected 'arn:<partition>:sqs:<region>:<account>:<queueName>').";
                return false;
            }
            var name = segments[5];
            if (!QueueName.IsValid(name))
            {
                error = "deadLetterTargetArn references an invalid queue name.";
                return false;
            }

            // FIFO/standard must match: SQS rejects mixed-type DLQ wiring,
            // and SB silently honours either side which would break the
            // sender's ordering assumptions.
            var sourceIsFifo = !string.IsNullOrEmpty(sourceQueueName)
                && sourceQueueName.EndsWith(".fifo", StringComparison.Ordinal);
            var targetIsFifo = name.EndsWith(".fifo", StringComparison.Ordinal);
            if (sourceIsFifo != targetIsFifo)
            {
                error = sourceIsFifo
                    ? "A FIFO source queue requires a FIFO (.fifo) dead-letter target."
                    : "A standard source queue requires a standard (non-.fifo) dead-letter target.";
                return false;
            }

            int parsed;
            if (maxEl.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (!maxEl.TryGetInt32(out parsed))
                {
                    error = "maxReceiveCount must fit in a 32-bit integer.";
                    return false;
                }
            }
            else if (maxEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                // AWS-JSON sometimes serializes numerics inside an embedded
                // JSON string. Accept the same to keep boto3 happy.
                if (!int.TryParse(maxEl.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out parsed))
                {
                    error = "maxReceiveCount is not a valid integer.";
                    return false;
                }
            }
            else
            {
                error = "maxReceiveCount must be a number.";
                return false;
            }

            if (parsed < 1 || parsed > 1000)
            {
                error = "maxReceiveCount must be between 1 and 1000.";
                return false;
            }

            dlqQueueName = name;
            maxReceiveCount = parsed;
            return true;
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = "RedrivePolicy is not valid JSON: " + ex.Message;
            return false;
        }
    }

    private static bool TryParseSeconds(string value, int min, int max, out int parsed)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
            parsed >= min && parsed <= max)
        {
            return true;
        }
        parsed = 0;
        return false;
    }

    /// <summary>
    /// Formats an integer seconds value as an ISO-8601 duration with hours,
    /// minutes, and seconds — the shape Service Bus accepts for properties
    /// like LockDuration and DefaultMessageTimeToLive.
    /// </summary>
    public static string FormatIso8601Seconds(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return System.Xml.XmlConvert.ToString(ts);
    }
}

/// <summary>
/// Strongly-typed view of the subset of <c>QueueDescription</c> properties
/// the SQS module reads/writes. Times are kept as nullable seconds to make
/// the round-trip with SQS attrs (which are seconds-granular) lossless.
/// </summary>
internal sealed class QueueDescriptionProperties
{
    public string? LockDuration { get; set; }
    public double? LockDurationSeconds { get; set; }
    public string? DefaultMessageTimeToLive { get; set; }
    public double? DefaultMessageTimeToLiveSeconds { get; set; }
    public int? MaxMessageSizeBytes { get; set; }
    public int? DelaySeconds { get; set; }
    public int? ReceiveMessageWaitTimeSeconds { get; set; }
    public bool? RequiresSession { get; set; }
    public bool? RequiresDuplicateDetection { get; set; }
    public long? ApproximateNumberOfMessages { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UserMetadata { get; set; }

    /// <summary>
    /// SB queue name (short, no path / namespace prefix) of the dead-letter
    /// queue messages are forwarded to when they exceed
    /// <see cref="MaxDeliveryCount"/>. Maps to SQS RedrivePolicy's
    /// <c>deadLetterTargetArn</c>; <see langword="null"/> when the queue
    /// has no DLQ wired.
    /// </summary>
    public string? ForwardDeadLetteredMessagesTo { get; set; }

    /// <summary>
    /// SB queue's MaxDeliveryCount — the number of receive attempts before
    /// a message is dead-lettered. Maps to SQS RedrivePolicy's
    /// <c>maxReceiveCount</c>. SQS allows 1..1000; SB defaults to 10.
    /// </summary>
    public int? MaxDeliveryCount { get; set; }

    /// <summary>
    /// True when the caller explicitly asked to clear an existing DLQ
    /// configuration (via <c>RedrivePolicy = ""</c> or <c>{}</c>). Distinct
    /// from <see cref="ForwardDeadLetteredMessagesTo"/> being null, which
    /// <see cref="QueueAttributeTranslator.Merge"/> treats as "no opinion".
    /// </summary>
    public bool ClearDeadLetter { get; set; }

    /// <summary>
    /// True when the queue's SB shape (RequiresSession=true) corresponds to
    /// what the SQS module would model as a FIFO queue.
    /// </summary>
    public bool IsFifoCandidate => RequiresSession == true;
}
