using System;
using System.Globalization;
using System.Text.Json;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Translates a DynamoDB TTL attribute (an absolute epoch-seconds timestamp on a
/// named item attribute) into the Cosmos per-item <c>ttl</c> field, which is a
/// duration in seconds relative to the document's last-write <c>_ts</c>. The
/// proxy recomputes this on every write, so a relative ttl pinned at write time
/// still expires the item at the correct absolute instant (the container's
/// <c>defaultTtl = -1</c> arms per-item ttl without imposing a default expiry).
/// </summary>
internal static class TtlTranslation
{
    // DynamoDB does not delete items whose TTL timestamp is more than five years
    // in the past, as a guard against accidental mass-deletion from a bad clock
    // or garbage value. Mirror that so a stale epoch can't silently purge data.
    private const long FiveYearsSeconds = 5L * 365 * 24 * 60 * 60;

    /// <summary>
    /// Computes the Cosmos per-item <c>ttl</c> (whole seconds from the item's
    /// last write) for a DynamoDB item map, or <c>null</c> when no ttl should be
    /// written. Returns null — leaving the item non-expiring, matching DynamoDB —
    /// when TTL is disabled, the configured attribute is absent, the attribute is
    /// not a Number, or the value is more than five years in the past. A past-due
    /// (but within five years) timestamp maps to <c>1</c> so Cosmos expires the
    /// item promptly (Cosmos rejects a ttl of 0 or any negative value except -1).
    /// </summary>
    /// <param name="item">The DynamoDB item as its attribute map, each value a
    /// type-tagged AttributeValue (e.g. <c>{"expiresAt":{"N":"1700000000"}}</c>).</param>
    /// <param name="config">The table's persisted TTL configuration, or null.</param>
    /// <param name="nowEpochSeconds">Current time as Unix epoch seconds.</param>
    public static int? ComputeItemTtlSeconds(JsonElement item, TableTimeToLive? config, long nowEpochSeconds)
    {
        if (config is null || !config.Enabled)
        {
            return null;
        }

        var attributeName = config.AttributeName;
        if (string.IsNullOrEmpty(attributeName) || item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!item.TryGetProperty(attributeName, out var attrValue) || attrValue.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The TTL attribute must be a DynamoDB Number ({"N":"<epoch-seconds>"}).
        // Any other type (S, B, …) means the attribute is not an expiry marker,
        // so the item is left non-expiring — DynamoDB ignores non-Number values.
        if (!attrValue.TryGetProperty("N", out var numberToken) || numberToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = numberToken.GetString();
        if (string.IsNullOrEmpty(text) || !TryParseEpochSeconds(text, out var epochSeconds))
        {
            return null;
        }

        // Classify before subtracting so an extreme epoch (e.g. long.MinValue)
        // can't overflow `epochSeconds - nowEpochSeconds` into a spurious
        // positive delta. `nowEpochSeconds` is real wall-clock time (~1.7e9), so
        // `nowEpochSeconds - FiveYearsSeconds` cannot underflow.
        if (epochSeconds <= nowEpochSeconds)
        {
            if (epochSeconds < nowEpochSeconds - FiveYearsSeconds)
            {
                return null;
            }

            // Past-due (within five years) → expire promptly (Cosmos rejects a
            // ttl of 0 or any negative value except -1).
            return 1;
        }

        long delta = epochSeconds - nowEpochSeconds;
        if (delta > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)delta;
    }

    private static bool TryParseEpochSeconds(string text, out long epochSeconds)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out epochSeconds))
        {
            return true;
        }

        // DynamoDB stores TTL as a Number; tolerate a fractional epoch by
        // flooring to whole seconds (Cosmos ttl granularity is one second).
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && !double.IsNaN(value) && !double.IsInfinity(value))
        {
            var floored = Math.Floor(value);
            if (floored is >= long.MinValue and <= long.MaxValue)
            {
                epochSeconds = (long)floored;
                return true;
            }
        }

        epochSeconds = 0;
        return false;
    }
}
