# SNS standard publish profile (Event Grid backend)

This profile covers `CreateTopic`, `Publish`, `PublishBatch`, and `DeleteTopic`
for an SNS topic whose Publish/PublishBatch traffic is routed to **Azure Event
Grid** via a per-topic `backend: EventGrid` override (or
`services.sns.defaultBackend: EventGrid` with the topic's destination/key
supplied by the binding's `azure.sns` block or its `eventGridFallback`).
`CreateTopic` and `DeleteTopic` still create and delete the backing Azure
Service Bus topic in this slice — subscription metadata continues to live on
Service Bus — so this profile requires the same Service Bus Topics binding as
the [Service Bus backend profile](sns-standard-publish-service-bus.md) in
addition to the Event Grid destination. `Subscribe`, subscription management,
FIFO topic ordering/dedup, and IAM-backed delivery/redrive policy
administration are not required by profile version 1.

## Required deployment contract

- Configure the Event Grid custom topic destination and access key either
  per-topic (`azure.sns.topics.<pattern>.eventGridTopicEndpoint` /
  `.eventGridAccessKey`) or at the binding level (`azure.sns.kind: eventGrid`
  as an `eventGridFallback` under the Service Bus Topics binding, combined with
  `services.sns.defaultBackend: EventGrid`).
- The Event Grid custom topic must use the classic Event Grid schema
  (`inputSchema: EventGridSchema`); CloudEvents-formatted custom topics are not
  supported by this profile.
- Size messages within Event Grid's classic-schema limits: 1 MB per event, 1 MB
  per HTTP batch, 5000 events per batch POST — all narrower than SNS's own
  limits in some dimensions.
- Use bounded AWS SDK retries for retryable HTTP-level publish failures;
  `Publish`/`PublishBatch` are not generally safe to retry without an
  application idempotency strategy.

## Behavioral qualification

`MessageId` is a proxy-generated GUID and `SequenceNumber` is always empty.
The proxy emits the classic Event Grid schema with `eventType=aws.sns.Message`
and `subject` always set to the SNS `TopicArn`; the AWS `Subject` parameter is
carried inside `data.Subject`, and message attributes are emitted as
`data.MessageAttributes` entries of the shape `{ Type, Value }`.

`PublishBatch` partial-failure semantics differ meaningfully from the Service
Bus backend: an oversized single entry (over the 1 MB classic-schema limit) is
rejected before any HTTP call and fails only that entry, but an HTTP-level
failure from Event Grid itself is mapped to a `Failed` result for **every**
message in the affected HTTP batch, even though Event Grid accepts or rejects
each POST atomically. Do not assume a `Failed` entry means only that specific
message was rejected by Event Grid.

`MessageGroupId` / `MessageDeduplicationId` are not honoured on this backend;
the proxy drops them and logs a warning rather than rejecting the request.

Before adoption, exercise representative concurrent publish load, a batch
containing both a genuinely oversized entry and normal entries (to observe the
real per-entry vs. whole-batch failure shapes above), bounded retry
exhaustion, and proxy restart. This backend's own `Event Grid publish path` /
`Event Grid batch publish path` sub-features each carry their own
`verified_real_azure` seal (see `required_sub_feature_seals` in the
manifest), captured against a live Event Grid custom topic and its Storage
Queue event subscription — not merely inherited from the shared Service Bus
seal on the same operations. The profile mechanically certifies as
`candidate`; GA additionally requires production-shaped SLO, restart, and
rollback qualification to be reviewed and committed.

## Capabilities outside version 1

`Subscribe`/`Unsubscribe`/subscription attributes, FIFO topic ordering and
content-based deduplication, and SNS delivery/redrive policy attributes are
accepted as documented gaps or simply not required by this profile version.
Topic and subscription ARNs use a proxy-synthesised placeholder account id.
