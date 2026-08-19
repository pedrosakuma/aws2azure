# sqs / ListQueues {#operation-sqs-listqueues}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:listqueues`
- **Status:** ✅ implemented
- **Azure equivalent:** `GET https://{namespace}.servicebus.windows.net/$Resources/queues?api-version=2021-05&$skip=N&$top=M`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### QueueNamePrefix {#sub-feature-queuenameprefix}

- **Capability ID:** `sub-feature:sqs:listqueues:queuenameprefix`
- **Status:** ✅ implemented

Filtered proxy-side after Service Bus returns the page; Service Bus has no native server-side prefix filter.

### MaxResults {#sub-feature-maxresults}

- **Capability ID:** `sub-feature:sqs:listqueues:maxresults`
- **Status:** ✅ implemented

Honoured up to the SQS cap of 1000. Server-side pages are 100 (Service Bus management limit); the proxy concatenates pages until MaxResults or end.

### NextToken {#sub-feature-nexttoken}

- **Capability ID:** `sub-feature:sqs:listqueues:nexttoken`
- **Status:** ✅ implemented

Opaque base-10 integer encoding the upstream $skip cursor; an end-of-feed probe avoids issuing a token when no more queues remain.

## Behaviour differences

- Service Bus iteration is by $skip/$top; the cursor is not stable across queue deletions. AWS SQS tokens are likewise opaque, so no public contract is broken.
- Prefix filtering happens after the page is returned, so the same NextToken may visit a partially-filtered page. This is consistent with AWS-SDK pagination but may surface fewer than MaxResults entries per call.
- Pagination is validated against real Azure Service Bus across multiple management API pages.
- Eventual consistency shortly after CreateQueue (issue #626): real-Azure workload run 29790063721 observed 4 transient misses across 1,634 ListQueues attempts (~0.24%) when a worker listed immediately after CreateQueue against a live namespace — the freshly created queue was already usable for Send/ReceiveMessage but not yet visible on the Service Bus management ($Resources/queues) listing. This matches AWS's documented ListQueues eventual-consistency caveat and is not a proxy defect; load-testing and production clients should tolerate a short bounded propagation delay rather than treating a single miss as a hard failure.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ListQueues.html>
- <https://learn.microsoft.com/rest/api/servicebus/list-queues>
- <https://github.com/pedrosakuma/aws2azure/actions/runs/29790063721>

