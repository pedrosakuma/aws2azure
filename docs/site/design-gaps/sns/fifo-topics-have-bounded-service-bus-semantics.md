# sns design gap / FIFO topics have bounded Service Bus semantics {#design-gap-sns-fifo-topics-have-bounded-service-bus-semantics}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:fifo-topics-have-bounded-service-bus-semantics`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

aws2azure supports a bounded Service Bus subset for SNS .fifo topics. MessageGroupId maps to SessionId (AMQP group-id). FifoThroughputScope=Topic preserves the effective AWS deduplication ID as broker MessageId; MessageGroup derives a deterministic composite broker identity so the same AWS ID can be reused independently across groups, while retaining the AWS ID as application metadata. ContentBasedDeduplication uses SHA-256(message body). Event Grid still cannot honor FIFO semantics and rejects .fifo topics plus FIFO-only request fields.

**Impact.** The proxy can approximate SNS FIFO only within the Service Bus backend's native limits, for structural reasons: (1) Service Bus has no concept of an SNS-compatible, globally-orderable SequenceNumber -- its sequence number is a per-entity, broker-local counter with no cross-entity or cross-region portability guarantee, so surfacing it as an SNS SequenceNumber would misrepresent a guarantee the backend does not provide; (2) Service Bus duplicate detection is an intentionally time-windowed, broker-side dedup cache (bounded by DuplicateDetectionHistoryTimeWindow) with no unbounded, SNS-parity dedup guarantee to request; (3) FifoThroughputScope=MessageGroup can reproduce deduplication scope but cannot create AWS per-group throughput quotas or isolation because Service Bus capacity and throttling remain governed by the namespace/entity tier and topology; (4) making the SNS subscription-management APIs provision session-aware Service Bus subscriptions by default would require guessing a RequiresSession=true topology up front for every subscription regardless of whether the topic is FIFO.

**Workaround.** Use the Service Bus backend for FIFO topics, choose FifoThroughputScope for deduplication behavior rather than throughput guarantees, keep publish retries within the topic's duplicate-detection window, and provision/session-size Service Bus resources with Azure-native tooling when guaranteed ordered processing or capacity isolation matters.

## References

- <https://docs.aws.amazon.com/sns/latest/dg/fifo-message-dedup.html>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-quotas>

