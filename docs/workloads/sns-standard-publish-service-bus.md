# SNS standard publish profile (Service Bus Topics backend)

This profile covers `CreateTopic`, `Publish`, `PublishBatch`, and `DeleteTopic`
for an SNS topic whose Publish/PublishBatch traffic is routed to **Azure
Service Bus Topics** over AMQP 1.0 (the proxy default backend, `kind:
serviceBusTopics` with no per-topic `backend` override). Topic administration
always uses Service Bus regardless of the selected publish backend; see the
companion [Event Grid backend profile](sns-standard-publish-event-grid.md) for
the same four operations with Publish/PublishBatch routed to Event Grid
instead. `Subscribe`, subscription management, FIFO topic ordering/dedup, and
IAM-backed delivery/redrive policy administration are not required by profile
version 1.

## Required deployment contract

- Leave `services.sns.defaultBackend` at `ServiceBusTopics` (the default) and
  do not set a per-topic `backend: EventGrid` override for topics this profile
  owns.
- Authenticate with SAS or Entra ID CBS; both are supported over AMQP.
- Size messages within the Service Bus tier's effective limit, not SNS's 256 KB
  publish limit — Standard Service Bus is more restrictive.
- Use bounded AWS SDK retries for retryable throttling and transient AMQP send
  failures; `Publish`/`PublishBatch` are not generally safe to retry without an
  application idempotency strategy.

## Behavioral qualification

`MessageId` is a proxy-generated GUID and `SequenceNumber` is always empty —
neither is an AWS-issued SNS identifier. Subject is carried both as the AMQP
`subject` property and the `aws.sns.Subject` application property; message
attributes are reconstructed downstream from parallel `{Name}` /
`{Name}.DataType` application properties. `PublishBatch` reports best-effort
per-entry outcomes over AMQP: a send failure for one entry does not fail
sibling entries in the same call, but the shape of `Failed` entries can differ
from AWS SNS's queueing-side partial-failure semantics.

Before adoption, exercise representative concurrent publish load, batch
partial failure with a genuine per-entry send failure, bounded retry
exhaustion, and proxy restart (topic state lives in Service Bus, so restarting
the proxy does not lose topics, but in-flight AMQP sender state is
connection-affine). The repository's real-Azure conformance seals establish
operation correctness; the profile remains `candidate` until production-shaped
SLO and rollback qualification are reviewed and committed.

## Capabilities outside version 1

`Subscribe`/`Unsubscribe`/subscription attributes, FIFO topic ordering and
content-based deduplication, and SNS delivery/redrive policy attributes
(`DeliveryPolicy`, `RedrivePolicy`, `SubscriptionRoleArn`) are accepted as
documented gaps or simply not required by this profile version. Topic and
subscription ARNs use a proxy-synthesised placeholder account id; do not parse
AWS account identity out of a returned ARN.
