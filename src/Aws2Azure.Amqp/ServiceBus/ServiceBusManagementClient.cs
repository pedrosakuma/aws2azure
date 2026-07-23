using System.Buffers;
using Aws2Azure.Amqp.Codec;
using Aws2Azure.Amqp.Connection;
using Aws2Azure.Amqp.Framing;
using Aws2Azure.Amqp.Management;

namespace Aws2Azure.Amqp.ServiceBus;

/// <summary>
/// Service Bus <c>$management</c> client: thin wrapper around
/// <see cref="AmqpRequestResponseLink"/> that knows how to compose
/// management requests (application properties carry the
/// <c>operation</c> name + optional server-timeout, the body is an
/// AMQP <c>value</c> map of operation arguments) and decode the
/// matching response (status-code in application properties, body
/// is an AMQP <c>value</c> map of operation results).
/// </summary>
/// <remarks>
/// <para>
/// Service Bus exposes an entity-scoped management node at
/// <c>{entity-path}/$management</c>. This client implements the renew-lock /
/// renew-session-lock operations; further ops (peek, scheduled-message
/// cancel, session state, …) plug in the same way.
/// </para>
/// <para>
/// This client is single-use per (connection, audience) pair —
/// disposing it tears the underlying links down. The
/// <see cref="ServiceBusAmqpPool"/> caches one per connection.
/// </para>
/// </remarks>
internal sealed class ServiceBusManagementClient : IAsyncDisposable
{
    internal const string RenewLockOperation = "com.microsoft:renew-lock";
    internal const string RenewSessionLockOperation = "com.microsoft:renew-session-lock";

    private static readonly TimeSpan DefaultServerTimeout = TimeSpan.FromSeconds(60);

    private readonly AmqpRequestResponseLink _link;
    private int _disposed;

    private ServiceBusManagementClient(AmqpRequestResponseLink link)
    {
        _link = link;
    }

    /// <summary>
    /// True when the underlying request/response link has detached
    /// (peer-initiated or otherwise faulted). Pool slots check this
    /// to evict a stale client before reuse.
    /// </summary>
    public bool IsClosed => _link.IsClosed;

    /// <summary>
    /// Opens a paired sender/receiver against the <c>$management</c>
    /// node on <paramref name="session"/>. The caller must have
    /// CBS-authorised the entity audience already (the broker
    /// rejects management requests on unauthorised links).
    /// </summary>
    public static async Task<ServiceBusManagementClient> OpenAsync(
        AmqpSession session,
        string managementAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(managementAddress);
        var link = new AmqpRequestResponseLink(session, new AmqpRequestResponseLinkSettings
        {
            Address = managementAddress,
            ReplyToAddress = $"aws2azure-mgmt-reply-{Guid.NewGuid():N}",
        });
        try
        {
            await link.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new ServiceBusManagementClient(link);
        }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Renews the lock on <paramref name="lockToken"/>. Returns the
    /// new lock expiry as advertised by the broker. The renewal
    /// duration is always the queue's configured <c>LockDuration</c>
    /// — Service Bus does not accept an explicit duration the way
    /// SQS's <c>ChangeMessageVisibility</c> does.
    /// </summary>
    /// <exception cref="ServiceBusManagementException">
    /// Thrown when the broker reports a non-2xx status code. The
    /// exception carries the broker status code + description +
    /// AMQP error condition (when present).
    /// </exception>
    public async Task<DateTimeOffset> RenewLockAsync(
        Guid lockToken, CancellationToken cancellationToken = default)
        => (await RenewLocksAsync(new[] { lockToken }, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>
    /// Batch variant: renews up to N lock tokens in one round-trip and
    /// returns the new expiries in the same order. Service Bus
    /// guarantees a 1:1 positional mapping between request and
    /// response arrays.
    /// </summary>
    public async Task<DateTimeOffset[]> RenewLocksAsync(
        IReadOnlyList<Guid> lockTokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockTokens);
        if (lockTokens.Count == 0)
            throw new ArgumentException("At least one lock token is required.", nameof(lockTokens));

        using var body = EncodeRenewLockRequest(lockTokens);
        var request = new AmqpMessage
        {
            ApplicationProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["operation"] = RenewLockOperation,
                ["com.microsoft:server-timeout"] = (uint)DefaultServerTimeout.TotalMilliseconds,
                ["com.microsoft:tracking-id"] = Guid.NewGuid().ToString(),
            },
            BodyValueBytes = body.Memory,
        };

        var response = await _link.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        ThrowIfNotSuccess(response, RenewLockOperation);
        return DecodeExpirations(response, expected: lockTokens.Count);
    }

    /// <summary>
    /// Session-flavoured renew-lock: renews the lock that the broker
    /// holds on <paramref name="sessionId"/> and returns the new
    /// session-lock expiry. Used by the FIFO
    /// <c>ChangeMessageVisibility(VisibilityTimeout &gt; 0)</c> path
    /// where the receipt handle is keyed by session-id (slice 7c.4).
    /// </summary>
    /// <remarks>
    /// Service Bus exposes this as the <c>com.microsoft:renew-session-lock</c>
    /// operation; the request body carries a single
    /// <c>session-id</c> field and the response carries a single
    /// <c>expiration</c> timestamp (singular, not an array — session
    /// locks are not batched). The granted duration is always the
    /// queue's configured <c>LockDuration</c> — the broker does not
    /// accept a caller-supplied duration.
    /// </remarks>
    /// <exception cref="ServiceBusManagementException">
    /// Thrown when the broker reports a non-2xx status code.
    /// </exception>
    public async Task<DateTimeOffset> RenewSessionLockAsync(
        string sessionId,
        string associatedLinkName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(associatedLinkName);

        using var body = EncodeRenewSessionLockRequest(sessionId);
        var request = new AmqpMessage
        {
            ApplicationProperties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["operation"] = RenewSessionLockOperation,
                ["com.microsoft:server-timeout"] = (uint)DefaultServerTimeout.TotalMilliseconds,
                ["com.microsoft:tracking-id"] = Guid.NewGuid().ToString(),
                ["associated-link-name"] = associatedLinkName,
            },
            BodyValueBytes = body.Memory,
        };

        var response = await _link.SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        ThrowIfNotSuccess(response, RenewSessionLockOperation);
        return DecodeSingleExpiration(response, RenewSessionLockOperation);
    }

    // --- Request encoding -------------------------------------------------

    private static PooledPayload EncodeRenewLockRequest(IReadOnlyList<Guid> lockTokens)
    {
        if (lockTokens.Count > 128)
            throw new ArgumentException("Batch too large (max 128 lock tokens per RenewLock).", nameof(lockTokens));

        // body = AmqpValue map { "lock-tokens" → array<Uuid> }.
        // The AmqpValue described-constructor header is added by
        // AmqpMessage.Write — here we only emit the value bytes
        // (which is the map itself).
        // Worst-case size: map32 header (9) + symbol32 key (5 + 11) +
        // array32 header (9 + 1 ctor) + 128 × 16 UUID bytes.
        var rented = ArrayPool<byte>.Shared.Rent(64 + 16 * lockTokens.Count);
        try
        {
            // Build the array of UUIDs (single-byte element constructor).
            // 128 × 16 = 2 KiB is safe on the stack.
            Span<byte> arrayElements = stackalloc byte[16 * 128];
            int aw = 0;
            Span<byte> uuidScratch = stackalloc byte[17];
            for (int i = 0; i < lockTokens.Count; i++)
            {
                AmqpPrimitiveWriter.WriteUuid(uuidScratch, lockTokens[i], out _);
                uuidScratch.Slice(1, 16).CopyTo(arrayElements[aw..]);
                aw += 16;
            }
            // Array header is at most ~10 bytes + 16 × count for payload.
            Span<byte> arrayBytes = stackalloc byte[16 + 16 * 128];
            AmqpCompoundWriter.WriteArray(
                arrayBytes,
                elementConstructor: stackalloc byte[] { AmqpFormatCode.Uuid },
                elementData: arrayElements[..aw],
                count: lockTokens.Count,
                out var arrayLen);

            // Build the map pair: key "lock-tokens" (string) → array<Uuid>.
            // The official SDK's MapKey wraps a string, so the key must stay
            // string-typed on the wire.
            // pairBytes must hold the full key+array — size from the
            // measured arrayLen rather than a fixed cap to avoid an
            // overflow at large batch sizes.
            var pairBuf = ArrayPool<byte>.Shared.Rent(32 + arrayLen);
            try
            {
                Span<byte> pairBytes = pairBuf;
                AmqpVariableWriter.WriteString(pairBytes, "lock-tokens", out var keyLen);
                arrayBytes[..arrayLen].CopyTo(pairBytes[keyLen..]);
                var pairLen = keyLen + arrayLen;

                // Wrap the pair as a map (pair count = 1, element count = 2).
                AmqpCompoundWriter.WriteMap(rented, pairBytes[..pairLen], pairCount: 1, out var mapLen);
                return new PooledPayload(rented, mapLen);
            }
            finally { ArrayPool<byte>.Shared.Return(pairBuf); }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    private static PooledPayload EncodeRenewSessionLockRequest(string sessionId)
    {
        // body = AmqpValue map { "session-id" → string }.
        // The official SDK's MapKey wraps the string "session-id", so both
        // key and value are UTF-8 strings. The wire size is modest
        // (16 + sessionId length × 4 worst case) so a single rented
        // page is plenty.
        var byteLen = System.Text.Encoding.UTF8.GetMaxByteCount(sessionId.Length);
        var rented = ArrayPool<byte>.Shared.Rent(64 + byteLen);
        try
        {
            Span<byte> pairBuf = stackalloc byte[256];
            byte[]? rentedPair = null;
            Span<byte> pair = pairBuf;
            if (16 + byteLen > pair.Length)
            {
                rentedPair = ArrayPool<byte>.Shared.Rent(32 + byteLen);
                pair = rentedPair;
            }
            try
            {
                AmqpVariableWriter.WriteString(pair, "session-id", out var keyLen);
                AmqpVariableWriter.WriteString(pair[keyLen..], sessionId, out var valLen);
                var pairLen = keyLen + valLen;

                AmqpCompoundWriter.WriteMap(rented, pair[..pairLen], pairCount: 1, out var mapLen);
                return new PooledPayload(rented, mapLen);
            }
            finally
            {
                if (rentedPair is not null)
                    ArrayPool<byte>.Shared.Return(rentedPair);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    // --- Response decoding ------------------------------------------------

    private static void ThrowIfNotSuccess(AmqpMessage response, string operation)
    {
        var props = response.ApplicationProperties;
        var statusCode = props is not null && props.TryGetValue("statusCode", out var sc) && sc is int i ? i : 0;
        if (statusCode >= 200 && statusCode <= 299)
            return;

        string? description = null;
        if (props is not null && props.TryGetValue("statusDescription", out var sd) && sd is string sds)
            description = sds;
        string? errorCondition = null;
        if (props is not null && props.TryGetValue("errorCondition", out var ec) && ec is string ecs)
            errorCondition = ecs;

        throw new ServiceBusManagementException(operation, statusCode, errorCondition, description);
    }

    private static DateTimeOffset[] DecodeExpirations(AmqpMessage response, int expected)
    {
        if (response.BodyValueBytes is not { } bodyMem || bodyMem.IsEmpty)
            throw new InvalidDataException("Service Bus $management response missing body.");

        var span = bodyMem.Span;
        var mapView = AmqpCompoundReader.ReadMap(span, out _);
        var els = mapView.Elements;
        var pairCount = mapView.Count / 2;
        int o = 0;
        for (int i = 0; i < pairCount; i++)
        {
            // Key may be a string or a symbol; Service Bus uses symbol
            // for $management response field names.
            string key;
            int kAdvance;
            var keyCode = els[o];
            if (keyCode == AmqpFormatCode.Symbol8 || keyCode == AmqpFormatCode.Symbol32)
                key = AmqpVariableReader.ReadSymbol(els[o..], out kAdvance);
            else
                key = AmqpVariableReader.ReadString(els[o..], out kAdvance);
            o += kAdvance;

            if (key == "expirations")
            {
                var arr = AmqpCompoundReader.ReadArray(els[o..], out var arrLen);
                o += arrLen;
                if (arr.ElementConstructor != AmqpFormatCode.TimestampMs)
                    throw new InvalidDataException(
                        $"Service Bus renew-lock returned non-timestamp array (constructor 0x{arr.ElementConstructor:X2}).");
                if (arr.Count != expected)
                    throw new InvalidDataException(
                        $"Service Bus renew-lock returned {arr.Count} expirations, expected {expected}.");
                var result = new DateTimeOffset[arr.Count];
                var data = arr.ElementData;
                for (int j = 0; j < arr.Count; j++)
                {
                    var unixMs = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data[(j * 8)..]);
                    result[j] = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
                }
                return result;
            }
            // Skip unknown value.
            var valLen = AmqpValueScanner.Measure(els[o..]);
            o += valLen;
        }
        throw new InvalidDataException("Service Bus renew-lock response missing 'expirations' field.");
    }

    private static DateTimeOffset DecodeSingleExpiration(AmqpMessage response, string operation)
    {
        if (response.BodyValueBytes is not { } bodyMem || bodyMem.IsEmpty)
            throw new InvalidDataException($"Service Bus {operation} response missing body.");

        var span = bodyMem.Span;
        var mapView = AmqpCompoundReader.ReadMap(span, out _);
        var els = mapView.Elements;
        var pairCount = mapView.Count / 2;
        int o = 0;
        for (int i = 0; i < pairCount; i++)
        {
            string key;
            int kAdvance;
            var keyCode = els[o];
            if (keyCode == AmqpFormatCode.Symbol8 || keyCode == AmqpFormatCode.Symbol32)
                key = AmqpVariableReader.ReadSymbol(els[o..], out kAdvance);
            else
                key = AmqpVariableReader.ReadString(els[o..], out kAdvance);
            o += kAdvance;

            if (key == "expiration")
            {
                var valCode = els[o];
                if (valCode != AmqpFormatCode.TimestampMs)
                    throw new InvalidDataException(
                        $"Service Bus {operation} returned non-timestamp 'expiration' field (constructor 0x{valCode:X2}).");
                var unixMs = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(els[(o + 1)..]);
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }
            var valLen = AmqpValueScanner.Measure(els[o..]);
            o += valLen;
        }
        throw new InvalidDataException($"Service Bus {operation} response missing 'expiration' field.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _link.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Raised when a Service Bus <c>$management</c> operation returns a
/// non-success status. Carries the broker-reported status code,
/// optional description, and optional AMQP error condition so callers
/// can map to the right protocol-level error response.
/// </summary>
internal sealed class ServiceBusManagementException : Exception
{
    public ServiceBusManagementException(
        string operation, int statusCode, string? errorCondition, string? description)
        : base($"Service Bus management operation '{operation}' failed with status {statusCode}" +
            (errorCondition is null ? string.Empty : $" ({errorCondition})") +
            (description is null ? string.Empty : $": {description}"))
    {
        Operation = operation;
        StatusCode = statusCode;
        ErrorCondition = errorCondition;
        Description = description;
    }

    public string Operation { get; }
    public int StatusCode { get; }
    public string? ErrorCondition { get; }
    public string? Description { get; }
}
