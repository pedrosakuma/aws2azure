# kinesis / GetShardIterator {#operation-kinesis-getsharditerator}

[← kinesis operation index](../../kinesis.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:kinesis:getsharditerator`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Event Hubs (AMQP 1.0 data plane)`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### Stateless HMAC-signed iterator tokens {#sub-feature-stateless-hmac-signed-iterator-tokens}

- **Capability ID:** `sub-feature:kinesis:getsharditerator:stateless-hmac-signed-iterator-tokens`
- **Status:** ✅ implemented

The proxy issues opaque shard iterators signed with the configured shard-iterator signing key (or the process-local fallback), rejects expired or future-issued tokens, and enforces a 5-minute TTL.

### Core iterator types {#sub-feature-core-iterator-types}

- **Capability ID:** `sub-feature:kinesis:getsharditerator:core-iterator-types`
- **Status:** ✅ implemented

Supports TRIM_HORIZON, LATEST, AT_TIMESTAMP, AT_SEQUENCE_NUMBER, and AFTER_SEQUENCE_NUMBER request shapes.

## Behaviour differences

- Iterators are proxy-issued opaque tokens rather than broker cursors; they remain valid for 5 minutes and require the proxy's configured shard-iterator signing key (or the process-local fallback key after restartless reuse). [conformance:field-value:ShardIterator]
- AT_SEQUENCE_NUMBER and AFTER_SEQUENCE_NUMBER accept two aws2azure-specific sequence forms: GetRecords-returned sequence:<x-opt-sequence-number> tokens round-trip to Event Hubs sequence selectors, while synthetic PutRecord/PutRecords sequence numbers are still interpreted as (unixMs << 20) | counter and therefore remain best-effort at millisecond granularity. If synthetic parsing fails the follow-up read falls back to the start of the shard.
- AT_TIMESTAMP positions are stored as ISO-8601 UTC in the opaque token; Timestamp values outside DateTimeOffset's supported Unix-millisecond range are rejected with ValidationException instead of surfacing an internal error.
- LATEST is translated to the AMQP filter `amqp.annotation.x-opt-offset > '@latest'` when the iterator's dedicated receiver link is first opened by GetRecords, not when GetShardIterator issues the token. A record published between those calls can be skipped; callers that need an explicit boundary can prime the iterator with GetRecords before publishing.
- Every GetShardIterator response carries a distinct iterator identity. GetRecords pools one AMQP receiver link per identity, so separate iterator chains progress independently while live; this profile still certifies only one consumer loop per partition and consumer group.
- AT_TIMESTAMP on the emulator is sensitive to host/container clock skew: the host-captured boundary timestamp can drift past the container-side x-opt-enqueued-time of records produced shortly after, hiding them from the receiver. Production Azure issues a single authoritative timestamp, so the divergence is emulator-only — tracked at #119, covered by real-Azure smoke.
- Verified against Event Hubs emulator (except the AT_TIMESTAMP scenario called out above); production Azure Event Hubs coverage is exercised by the real-Azure conformance workflow.
- A stream name that was never provisioned surfaces a native Kinesis ResourceNotFoundException (400). Azure Service Bus's Atom-based Event Hubs management GET can answer with HTTP 200 and a body lacking an <EventHubDescription> element instead of a clean 404 for a nonexistent entity; the proxy now checks for the presence of that element directly and treats its absence as not-found, rather than letting the parse failure escape as an unmapped error. This check is deliberately narrow: a present-but-malformed description (a genuinely existing stream with a broken field) still surfaces its own parse error rather than being reclassified as not-found. Confirmed against real Azure Event Hubs by GetShardIterator_against_nonexistent_stream_returns_native_resource_not_found_error.
- ResourceNotFoundException.ErrorType surfaces as Unknown rather than Sender on AWSSDK.Kinesis, for any JSON-protocol Kinesis error the proxy renders. AWSSDK.Core's JSON-RPC error unmarshaller (JsonErrorResponseUnmarshaller) hardcodes ErrorType.Unknown and only derives a Sender/Receiver classification from an Error/Type XML element (an XML-protocol-only mechanism) — no JSON body field or generic response header feeds it for this SDK version, so this is a client-SDK characteristic the proxy cannot influence, not a gap in the proxy's response. Confirmed by decompiling AWSSDK.Core 4.0.9 and AWSSDK.Kinesis 4.0.8.18.

## References

- <https://docs.aws.amazon.com/kinesis/latest/APIReference/API_GetShardIterator.html>

