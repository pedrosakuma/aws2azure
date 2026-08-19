# sqs / ListDeadLetterSourceQueues {#operation-sqs-listdeadlettersourcequeues}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:listdeadlettersourcequeues`
- **Status:** ✅ implemented
- **Azure equivalent:** `Page through SB management GET /$Resources/queues?api-version=2021-05 and filter entries whose ForwardDeadLetteredMessagesTo equals the requested queue.`
- **Real-Azure verified:** ✅ 2026-08-11 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/31447694984) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/31447694984)

## Sub-features

### Queue existence probe {#sub-feature-queue-existence-probe}

- **Capability ID:** `sub-feature:sqs:listdeadlettersourcequeues:queue-existence-probe`
- **Status:** ✅ implemented

SQS returns NonExistentQueue when the DLQ target itself is unknown; the proxy issues a GET /{queue} before paging.

### Page-walk + filter {#sub-feature-page-walk--filter}

- **Capability ID:** `sub-feature:sqs:listdeadlettersourcequeues:page-walk--filter`
- **Status:** ✅ implemented

SB management API caps a page at 100 entries; the proxy walks pages until a short page is observed, filtering each entry by ForwardDeadLetteredMessagesTo == target.

### MaxResults / NextToken pagination {#sub-feature-maxresults---nexttoken-pagination}

- **Capability ID:** `sub-feature:sqs:listdeadlettersourcequeues:maxresults---nexttoken-pagination`
- **Status:** ✅ implemented

MaxResults defaults to 1000 (SQS hard cap); NextToken is a stateless integer cursor into the SB queue listing. The proxy emits it only when another matching source exists, and the cursor survives proxy restart.

## Behaviour differences

- Linear scan: the proxy issues one or more SB management GETs per ListDeadLetterSourceQueues call. On namespaces with thousands of queues this is O(N) and may be slow; the NFR phase should consider a cached reverse index.
- Emulator-backed validation is blocked because the Service Bus emulator does not expose management REST. The sqs-dlq-redrive real-Azure source scenario covers pagination and resume across proxy restart.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ListDeadLetterSourceQueues.html>
- <https://learn.microsoft.com/rest/api/servicebus/list-queues>

