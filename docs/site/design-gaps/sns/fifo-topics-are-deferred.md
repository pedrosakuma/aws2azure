# sns design gap / FIFO topics are deferred {#design-gap-sns-fifo-topics-are-deferred}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:fifo-topics-are-deferred`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#692](https://github.com/pedrosakuma/aws2azure/issues/692)

aws2azure now supports a bounded Service Bus subset for SNS .fifo topics. On the Service Bus backend, MessageGroupId maps to SessionId (AMQP group-id), MessageDeduplicationId maps to broker MessageId, and ContentBasedDeduplication=true falls back to SHA-256(message body) when no dedup id is supplied. Event Grid still cannot honor FIFO semantics and rejects them.

**Impact.** The proxy can approximate SNS FIFO only within the Service Bus backend's native limits. Deduplication is broker-windowed rather than a portable SNS guarantee, no SNS-compatible SequenceNumber is returned, and the built-in SNS subscription-management APIs still create regular (non-session-aware) Service Bus subscriptions.

**Workaround.** Use the Service Bus backend for FIFO topics, keep publish retries within the topic's duplicate-detection window, and provision session-aware Service Bus subscriptions with Azure-native tooling when guaranteed ordered processing matters.

