# SNS subscription management profile (Service Bus Topics backend)

This profile covers `Subscribe`, `ConfirmSubscription`, `ListSubscriptions`,
`ListSubscriptionsByTopic`, `GetSubscriptionAttributes`,
`SetSubscriptionAttributes`, and `Unsubscribe` for Azure Service Bus topic
subscriptions. Subscription ARNs are synthetic:
`arn:aws:sns:{request-region}:000000000000:{topic}:{subscription-id}`, where a
proxy-created subscription id is the first 20 lowercase hexadecimal characters
of SHA-256 over `TopicArn + "\n" + Protocol + "\n" + Endpoint`.

## Required deployment contract

- Use the Service Bus Topics publish backend for topics adopting this profile.
  These APIs do not manage Azure Event Grid event subscriptions, and Event
  Grid-published events do not fan out through the Service Bus subscriptions.
- Keep the AWS binding secret stable across proxy instances and restarts.
  Version-1 subscription pagination tokens are HMAC-SHA256 signed with that
  secret and bound to their list operation; per-topic tokens are additionally
  bound to the exact topic name.
- Treat `sqs`, `http`, and `https` as metadata labels for a native Azure
  subscriber. The proxy records the endpoint but does not run an SNS push
  delivery worker.

## Lifecycle and metadata contract

`Subscribe` is deterministic and immediately confirmed. `ConfirmSubscription`
is therefore a validated no-op: it accepts the matching SubscriptionArn or its
20-hex id, rejects arbitrary or cross-topic tokens, and never performs an
out-of-band challenge. Filter policy/scope and raw-delivery values persist in
`SubscriptionDescription.UserMetadata`; filter policy is not programmed into a
Service Bus rule and raw delivery does not change a delivery envelope.

`SetSubscriptionAttributes` reads the current Atom entry, merges only the
aws2azure UserMetadata value into the original ordered Service Bus property
set, and conditionally writes it with the returned ETag in `If-Match`.
Concurrent mutation is surfaced as `ConcurrentAccess`, so unrelated Service Bus
properties are neither defaulted nor silently overwritten.

## Qualification boundary

Adoption requires live lifecycle, metadata persistence across proxy restart,
signed pagination and cross-use rejection, concurrent management load, and
idempotent cleanup evidence. The repository contains deterministic unit and
Service Bus emulator coverage plus opt-in real-Azure lifecycle/load scenarios;
the profile remains `candidate` until the reviewed evidence and rollback
qualification are committed by the evidence owner.

Cross-account/IAM delivery policy, strict SNS FIFO semantics, active
HTTP/S/SQS push delivery, and every Azure Event Grid event-subscription
operation are outside version 1.
