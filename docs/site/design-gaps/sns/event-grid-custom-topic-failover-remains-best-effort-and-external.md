# sns design gap / Event Grid custom-topic failover remains best-effort and external {#design-gap-sns-event-grid-custom-topic-failover-remains-best-effort-and-external}

[← Design-gap index](../../design-gaps.md)

- **Capability ID:** `design-gap:sns:event-grid-custom-topic-failover-remains-best-effort-and-external`
- **Status:** 🔵 by design
- **Disposition:** 🔵 by design

The SNS Event Grid backend targets classic Event Grid custom topics via EventGridCredentials.Endpoint or namespace+topicName-derived /api/events endpoints. Those topics are single-region for steady-state traffic; Azure's cross-region recovery is metadata-only and Microsoft-managed on a best-effort timeline, not a customer-controlled fast failover. Event Grid namespaces provide a different multitenant model that this proxy does not use for SNS publish.

**Impact.** Regional outages on an Event Grid-backed SNS topic can interrupt Publish and PublishBatch for an extended period, and any unprocessed event data in the failed region can be lost. The proxy cannot provide SNS-style rapid regional failover or active/active publish continuity for this backend by itself.

**Workaround.** Treat the Event Grid backend as requiring an external multiregion DR plan: configure retries/idempotency, dead-letter or archive where needed, and build client-side failover to separately provisioned Event Grid resources if bounded regional failover time matters.

## References

- <https://learn.microsoft.com/en-us/azure/reliability/reliability-event-grid>
- <https://learn.microsoft.com/en-us/azure/event-grid/post-to-custom-topic>

