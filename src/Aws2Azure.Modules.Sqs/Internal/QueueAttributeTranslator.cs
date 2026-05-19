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
                case "Policy":
                case "RedrivePolicy":
                case "RedriveAllowPolicy":
                case "KmsMasterKeyId":
                case "KmsDataKeyReusePeriodSeconds":
                case "SqsManagedSseEnabled":
                case "DeduplicationScope":
                case "FifoThroughputLimit":
                    // Slice-5 territory — accept silently for now so callers
                    // can include them in a single CreateQueue call without
                    // erroring; the gap doc records the divergence.
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
        };
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

    /// <summary>
    /// True when the queue's SB shape (RequiresSession=true) corresponds to
    /// what the SQS module would model as a FIFO queue.
    /// </summary>
    public bool IsFifoCandidate => RequiresSession == true;
}
