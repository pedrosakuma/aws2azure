# sns design gap / FIFO topics are deferred {#design-gap-sns-fifo-topics-are-deferred}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:fifo-topics-are-deferred`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design

aws2azure supports a bounded Service Bus subset for SNS .fifo topics. On the Service Bus backend, MessageGroupId maps to SessionId (AMQP group-id), MessageDeduplicationId maps to broker MessageId, and ContentBasedDeduplication=true falls back to SHA-256(message body) when no dedup id is supplied. Event Grid still cannot honor FIFO semantics and rejects them. Reclassified from feasible_backlog to by_design after re-review for #800: the three residual gaps below are structural boundaries of the Service Bus AMQP entity model rather than something further engineering here can close.

**Impact.** The proxy can approximate SNS FIFO only within the Service Bus backend's native limits, for structural reasons: (1) Service Bus has no concept of an SNS-compatible, globally-orderable SequenceNumber -- its sequence number is a per-entity, broker-local counter with no cross-entity or cross-region portability guarantee, so surfacing it as an SNS SequenceNumber would misrepresent a guarantee the backend does not provide; (2) Service Bus duplicate detection is an intentionally time-windowed, broker-side dedup cache (bounded by DuplicateDetectionHistoryTimeWindow) with no unbounded, SNS-parity dedup guarantee to request; (3) making the SNS subscription-management APIs (Subscribe et al.) provision session-aware Service Bus subscriptions by default would require guessing a RequiresSession=true topology up front for every subscription regardless of whether the topic is FIFO, which is unsafe for non-FIFO topics and would need a new, deliberate opt-in configuration surface rather than a translation-fidelity fix.

**Workaround.** Use the Service Bus backend for FIFO topics, keep publish retries within the topic's duplicate-detection window, and provision session-aware Service Bus subscriptions with Azure-native tooling when guaranteed ordered processing matters.

