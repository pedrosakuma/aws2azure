using System;
using System.Text.Json;
using Aws2Azure.Core.Configuration;

namespace Aws2Azure.Modules.DynamoDb.Internal;

/// <summary>
/// Cosmos DB account default consistency levels, ordered weakest-last as
/// Cosmos documents them. The proxy only needs to know whether a level is
/// strong enough to honor DynamoDB <c>ConsistentRead</c> / read-your-write.
/// </summary>
internal enum CosmosConsistencyLevel
{
    /// <summary>Could not be determined from the account read.</summary>
    Unknown = 0,
    Strong,
    BoundedStaleness,
    Session,
    ConsistentPrefix,
    Eventual,
}

/// <summary>
/// Pure helpers for the #204 consistency probe: parse the
/// <c>defaultConsistencyLevel</c> out of a Cosmos DatabaseAccount read body
/// and decide whether a level can honor DynamoDB <c>ConsistentRead</c>.
///
/// <para>Cosmos only ever <em>relaxes</em> consistency per request, never
/// strengthens it (see the REST contract for <c>x-ms-consistency-level</c>).
/// So the proxy's <c>x-ms-consistency-level: Strong</c> header on a
/// <c>ConsistentRead</c> is silently ignored unless the account's own default
/// is already Strong or Bounded Staleness — on Session / Consistent Prefix /
/// Eventual accounts <c>ConsistentRead</c> and read-your-write are not
/// honored.</para>
/// </summary>
internal static class CosmosConsistency
{
    /// <summary>
    /// Extracts <c>userConsistencyPolicy.defaultConsistencyLevel</c> from a
    /// Cosmos DatabaseAccount read body. Returns <see cref="CosmosConsistencyLevel.Unknown"/>
    /// when the body is not an object, the property is absent, or its value is
    /// unrecognised. Runs once at startup, so a DOM parse is acceptable.
    /// </summary>
    public static CosmosConsistencyLevel ParseDefaultConsistency(ReadOnlySpan<byte> accountJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(accountJson.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return CosmosConsistencyLevel.Unknown;
            if (!doc.RootElement.TryGetProperty("userConsistencyPolicy", out var policy)
                || policy.ValueKind != JsonValueKind.Object)
                return CosmosConsistencyLevel.Unknown;
            if (!policy.TryGetProperty("defaultConsistencyLevel", out var level)
                || level.ValueKind != JsonValueKind.String)
                return CosmosConsistencyLevel.Unknown;
            return FromName(level.GetString());
        }
        catch (JsonException)
        {
            return CosmosConsistencyLevel.Unknown;
        }
    }

    /// <summary>Maps a Cosmos consistency-level name (case-insensitive) to the
    /// enum. Unrecognised / null names map to <see cref="CosmosConsistencyLevel.Unknown"/>.</summary>
    public static CosmosConsistencyLevel FromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return CosmosConsistencyLevel.Unknown;
        if (name.Equals("Strong", StringComparison.OrdinalIgnoreCase)) return CosmosConsistencyLevel.Strong;
        if (name.Equals("BoundedStaleness", StringComparison.OrdinalIgnoreCase)) return CosmosConsistencyLevel.BoundedStaleness;
        if (name.Equals("Session", StringComparison.OrdinalIgnoreCase)) return CosmosConsistencyLevel.Session;
        if (name.Equals("ConsistentPrefix", StringComparison.OrdinalIgnoreCase)) return CosmosConsistencyLevel.ConsistentPrefix;
        if (name.Equals("Eventual", StringComparison.OrdinalIgnoreCase)) return CosmosConsistencyLevel.Eventual;
        return CosmosConsistencyLevel.Unknown;
    }

    /// <summary>
    /// True when an account at <paramref name="level"/> can honor DynamoDB
    /// <c>ConsistentRead</c> (i.e. a per-request Strong request is not
    /// downgraded). Only Strong and Bounded Staleness qualify.
    /// </summary>
    public static bool CanHonorConsistentRead(CosmosConsistencyLevel level)
        => level is CosmosConsistencyLevel.Strong or CosmosConsistencyLevel.BoundedStaleness;

    /// <summary>The action the startup probe should take for one account.</summary>
    internal enum ProbeOutcome
    {
        /// <summary>Account can honor ConsistentRead — nothing to do.</summary>
        Ok,
        /// <summary>Below Strong/Bounded — log a warning and continue.</summary>
        Warn,
        /// <summary>Below Strong/Bounded under Required mode — fail startup.</summary>
        Fail,
    }

    /// <summary>
    /// Pure decision for a successfully-determined level under a check mode.
    /// <see cref="CosmosConsistencyLevel.Unknown"/> is treated as "cannot
    /// confirm" and follows the same warn/fail path as a below-threshold level
    /// (callers may message it differently). <see cref="ConsistencyCheckMode.Disabled"/>
    /// always yields <see cref="ProbeOutcome.Ok"/>.
    /// </summary>
    public static ProbeOutcome Decide(CosmosConsistencyLevel level, ConsistencyCheckMode mode)
    {
        if (mode == ConsistencyCheckMode.Disabled) return ProbeOutcome.Ok;
        if (CanHonorConsistentRead(level)) return ProbeOutcome.Ok;
        return mode == ConsistencyCheckMode.Required ? ProbeOutcome.Fail : ProbeOutcome.Warn;
    }
}
