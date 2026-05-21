namespace Aws2Azure.Amqp.Framing;

/// <summary>
/// Symbol constants for AMQP 1.0 spec-defined error conditions
/// ("AMQP 1.0 Transport" §2.8.15 / §2.8.16 / §2.8.17 / §2.8.18) and
/// Service Bus / EventHub vendor-defined conditions
/// (<c>com.microsoft:*</c>). Every <see cref="AmqpError.Condition"/>
/// on the wire is one of these symbols (case-sensitive) — exposing
/// them as constants makes the classifier in
/// <see cref="AmqpErrorClassifier"/> a closed switch and lets call
/// sites avoid string typos.
/// </summary>
internal static class AmqpErrorCondition
{
    // --- amqp:* (§2.8.15 generic) -------------------------------------
    public const string InternalError = "amqp:internal-error";
    public const string NotFound = "amqp:not-found";
    public const string UnauthorizedAccess = "amqp:unauthorized-access";
    public const string DecodeError = "amqp:decode-error";
    public const string ResourceLimitExceeded = "amqp:resource-limit-exceeded";
    public const string NotAllowed = "amqp:not-allowed";
    public const string InvalidField = "amqp:invalid-field";
    public const string NotImplemented = "amqp:not-implemented";
    public const string ResourceLocked = "amqp:resource-locked";
    public const string PreconditionFailed = "amqp:precondition-failed";
    public const string ResourceDeleted = "amqp:resource-deleted";
    public const string IllegalState = "amqp:illegal-state";
    public const string FrameSizeTooSmall = "amqp:frame-size-too-small";

    // --- amqp:connection:* (§2.8.16) ----------------------------------
    public const string ConnectionForced = "amqp:connection:forced";
    public const string ConnectionFramingError = "amqp:connection:framing-error";
    public const string ConnectionRedirect = "amqp:connection:redirect";

    // --- amqp:session:* (§2.8.17) -------------------------------------
    public const string SessionWindowViolation = "amqp:session:window-violation";
    public const string SessionErrantLink = "amqp:session:errant-link";
    public const string SessionHandleInUse = "amqp:session:handle-in-use";
    public const string SessionUnattachedHandle = "amqp:session:unattached-handle";

    // --- amqp:link:* (§2.8.18) ----------------------------------------
    public const string LinkDetachForced = "amqp:link:detach-forced";
    public const string LinkTransferLimitExceeded = "amqp:link:transfer-limit-exceeded";
    public const string LinkMessageSizeExceeded = "amqp:link:message-size-exceeded";
    public const string LinkRedirect = "amqp:link:redirect";
    public const string LinkStolen = "amqp:link:stolen";

    // --- com.microsoft:* (Service Bus / Event Hubs vendor conditions) -
    /// <summary>Server is throttling: retry after the suggested back-off.</summary>
    public const string ServerBusy = "com.microsoft:server-busy";
    /// <summary>Request argument was malformed; do not retry.</summary>
    public const string ArgumentError = "com.microsoft:argument-error";
    /// <summary>Argument out of valid range; do not retry.</summary>
    public const string ArgumentOutOfRange = "com.microsoft:argument-out-of-range";
    /// <summary>Operation timed out at the server; safe to retry.</summary>
    public const string Timeout = "com.microsoft:timeout";
    /// <summary>Message lock was lost (session/lock expired); reacquire and re-receive.</summary>
    public const string MessageLockLost = "com.microsoft:message-lock-lost";
    /// <summary>Session lock was lost; reacquire the session.</summary>
    public const string SessionLockLost = "com.microsoft:session-lock-lost";
    /// <summary>Session cannot be locked; another consumer holds it.</summary>
    public const string SessionCannotBeLocked = "com.microsoft:session-cannot-be-locked";
    /// <summary>Entity was disabled by the namespace owner; do not retry.</summary>
    public const string EntityDisabled = "com.microsoft:entity-disabled";
    /// <summary>Publisher was revoked; do not retry with the same credentials.</summary>
    public const string PublisherRevoked = "com.microsoft:publisher-revoked";
    /// <summary>Partition is not owned by this consumer; refresh leases.</summary>
    public const string PartitionNotOwned = "com.microsoft:partition-not-owned";
    /// <summary>Store lock was lost (Event Hubs checkpoint store).</summary>
    public const string StoreLockLost = "com.microsoft:store-lock-lost";
    /// <summary>SAS token expired; renew via CBS.</summary>
    public const string TokenExpired = "com.microsoft:auth:expired";
}

/// <summary>
/// UTF-8 (ASCII) byte-literal companions to <see cref="AmqpErrorCondition"/>.
/// Symbol values on the AMQP wire are ASCII bytes; comparing the
/// wire-bytes directly against these <c>"..."u8</c> literals avoids
/// allocating a <see cref="string"/> per error frame on the hot path
/// (see <see cref="AmqpErrorClassifier.Classify(ReadOnlySpan{byte})"/>).
/// The two registries are kept in lock-step manually because
/// <c>"..."u8</c> literals are not valid <c>const</c> targets — a
/// guard test asserts the two sides match byte-for-byte.
/// </summary>
internal static class AmqpErrorConditionU8
{
    // --- amqp:* (§2.8.15 generic) -------------------------------------
    public static ReadOnlySpan<byte> InternalError => "amqp:internal-error"u8;
    public static ReadOnlySpan<byte> NotFound => "amqp:not-found"u8;
    public static ReadOnlySpan<byte> UnauthorizedAccess => "amqp:unauthorized-access"u8;
    public static ReadOnlySpan<byte> DecodeError => "amqp:decode-error"u8;
    public static ReadOnlySpan<byte> ResourceLimitExceeded => "amqp:resource-limit-exceeded"u8;
    public static ReadOnlySpan<byte> NotAllowed => "amqp:not-allowed"u8;
    public static ReadOnlySpan<byte> InvalidField => "amqp:invalid-field"u8;
    public static ReadOnlySpan<byte> NotImplemented => "amqp:not-implemented"u8;
    public static ReadOnlySpan<byte> ResourceLocked => "amqp:resource-locked"u8;
    public static ReadOnlySpan<byte> PreconditionFailed => "amqp:precondition-failed"u8;
    public static ReadOnlySpan<byte> ResourceDeleted => "amqp:resource-deleted"u8;
    public static ReadOnlySpan<byte> IllegalState => "amqp:illegal-state"u8;
    public static ReadOnlySpan<byte> FrameSizeTooSmall => "amqp:frame-size-too-small"u8;

    // --- amqp:connection:* (§2.8.16) ----------------------------------
    public static ReadOnlySpan<byte> ConnectionForced => "amqp:connection:forced"u8;
    public static ReadOnlySpan<byte> ConnectionFramingError => "amqp:connection:framing-error"u8;
    public static ReadOnlySpan<byte> ConnectionRedirect => "amqp:connection:redirect"u8;

    // --- amqp:session:* (§2.8.17) -------------------------------------
    public static ReadOnlySpan<byte> SessionWindowViolation => "amqp:session:window-violation"u8;
    public static ReadOnlySpan<byte> SessionErrantLink => "amqp:session:errant-link"u8;
    public static ReadOnlySpan<byte> SessionHandleInUse => "amqp:session:handle-in-use"u8;
    public static ReadOnlySpan<byte> SessionUnattachedHandle => "amqp:session:unattached-handle"u8;

    // --- amqp:link:* (§2.8.18) ----------------------------------------
    public static ReadOnlySpan<byte> LinkDetachForced => "amqp:link:detach-forced"u8;
    public static ReadOnlySpan<byte> LinkTransferLimitExceeded => "amqp:link:transfer-limit-exceeded"u8;
    public static ReadOnlySpan<byte> LinkMessageSizeExceeded => "amqp:link:message-size-exceeded"u8;
    public static ReadOnlySpan<byte> LinkRedirect => "amqp:link:redirect"u8;
    public static ReadOnlySpan<byte> LinkStolen => "amqp:link:stolen"u8;

    // --- com.microsoft:* (Service Bus / Event Hubs vendor) ------------
    public static ReadOnlySpan<byte> ServerBusy => "com.microsoft:server-busy"u8;
    public static ReadOnlySpan<byte> ArgumentError => "com.microsoft:argument-error"u8;
    public static ReadOnlySpan<byte> ArgumentOutOfRange => "com.microsoft:argument-out-of-range"u8;
    public static ReadOnlySpan<byte> Timeout => "com.microsoft:timeout"u8;
    public static ReadOnlySpan<byte> MessageLockLost => "com.microsoft:message-lock-lost"u8;
    public static ReadOnlySpan<byte> SessionLockLost => "com.microsoft:session-lock-lost"u8;
    public static ReadOnlySpan<byte> SessionCannotBeLocked => "com.microsoft:session-cannot-be-locked"u8;
    public static ReadOnlySpan<byte> EntityDisabled => "com.microsoft:entity-disabled"u8;
    public static ReadOnlySpan<byte> PublisherRevoked => "com.microsoft:publisher-revoked"u8;
    public static ReadOnlySpan<byte> PartitionNotOwned => "com.microsoft:partition-not-owned"u8;
    public static ReadOnlySpan<byte> StoreLockLost => "com.microsoft:store-lock-lost"u8;
    public static ReadOnlySpan<byte> TokenExpired => "com.microsoft:auth:expired"u8;
}

/// <summary>
/// Classification of an <see cref="AmqpError"/> for retry / failover
/// decisions. The mapping lives in <see cref="AmqpErrorClassifier"/>;
/// callers use it to choose between immediate retry, back-off,
/// reauthentication, link reattach, or surfacing the error to the
/// upstream caller.
/// </summary>
internal enum AmqpErrorKind
{
    /// <summary>Unrecognised condition; treat as fatal until reviewed.</summary>
    Unknown = 0,
    /// <summary>Transient — safe to retry after a short delay.</summary>
    Transient,
    /// <summary>Throttled — back off using the server-suggested delay where present.</summary>
    Throttled,
    /// <summary>Authentication / authorisation failure — re-acquire credentials.</summary>
    Auth,
    /// <summary>Lock lost — re-receive / re-acquire the session before retrying.</summary>
    LockLost,
    /// <summary>Fatal client-side error (bad input, not allowed); do not retry.</summary>
    ClientFatal,
    /// <summary>Fatal server-side error; do not retry.</summary>
    ServerFatal,
    /// <summary>Server requested a redirect (connection or link); follow the address in the error info.</summary>
    Redirect,
}

/// <summary>
/// Maps an <see cref="AmqpError.Condition"/> to an
/// <see cref="AmqpErrorKind"/>. The mapping covers every spec-defined
/// condition in <see cref="AmqpErrorCondition"/> plus the Service Bus
/// vendor conditions we care about. Unknown conditions fall through to
/// <see cref="AmqpErrorKind.Unknown"/> — callers should log and treat
/// as fatal pending review (this preserves the closed-registry rule
/// from ADR-0002).
/// </summary>
internal static class AmqpErrorClassifier
{
    public static AmqpErrorKind Classify(string? condition) => condition switch
    {
        // Transient (retry after backoff).
        AmqpErrorCondition.InternalError => AmqpErrorKind.Transient,
        AmqpErrorCondition.ConnectionForced => AmqpErrorKind.Transient,
        AmqpErrorCondition.LinkDetachForced => AmqpErrorKind.Transient,
        AmqpErrorCondition.LinkStolen => AmqpErrorKind.Transient,
        AmqpErrorCondition.Timeout => AmqpErrorKind.Transient,

        // Throttled (honour Retry-After-like hint in error.info).
        AmqpErrorCondition.ResourceLimitExceeded => AmqpErrorKind.Throttled,
        AmqpErrorCondition.LinkTransferLimitExceeded => AmqpErrorKind.Throttled,
        AmqpErrorCondition.ServerBusy => AmqpErrorKind.Throttled,

        // Authentication / authorisation.
        AmqpErrorCondition.UnauthorizedAccess => AmqpErrorKind.Auth,
        AmqpErrorCondition.TokenExpired => AmqpErrorKind.Auth,
        AmqpErrorCondition.PublisherRevoked => AmqpErrorKind.Auth,

        // Lock lost — reacquire and re-receive.
        AmqpErrorCondition.MessageLockLost => AmqpErrorKind.LockLost,
        AmqpErrorCondition.SessionLockLost => AmqpErrorKind.LockLost,
        AmqpErrorCondition.SessionCannotBeLocked => AmqpErrorKind.LockLost,
        AmqpErrorCondition.StoreLockLost => AmqpErrorKind.LockLost,
        AmqpErrorCondition.PartitionNotOwned => AmqpErrorKind.LockLost,

        // Redirect — follow address in info map.
        AmqpErrorCondition.ConnectionRedirect => AmqpErrorKind.Redirect,
        AmqpErrorCondition.LinkRedirect => AmqpErrorKind.Redirect,

        // Client-side fatal.
        AmqpErrorCondition.DecodeError => AmqpErrorKind.ClientFatal,
        AmqpErrorCondition.NotAllowed => AmqpErrorKind.ClientFatal,
        AmqpErrorCondition.InvalidField => AmqpErrorKind.ClientFatal,
        AmqpErrorCondition.PreconditionFailed => AmqpErrorKind.ClientFatal,
        AmqpErrorCondition.LinkMessageSizeExceeded => AmqpErrorKind.ClientFatal,
        AmqpErrorCondition.FrameSizeTooSmall => AmqpErrorKind.ClientFatal,
        AmqpErrorCondition.ArgumentError => AmqpErrorKind.ClientFatal,
        AmqpErrorCondition.ArgumentOutOfRange => AmqpErrorKind.ClientFatal,

        // Server-side fatal.
        AmqpErrorCondition.NotFound => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.NotImplemented => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.ResourceLocked => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.ResourceDeleted => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.IllegalState => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.ConnectionFramingError => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.SessionWindowViolation => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.SessionErrantLink => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.SessionHandleInUse => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.SessionUnattachedHandle => AmqpErrorKind.ServerFatal,
        AmqpErrorCondition.EntityDisabled => AmqpErrorKind.ServerFatal,

        _ => AmqpErrorKind.Unknown,
    };

    /// <summary>
    /// Alloc-free overload: classifies an AMQP condition symbol directly
    /// from its on-wire ASCII bytes, without round-tripping through a
    /// <see cref="string"/>. Equivalent to
    /// <see cref="Classify(string?)"/> for every condition listed in
    /// <see cref="AmqpErrorConditionU8"/>; unknown bytes return
    /// <see cref="AmqpErrorKind.Unknown"/>. Pass an empty span for null.
    /// </summary>
    public static AmqpErrorKind Classify(ReadOnlySpan<byte> condition)
    {
        if (condition.IsEmpty) return AmqpErrorKind.Unknown;

        // Length-bucket dispatch: most condition symbols share a length
        // with at most a handful of siblings, so we collapse the cascade
        // into a single switch on Length followed by SequenceEqual.
        switch (condition.Length)
        {
            case 14:
                if (condition.SequenceEqual(AmqpErrorConditionU8.NotFound)) return AmqpErrorKind.ServerFatal;
                break;
            case 16:
                if (condition.SequenceEqual(AmqpErrorConditionU8.NotAllowed)) return AmqpErrorKind.ClientFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.LinkStolen)) return AmqpErrorKind.Transient;
                break;
            case 17:
                if (condition.SequenceEqual(AmqpErrorConditionU8.DecodeError)) return AmqpErrorKind.ClientFatal;
                break;
            case 18:
                if (condition.SequenceEqual(AmqpErrorConditionU8.InvalidField)) return AmqpErrorKind.ClientFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.IllegalState)) return AmqpErrorKind.ServerFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.LinkRedirect)) return AmqpErrorKind.Redirect;
                break;
            case 19:
                if (condition.SequenceEqual(AmqpErrorConditionU8.InternalError)) return AmqpErrorKind.Transient;
                break;
            case 20:
                if (condition.SequenceEqual(AmqpErrorConditionU8.NotImplemented)) return AmqpErrorKind.ServerFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.ResourceLocked)) return AmqpErrorKind.ServerFatal;
                break;
            case 21:
                if (condition.SequenceEqual(AmqpErrorConditionU8.ResourceDeleted)) return AmqpErrorKind.ServerFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.Timeout)) return AmqpErrorKind.Transient;
                break;
            case 22:
                if (condition.SequenceEqual(AmqpErrorConditionU8.ConnectionForced)) return AmqpErrorKind.Transient;
                break;
            case 23:
                if (condition.SequenceEqual(AmqpErrorConditionU8.LinkDetachForced)) return AmqpErrorKind.Transient;
                break;
            case 24:
                if (condition.SequenceEqual(AmqpErrorConditionU8.UnauthorizedAccess)) return AmqpErrorKind.Auth;
                if (condition.SequenceEqual(AmqpErrorConditionU8.PreconditionFailed)) return AmqpErrorKind.ClientFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.ConnectionRedirect)) return AmqpErrorKind.Redirect;
                if (condition.SequenceEqual(AmqpErrorConditionU8.SessionErrantLink)) return AmqpErrorKind.ServerFatal;
                break;
            case 25:
                if (condition.SequenceEqual(AmqpErrorConditionU8.FrameSizeTooSmall)) return AmqpErrorKind.ClientFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.ServerBusy)) return AmqpErrorKind.Throttled;
                break;
            case 26:
                if (condition.SequenceEqual(AmqpErrorConditionU8.SessionHandleInUse)) return AmqpErrorKind.ServerFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.TokenExpired)) return AmqpErrorKind.Auth;
                break;
            case 28:
                if (condition.SequenceEqual(AmqpErrorConditionU8.ResourceLimitExceeded)) return AmqpErrorKind.Throttled;
                if (condition.SequenceEqual(AmqpErrorConditionU8.ArgumentError)) return AmqpErrorKind.ClientFatal;
                break;
            case 29:
                if (condition.SequenceEqual(AmqpErrorConditionU8.ConnectionFramingError)) return AmqpErrorKind.ServerFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.SessionWindowViolation)) return AmqpErrorKind.ServerFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.EntityDisabled)) return AmqpErrorKind.ServerFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.StoreLockLost)) return AmqpErrorKind.LockLost;
                break;
            case 30:
                if (condition.SequenceEqual(AmqpErrorConditionU8.SessionUnattachedHandle)) return AmqpErrorKind.ServerFatal;
                break;
            case 31:
                if (condition.SequenceEqual(AmqpErrorConditionU8.LinkMessageSizeExceeded)) return AmqpErrorKind.ClientFatal;
                if (condition.SequenceEqual(AmqpErrorConditionU8.MessageLockLost)) return AmqpErrorKind.LockLost;
                if (condition.SequenceEqual(AmqpErrorConditionU8.SessionLockLost)) return AmqpErrorKind.LockLost;
                if (condition.SequenceEqual(AmqpErrorConditionU8.PublisherRevoked)) return AmqpErrorKind.Auth;
                break;
            case 33:
                if (condition.SequenceEqual(AmqpErrorConditionU8.LinkTransferLimitExceeded)) return AmqpErrorKind.Throttled;
                if (condition.SequenceEqual(AmqpErrorConditionU8.PartitionNotOwned)) return AmqpErrorKind.LockLost;
                break;
            case 35:
                if (condition.SequenceEqual(AmqpErrorConditionU8.ArgumentOutOfRange)) return AmqpErrorKind.ClientFatal;
                break;
            case 38:
                if (condition.SequenceEqual(AmqpErrorConditionU8.SessionCannotBeLocked)) return AmqpErrorKind.LockLost;
                break;
        }
        return AmqpErrorKind.Unknown;
    }
}
