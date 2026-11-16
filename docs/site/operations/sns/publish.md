# sns / Publish {#operation-sns-publish}

[← sns operation index](../../sns.md) · [Coverage matrix](../../coverage.md)

- **Capability ID:** `operation:sns:publish`
- **Status:** 🟡 partial
- **Disposition:** 🔵 by design
- **Azure equivalent:** `Azure Service Bus Topics / Azure Event Grid`
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

## Sub-features

### AMQP publish path {#sub-feature-amqp-publish-path}

- **Capability ID:** `sub-feature:sns:publish:amqp-publish-path`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-16 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29473539261)

Sends SNS Publish requests to Azure Service Bus Topics over AMQP 1.0 using SAS or Entra ID CBS authentication.

### Event Grid publish path {#sub-feature-event-grid-publish-path}

- **Capability ID:** `sub-feature:sns:publish:event-grid-publish-path`
- **Status:** ✅ implemented
- **Real-Azure verified:** ✅ 2026-07-21 · [evidence](https://github.com/pedrosakuma/aws2azure/actions/runs/29789050325) · [workflow run](https://github.com/pedrosakuma/aws2azure/actions/runs/29789050325)

Sends SNS Publish requests to Azure Event Grid custom topics over the classic Event Grid schema using a per-topic backend switch.

### Service Bus FIFO subset {#sub-feature-service-bus-fifo-subset}

- **Capability ID:** `sub-feature:sns:publish:service-bus-fifo-subset`
- **Status:** ✅ implemented

For Service Bus-backed topics whose SNS names end in .fifo, Publish requires MessageGroupId and maps it to AMQP group-id/Service Bus SessionId. Topic scope preserves the effective AWS deduplication ID as the broker MessageId. MessageGroup scope derives a deterministic SHA-256 broker MessageId from length-prefixed MessageGroupId plus the effective explicit or content-based deduplication ID, while retaining that AWS deduplication ID in the AWS.Aws2Azure.MessageDeduplicationId application property. The underlying Service Bus topic must have duplicate detection enabled.

## Behaviour differences

- Publish accepts the legacy TargetArn parameter as a fallback alias for TopicArn when TopicArn is absent, matching real AWS SNS's backward-compatible behavior (TargetArn predates TopicArn and is still sent by some SNS clients, including Apache Airflow's SnsPublishOperator). aws2azure only supports the topic-publish use case; TargetArn values pointing at mobile push platform endpoints are not supported. Confirmed by PublishHandlerTests.HandleAsync_accepts_legacy_TargetArn_as_alias_for_TopicArn.
- MessageId is a proxy-generated GUID, not an AWS-generated SNS identifier.
- SequenceNumber is returned empty because neither Azure Service Bus nor Azure Event Grid exposes an SNS-compatible sequence number on publish.
- MessageStructure=json is passed through as-is; the proxy does not filter per-protocol payloads yet.
- On the Service Bus Topics backend, MessageAttributes encode DataType in a parallel application property named '{Name}.DataType' so AWS-style attributes can be reconstructed by downstream consumers.
- RawMessageDelivery stored on SNS subscriptions is not consulted at publish time. Service Bus publishes always send the raw message bytes, and Event Grid publishes always emit the Event Grid envelope described below.
- On the Event Grid backend, the proxy emits the classic Event Grid schema with eventType=aws.sns.Message; CloudEvents-formatted Event Grid topics are not supported in this slice.
- On the Event Grid backend, MessageAttributes are emitted inside data.MessageAttributes as { Type, Value } objects.
- On the Event Grid backend, the returned SNS MessageId is the proxy-generated GUID used as the Event Grid envelope id field.
- On the Event Grid backend, the Event Grid envelope subject is always the SNS TopicArn; the AWS Subject parameter is copied into data.Subject.
- On the Event Grid backend, HTTP-level publish failures are mapped to SNS per-message failure semantics by the proxy; Publish returns an SNS error while PublishBatch marks each affected entry failed.
- On the Service Bus Topics backend, regional failover follows Service Bus Geo-DR only when the configured namespace/endpoints use the Geo-DR alias hostname; direct primary-namespace endpoints do not switch regions automatically, and queued topic/subscription messages still are not replicated by Geo-DR.
- On the Event Grid backend, publish eligibility is determined solely by the configured Event Grid route/credentials. CreateTopic / DeleteTopic manage only the Service Bus compatibility side and do not create, delete, or existence-check the Azure Event Grid custom topic before Publish.
- On the Event Grid backend, multiple SNS TopicArns can share a single Azure Event Grid custom topic when routing falls back to the credential-level endpoint or namespace+topicName. Azure-side isolation is then by envelope fields (subject / data.TopicArn), not by dedicated Azure topic resources.
- On the Event Grid backend, authentication supports only `aeg-sas-key` shared keys or Microsoft Entra bearer tokens. Event Grid SAS publish tokens (`aeg-sas-token` / `Authorization: SharedAccessSignature`) are not accepted or generated by the proxy.
- On the Event Grid backend, Publish targets classic Event Grid custom topics. Regional recovery for those topics is metadata-only and Microsoft-managed on a best-effort timeline, so the proxy cannot provide rapid, customer-controlled regional failover by itself.
- Subject is exposed both as the AMQP subject property and as the 'aws.sns.Subject' application property on the Service Bus Topics backend.
- For Service Bus-backed FIFO topics, broker-side duplicate detection is limited to Service Bus's duplicate-detection window. aws2azure provisions new FIFO topics with a 5-minute window, but out-of-band topic changes or publishes outside that window are treated as new messages.
- Topic FifoThroughputScope keeps the prior broker identity unchanged, so identical effective deduplication IDs are duplicates even when MessageGroupId differs. MessageGroup scope uses a fixed 64-character collision-resistant composite broker MessageId, so identical IDs in different groups remain independent while retries in the same group remain deterministic. The original/effective AWS deduplication ID is retained separately in AWS.Aws2Azure.MessageDeduplicationId because the composite broker MessageId is an Azure implementation detail. Covered by SnsFifoPublishSupportTests.Topic_scope_preserves_the_effective_deduplication_id_as_broker_identity, SnsFifoPublishSupportTests.MessageGroup_scope_is_deterministic_and_separates_identical_ids_across_groups, and SnsPublishServiceBusTests.PublishBatch_message_group_scope_deduplicates_retries_per_group.
- MessageGroup scope reproduces SNS deduplication scope only; it does not create independent Azure throughput quotas, reserve capacity per session, or raise Service Bus namespace/entity limits. Azure throughput and throttling remain governed by the selected Service Bus tier, namespace capacity, messaging units, partitions, and workload shape.
- FIFO Publish re-reads the Service Bus topic description before resolving broker identity because FifoThroughputScope is mutable. This management-plane lookup avoids stale per-process scope across proxy replicas but adds backend latency compared with standard-topic publishes and the former immutable FIFO metadata cache. Covered by PublishHandlerTests.HandleAsync_reloads_mutable_scope_instead_of_using_stale_cached_metadata.
- MessageGroupId and explicit MessageDeduplicationId enforce AWS's 1-128 character visible-ASCII contract before reaching Service Bus, ensuring the SessionId and raw Topic-scope MessageId stay within Azure's 128-character limits. Covered by SnsFifoPublishSupportTests.Fifo_identifiers_enforce_the_aws_length_limit and SnsFifoPublishSupportTests.Fifo_identifiers_reject_characters_outside_the_aws_ascii_set.
- ContentBasedDeduplication uses the same SHA-256(message body) effective AWS deduplication ID in both scopes. Under MessageGroup scope the group is included only in the broker identity, allowing identical bodies in different groups while suppressing retries within one group. Covered by SnsFifoPublishSupportTests.MessageGroup_scope_applies_to_content_based_deduplication and SnsPublishServiceBusTests.Publish_message_group_scope_applies_to_content_based_deduplication.
- For Service Bus-backed FIFO topics, the proxy does not synthesize or return an SNS FIFO SequenceNumber because Service Bus does not expose an SNS-compatible publish sequence identifier on send.
- For standard (non-.fifo) SNS topic names, MessageGroupId and MessageDeduplicationId are rejected with InvalidParameter instead of being silently approximated.
- FIFO topics are unsupported on the Event Grid backend. Publish rejects .fifo topics and FIFO-only request parameters there with InvalidParameter instead of dropping them.
- aws2azure sets Service Bus SessionId on published FIFO messages, but the current SNS subscription-management APIs still create regular Service Bus subscriptions. Guaranteed ordered processing therefore requires consumers to use Service Bus-native session-aware subscriptions provisioned outside the SNS compatibility APIs.
- Azure Service Bus and Event Grid message size limits differ from SNS; Event Grid classic schema also enforces 1 MB per event and 1 MB per HTTP batch.
- Publish to a nonexistent Service Bus-backed topic: the AMQP CBS (Claims-Based Security) put-token handshake that precedes sender-link attach fails claim validation for a missing topic with HTTP 404 (Azure's own "messaging entity ... could not be found"), not a link-level amqp:unauthorized-access rejection. The proxy inspects the CBS response status code directly and renders SNS's native NotFoundException only for that 404 case; any other CBS status (401/403/etc.) is still treated as a genuine authorization failure. Confirmed against real Azure by SnsRealAzureErrorPathTests.Publish_to_nonexistent_topic_returns_native_not_found_error.

## References

- <https://docs.aws.amazon.com/sns/latest/api/API_Publish.html>
- <https://docs.aws.amazon.com/sns/latest/dg/fifo-message-dedup.html>
- <https://learn.microsoft.com/azure/service-bus-messaging/service-bus-amqp-protocol-guide>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection>
- <https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-geo-dr>
- <https://learn.microsoft.com/en-us/azure/event-grid/authenticate-with-access-keys-shared-access-signatures>
- <https://learn.microsoft.com/en-us/azure/reliability/reliability-event-grid>
- <https://learn.microsoft.com/azure/event-grid/post-to-custom-topic>

