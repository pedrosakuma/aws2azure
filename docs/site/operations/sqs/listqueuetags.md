# sqs / ListQueueTags {#operation-sqs-listqueuetags}

[← sqs operation index](../../sqs.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sqs:listqueuetags`
- **Status:** 🟡 partial
- **Disposition:** 🛠️ feasible backlog
- **Tracking issue:** [#693](https://github.com/pedrosakuma/aws2azure/issues/693)
- **Azure equivalent:** `GET QueueDescription and decode aws2azure's compact metadata envelope from UserMetadata.`

## Sub-features

### Queue existence validation {#sub-feature-queue-existence-validation}

- **Capability ID:** `sub-feature:sqs:listqueuetags:queue-existence-validation`
- **Status:** ✅ implemented

Returns NonExistentQueue if the SB queue does not exist.

### Tags round-trip {#sub-feature-tags-round-trip}

- **Capability ID:** `sub-feature:sqs:listqueuetags:tags-round-trip`
- **Status:** ✅ implemented

Decodes the SQS tag map persisted by CreateQueue/TagQueue/UntagQueue in QueueDescription.UserMetadata.

### Empty / foreign metadata handling {#sub-feature-empty---foreign-metadata-handling}

- **Capability ID:** `sub-feature:sqs:listqueuetags:empty---foreign-metadata-handling`
- **Status:** ✅ implemented

Missing, empty, non-base64, or non-aws2azure UserMetadata is treated as an empty SQS tag map.

## Behaviour differences

- Tags are stored inside an aws2azure-owned compact metadata envelope, base64-encoded into Service Bus QueueDescription.UserMetadata. Azure-side tools will see the opaque base64 blob rather than individual tag keys.
- Service Bus UserMetadata is limited to roughly 1024 characters in the legacy management schema, so TagQueue may reject otherwise-valid SQS tag sets that cannot fit.
- Real-Azure conformance coverage exists in Aws2Azure.IntegrationTests.Sqs.SqsRealAzureConformanceTests.Queue_metadata_and_tags_round_trip_against_real_service_bus; it was not executed in this environment.

## References

- <https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ListQueueTags.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-xml-management-api>

