# Versioning and compatibility policy

This document defines the compatibility contract for released aws2azure
artifacts. It applies to the proxy binary and container, operator configuration,
published manifests and schemas, workload support claims, and proxy-owned data
formats stored in clients or Azure.

The contract becomes binding with `v1.0.0`. The current `v0.1.0` release is a
prerelease: compatibility is best effort and every `0.y` release must describe
its migration and rollback impact.

## Release versions and support window

aws2azure follows [Semantic Versioning 2.0.0](https://semver.org/):

- **MAJOR** changes may intentionally break a public contract after the
  deprecation rules below.
- **MINOR** changes add backward-compatible functionality. They may add optional
  configuration or schema fields with safe defaults.
- **PATCH** changes fix defects or security issues without intentionally changing
  supported contracts.

Tags and release artifacts use `vMAJOR.MINOR.PATCH`. A workload profile has its
own integer `version`; a profile version is not inferred from the proxy version.
The release support matrix maps the two.

For a stable major line, the supported window is the latest patch of the current
minor and the latest patch of the immediately previous minor. "Supported" means
that compatibility and security fixes are considered for those releases; it is
not a commercial SLA. Operators should move to the latest patch before reporting
a defect.

## Upgrade, mixed-version, and rollback span

The supported in-place span is:

- any patch to a later patch in the same minor;
- the latest patch of minor `N-1` to the latest patch of minor `N`, within one
  major; and
- rollback over the same span when the target release notes do not identify an
  irreversible migration.

Skipping a minor, crossing a major, or downgrading across more than one minor
requires staged hops or workload-specific qualification. During a rolling
upgrade, only those two adjacent supported minors may serve the same bindings
and Azure resources.

Before rollout, operators must:

1. read both releases' support/compatibility matrices;
2. retain the exact previous artifact and configuration;
3. use configuration accepted by both versions while they coexist;
4. verify that every applicable durable and rolling-upgrade format below is
   listed as compatible; and
5. exercise candidate-write/previous-read rollback against the actual workload.

Rollback restores code and configuration. It does not undo Azure writes,
messages, resource mutations, or credential revocation.

## Public compatibility obligations

### AWS wire behavior and workload profiles

The public behavior contract is scoped to the workload profiles announced as
GA in a release, not to complete AWS service parity. Removing a required
operation, invalidating a previously accepted request, or materially changing
the documented semantics of a GA profile requires a MAJOR release unless it is
a security correction or a fix that restores the already documented contract.
Every behavior change still appears in release notes and the gap documentation.

### Operator configuration

The JSON document selected by `AWS2AZURE_CONFIG_FILE`, its `AWS2AZURE__...`
environment overrides, and documented process-level startup controls are the
public operator configuration contract. Current process-level controls include
`AWS2AZURE_MAX_CONNECTIONS_PER_SERVER`, `AWS2AZURE_SB_SESSION_IDLE_SECONDS`,
and the emulator-only `AWS2AZURE_INSECURE_TLS`. Within a major:

- a configuration accepted by a supported older release must retain its meaning;
- additions are optional and default to the prior behavior;
- renames, removals, type changes, new required values, and changed defaults are
  breaking changes;
- a new release may accept a superset that an older release cannot parse, so a
  rolling upgrade must use the intersection until rollback risk has passed; and
- release notes must show any required edit and the old/new forms without
  exposing secrets.

Diagnostic or test switches explicitly marked unsuitable for production, such
as `AWS2AZURE_AMQP_TIMING`, are not supported production configuration. Their
addition, change, or removal must still be identified in release notes.

The configuration currently has no embedded schema version, so proxy SemVer
governs the whole shape. Configuration is startup state, not a migration log.

### Manifests and versioned schemas

Repository and release artifacts that carry `schema_version` use an integer
schema version independent of proxy SemVer.

- Adding an optional field may keep the same schema version only when every
  supported consumer safely accepts its absence and presence.
- Removing or renaming a field, changing its type or meaning, or adding a
  required field requires a schema-version increment.
- Before a producer emits schema `N`, supported consumers must read `N` and
  `N-1`. They must continue doing so for at least the stable deprecation period.
- Producers emit one canonical current schema; generated outputs are regenerated
  from their source and are never hand-edited.
- A release must identify every schema it emits and accepts. The current
  repository schemas are version `1`; the two-version reader obligation begins
  when a version `2` producer is introduced.

This applies to workload profiles, qualification/evidence records, approved
runtime ledgers, real-Azure conformance records, and sealed-runtime
manifests/identities. Gap YAML is a strict, unversioned source schema today;
changing its accepted shape requires a coordinated validator, documentation,
generated-output, and release-note change.

### Persisted formats

Every proxy-owned format belongs to one class:

- **durable** — retained across restarts without a bounded protocol lifetime.
  The current and previous supported minor must both read data written by either
  version. Changes use dual-read/single-write or a separately qualified,
  reversible migration.
- **rolling-upgrade** — client- or broker-held state with a bounded operational
  lifetime. Adjacent supported minors must consume each other's values for the
  maximum lifetime, and deployments must retain the same signing material.
- **ephemeral** — process-local, reconstructible state. It may be discarded on
  restart and must never be the only copy of correctness-critical state.

A MINOR or PATCH release must not silently make previous-version rollback unable
to read newly written durable state. If compatibility cannot be preserved, the
change is MAJOR or opt-in and disabled until an explicitly documented migration.

## Persisted-format inventory

The references below identify the current producer/consumer and representative
contract tests. Provider-owned Azure wire formats are not redefined here.

| Format | Class | Compatibility boundary | Source and tests |
|---|---|---|---|
| Binding-centric JSON configuration, `AWS2AZURE__...` overrides, and documented startup environment controls | durable | Operator-owned startup state; the old configuration and production controls must retain their meaning throughout the supported rollback span. | [`ConfigDocument`](../src/Aws2Azure.Core/Configuration/ConfigDocument.cs#L5-L37), [`Program`](../src/Aws2Azure.Proxy/Program.cs#L89-L97), [`process controls`](../src/Aws2Azure.Proxy/Program.cs#L157-L202), [`connection cap`](../src/Aws2Azure.Core/Azure/AzureHttpClient.cs#L361-L372); [`ProxyConfigLoaderTests`](../tests/Aws2Azure.UnitTests/Configuration/ProxyConfigLoaderTests.cs#L22-L177), [`AzureHttpClientConnectionCapTests`](../tests/Aws2Azure.UnitTests/Azure/AzureHttpClientTests.cs#L271-L294) |
| DynamoDB item documents in Cosmos (`_a2a`, `_a2a_pk`, `_a2a:*`, shadow and order-key fields, native `ttl`) | durable | Items may outlive every proxy process. Readers and writers in adjacent supported minors must round-trip both versions' documents. | [`InferredAttributeStorage`](../src/Aws2Azure.Modules.DynamoDb/Persistence/InferredAttributeStorage.cs#L15-L121); [`InferredAttributeStorageTests`](../tests/Aws2Azure.UnitTests/DynamoDb/Persistence/InferredAttributeStorageTests.cs#L63-L205) |
| DynamoDB table sidecar document `__aws2azure_table_meta__` | durable | Table keys, indexes, tags, billing mode, and TTL metadata must remain readable and mutation-safe across upgrade and rollback. | [`TableMetadata`](../src/Aws2Azure.Modules.DynamoDb/Internal/TableMetadata.cs#L7-L76), [`TableLifecycleMetadata`](../src/Aws2Azure.Modules.DynamoDb/Operations/TableLifecycleMetadata.cs#L70-L204); [`TableLifecycleHandlersTests`](../tests/Aws2Azure.UnitTests/DynamoDb/TableLifecycleHandlersTests.cs#L109-L211) |
| DynamoDB Cosmos stored procedures (`atomicWrite_v2`, `atomicTransactWrite_v2`) and their JSON parameter/AST contracts | durable | Stored procedures remain in each container after process replacement. A body-contract change must use a fresh versioned ID so old and new binaries never execute different bodies under one ID during upgrade or rollback. | [`SprocManager`](../src/Aws2Azure.Modules.DynamoDb/Internal/SprocManager.cs#L18-L165); [`TransactWriteItemsHandlerTests`](../tests/Aws2Azure.UnitTests/DynamoDb/TransactWriteItemsHandlerTests.cs#L274-L303), [`SprocAstSerializerTests`](../tests/Aws2Azure.UnitTests/DynamoDb/SprocAstSerializerTests.cs#L9-L53) |
| S3 bucket tags and versioning intent in container metadata (`aws2azurebuckettags`, `aws2azureversioning`) | durable | Bucket state survives process replacement. Adjacent versions must preserve both values and unrelated Azure metadata while reading and updating either format. | [`SubresourceHandlers`](../src/Aws2Azure.Modules.S3/Operations/SubresourceHandlers.cs#L36-L47), [`tag/versioning codecs`](../src/Aws2Azure.Modules.S3/Operations/SubresourceHandlers.cs#L455-L599); [`SubresourceHandlersTests`](../tests/Aws2Azure.UnitTests/S3/SubresourceHandlersTests.cs#L19-L80), [`versioning tests`](../tests/Aws2Azure.UnitTests/S3/SubresourceHandlersTests.cs#L147-L220) |
| SQS queue tags in Service Bus `QueueDescription.UserMetadata` (`A2ZSQST1`) | durable | Queue tags survive process replacement; unknown/foreign metadata must not be overwritten. | [`SqsQueueTagStore`](../src/Aws2Azure.Modules.Sqs/Operations/SqsQueueTagStore.cs#L12-L215); [`TailHandlersTests`](../tests/Aws2Azure.UnitTests/Sqs/TailHandlersTests.cs#L112-L185) |
| SNS deterministic subscription IDs and JSON in Service Bus `SubscriptionDescription.UserMetadata` | durable | The topic/protocol/endpoint identity plus filter policy/scope and raw-delivery state survive process replacement. The ID derivation and serialized metadata must stay stable across upgrade and rollback. | [`SnsSubscriptionSupport`](../src/Aws2Azure.Modules.Sns/Operations/SnsSubscriptionSupport.cs#L68-L105), [`serialization`](../src/Aws2Azure.Modules.Sns/Operations/SnsSubscriptionSupport.cs#L203-L233); [`SubscribeHandlerTests`](../tests/Aws2Azure.UnitTests/Sns/SubscribeHandlerTests.cs#L145-L188) |
| Secrets Manager internal Key Vault tags (`aws2azure-client-request-token`, `-payload-sha256`, `-version-stages`) | durable | Idempotency and version-stage semantics must survive upgrade and rollback; the prefix remains reserved. | [`KeyVaultSecretClient`](../src/Aws2Azure.Modules.SecretsManager/KeyVaultSecretClient.cs#L10-L16), [`tag codec`](../src/Aws2Azure.Modules.SecretsManager/KeyVaultSecretClient.cs#L293-L438); [`SecretsManagerServiceModuleTests`](../tests/Aws2Azure.UnitTests/SecretsManager/SecretsManagerServiceModuleTests.cs#L781-L803), [`idempotency tests`](../tests/Aws2Azure.UnitTests/SecretsManager/SecretsManagerServiceModuleTests.cs#L929-L973) |
| SQS broker-retained message attributes and `Aws2Azure-AttrTypes` side channel | rolling-upgrade | Messages produced before or during rollout must be readable until queue/message retention and DLQ drain complete. | [`SqsAttributeTypeRegistry`](../src/Aws2Azure.Modules.Sqs/Operations/SqsAttributeTypeRegistry.cs#L9-L49), [`AmqpSendMessageHandlers`](../src/Aws2Azure.Modules.Sqs/Operations/AmqpSendMessageHandlers.cs#L25-L43), [`AmqpMessageTranslator`](../src/Aws2Azure.Modules.Sqs/Operations/AmqpMessageTranslator.cs#L89-L169); [`AmqpSendMessageHandlersTests`](../tests/Aws2Azure.UnitTests/Sqs/AmqpSendMessageHandlersTests.cs#L88-L117), [`AmqpReceiveMessageAttributesTests`](../tests/Aws2Azure.UnitTests/Sqs/AmqpReceiveMessageAttributesTests.cs#L57-L102) |
| SNS broker/Event Grid message envelopes and application-property names | rolling-upgrade | Broker-retained or retried notifications and subscriber filters may span a rollout; subject, FIFO, and message-attribute encodings must retain their meaning until delivery and retry windows close. | [`SnsPublishSupport`](../src/Aws2Azure.Modules.Sns/Operations/SnsPublishSupport.cs#L134-L215), [`EventGridEnvelope`](../src/Aws2Azure.Modules.Sns/EventGrid/EventGridEnvelope.cs#L1-L58); [`PublishHandlerTests`](../tests/Aws2Azure.UnitTests/Sns/PublishHandlerTests.cs#L105-L175), [`EventGridPublisherTests`](../tests/Aws2Azure.UnitTests/Sns/EventGridPublisherTests.cs#L30-L53) |
| DynamoDB Query/Scan continuation sentinels (`LastEvaluatedKey.__a2a_continuation`) | rolling-upgrade | Client-held base64 Cosmos continuations must round-trip through `ExclusiveStartKey` across adjacent versions for an active pagination sequence. | [`QueryHandler`](../src/Aws2Azure.Modules.DynamoDb/Operations/QueryHandler.cs#L45-L58), [`query encoding`](../src/Aws2Azure.Modules.DynamoDb/Operations/QueryHandler.cs#L735-L746), [`ScanHandler`](../src/Aws2Azure.Modules.DynamoDb/Operations/ScanHandler.cs#L56-L64), [`scan encoding`](../src/Aws2Azure.Modules.DynamoDb/Operations/ScanHandler.cs#L586-L600); [`QueryHandlerTests`](../tests/Aws2Azure.UnitTests/DynamoDb/QueryHandlerTests.cs#L790-L832), [`ScanHandlerTests`](../tests/Aws2Azure.UnitTests/DynamoDb/ScanHandlerTests.cs#L715-L752) |
| S3 multipart `UploadId` and Azure uncommitted-block IDs | rolling-upgrade | Candidate and previous versions must complete/abort uploads created by either version for the seven-day upload lifetime; the storage account key is part of token validation. | [`UploadIdCodec`](../src/Aws2Azure.Modules.S3/Internal/UploadIdCodec.cs#L7-L39), [`block IDs`](../src/Aws2Azure.Modules.S3/Internal/UploadIdCodec.cs#L116-L142); [`UploadIdCodecTests`](../tests/Aws2Azure.UnitTests/S3/UploadIdCodecTests.cs#L10-L91) |
| S3 listing continuation tokens | rolling-upgrade | Tokens are client-held opaque Azure markers; adjacent versions must decode values minted before traffic shifts. | [`ContinuationTokenCodec`](../src/Aws2Azure.Modules.S3/Internal/ContinuationTokenCodec.cs#L5-L62); [`ContinuationTokenCodecTests`](../tests/Aws2Azure.UnitTests/S3/ContinuationTokenCodecTests.cs#L5-L39) |
| SQS REST v1 and AMQP v2/v3 receipt handles | rolling-upgrade | Deletes and visibility changes must route handles minted by a draining version until the Service Bus lock expires. | [`ReceiptHandle`](../src/Aws2Azure.Modules.Sqs/Internal/ReceiptHandle.cs#L8-L125), [`AmqpReceiptHandle`](../src/Aws2Azure.Modules.Sqs/Internal/AmqpReceiptHandle.cs#L9-L174); [`ReceiptHandleTests`](../tests/Aws2Azure.UnitTests/Sqs/ReceiptHandleTests.cs#L7-L87), [`AmqpReceiptHandleTests`](../tests/Aws2Azure.UnitTests/Sqs/AmqpReceiptHandleTests.cs#L7-L165) |
| SQS ListQueues/ListDeadLetterSourceQueues `NextToken` cursors | rolling-upgrade | Client-held Service Bus skip offsets must retain their meaning across adjacent versions for an active pagination sequence. | [`QueueLifecycleHandlers`](../src/Aws2Azure.Modules.Sqs/Operations/QueueLifecycleHandlers.cs#L217-L307), [`TailHandlers`](../src/Aws2Azure.Modules.Sqs/Operations/TailHandlers.cs#L45-L159); [`SqsRealAzureConformanceTests`](../tests/Aws2Azure.IntegrationTests/Sqs/SqsRealAzureConformanceTests.cs#L11-L51), [`TailHandlersTests`](../tests/Aws2Azure.UnitTests/Sqs/TailHandlersTests.cs#L398-L470) |
| Kinesis shard iterators and ListShards cursors (`aws2az-it-`, `aws2az-ls-`, payload `v1`) | rolling-upgrade | Tokens live for five minutes. All coexisting instances must use the same configured `shardIteratorSigningKey`; the fallback process key is restart-local and therefore only ephemeral. | [`HmacTokenCodec`](../src/Aws2Azure.Modules.Kinesis/ShardIterators/HmacTokenCodec.cs#L14-L104), [`ShardIteratorTokenCodecFactory`](../src/Aws2Azure.Modules.Kinesis/ShardIterators/ShardIteratorTokenCodecFactory.cs#L7-L43), [`ListShardsCursorCodec`](../src/Aws2Azure.Modules.Kinesis/ShardIterators/ListShardsCursorCodec.cs#L8-L113); [`ShardIteratorTokenCodecTests`](../tests/Aws2Azure.UnitTests/Kinesis/ShardIteratorTokenCodecTests.cs#L23-L178), [`ListShardsHandlerTests`](../tests/Aws2Azure.UnitTests/Kinesis/ListShardsHandlerTests.cs#L64-L93) |
| SNS ListTopics/ListSubscriptions next tokens | rolling-upgrade | Client-held pagination offsets must decode across adjacent versions for an active pagination sequence. | [`SnsTopicSupport`](../src/Aws2Azure.Modules.Sns/Operations/SnsTopicSupport.cs#L205-L228), [`SnsSubscriptionSupport`](../src/Aws2Azure.Modules.Sns/Operations/SnsSubscriptionSupport.cs#L147-L190); [`ListTopicsHandlerTests`](../tests/Aws2Azure.UnitTests/Sns/ListTopicsHandlerTests.cs#L72-L95), [`ListSubscriptionsHandlerTests`](../tests/Aws2Azure.UnitTests/Sns/ListSubscriptionsHandlerTests.cs#L57-L89) |
| Secrets Manager `ListSecrets` Key Vault `$skiptoken` values | rolling-upgrade | The AWS `NextToken` is a client-held opaque Azure marker; adjacent versions must forward tokens minted before traffic shifts for an active pagination sequence. | [`ListSecretsHandler`](../src/Aws2Azure.Modules.SecretsManager/Operations/ListSecretsHandler.cs#L10-L73); [`SecretsManagerServiceModuleTests`](../tests/Aws2Azure.UnitTests/SecretsManager/SecretsManagerServiceModuleTests.cs#L292-L360) |
| Gap docs, real-Azure conformance, workload profile/qualification/evidence/approved-runtime records, and sealed-runtime manifests | durable | These are reviewed source/provenance records. Schema changes follow the versioned-schema rules above; generated site/registry files remain commit-coherent with their sources. | [`docs/gaps/README`](./gaps/README.md#L1-L22), [`WorkloadGaCertification`](../tools/Aws2Azure.GapDocs/WorkloadGaCertification.cs#L103-L118), [`SloQualification`](../tools/Aws2Azure.GapDocs/SloQualification.cs#L189-L204), [`ApprovedRuntimeLedger`](../tools/Aws2Azure.GapDocs/ApprovedRuntimeLedger.cs#L250-L291), [`sealed-runtime-manifest.sh`](../eng/sealed-runtime-manifest.sh#L228-L287); [`WorkloadCheckerTests`](../tests/Aws2Azure.UnitTests/GapDocs/WorkloadCheckerTests.cs#L75-L117), [`ApprovedRuntimeLedgerTests`](../tests/Aws2Azure.UnitTests/GapDocs/ApprovedRuntimeLedgerTests.cs#L11-L35), [`test-sealed-runtime-manifest.sh`](../eng/test-sealed-runtime-manifest.sh#L28-L95) |
| DynamoDB table and Event Hubs metadata caches | ephemeral | Rebuilt from Azure after TTL expiry or restart; neither cache is authoritative. | [`TableMetadataCache`](../src/Aws2Azure.Modules.DynamoDb/Internal/TableMetadataCache.cs#L7-L95), [`EventHubMetadataCache`](../src/Aws2Azure.Modules.Kinesis/EventHubsRest/EventHubMetadataCache.cs#L16-L108); [`TableLifecycleHandlersTests`](../tests/Aws2Azure.UnitTests/DynamoDb/TableLifecycleHandlersTests.cs#L25-L44), [`EventHubMetadataCacheTests`](../tests/Aws2Azure.UnitTests/Kinesis/EventHubMetadataCacheTests.cs#L10-L83) |
| Entra access-token cache and in-flight refresh state | ephemeral | Rebuilt from the configured identity provider after restart; Azure remains authoritative. | [`CachedTokenSource`](../src/Aws2Azure.Core/Azure/CachedTokenSource.cs#L10-L42); [`CachedTokenSourceTests`](../tests/Aws2Azure.UnitTests/Azure/CachedTokenSourceTests.cs#L11-L124) |
| Service Bus AMQP connection/link/session pools | ephemeral | Connections and links are recreated from configuration and broker state after invalidation or restart. | [`ServiceBusAmqpPool`](../src/Aws2Azure.Amqp/ServiceBus/ServiceBusAmqpPool.cs#L8-L39); [`ServiceBusAmqpPoolTests`](../tests/Aws2Azure.UnitTests/Amqp/ServiceBus/ServiceBusAmqpPoolTests.cs#L7-L145) |
| Per-endpoint circuit-breaker state | ephemeral | Failure counters, open windows, and probes reset on restart; Azure health remains authoritative. | [`CircuitBreaker`](../src/Aws2Azure.Core/Azure/CircuitBreaker.cs#L5-L29); [`CircuitBreakerTests`](../tests/Aws2Azure.UnitTests/Azure/CircuitBreakerTests.cs#L28-L139) |
| Kinesis fallback process signing keys | ephemeral | Tokens signed without configured `shardIteratorSigningKey` are valid only in the issuing process and are not restart/rolling-upgrade compatible. | [`ShardIteratorTokenCodecFactory`](../src/Aws2Azure.Modules.Kinesis/ShardIterators/ShardIteratorTokenCodecFactory.cs#L7-L43), [`ListShardsCursorCodecFactory`](../src/Aws2Azure.Modules.Kinesis/ShardIterators/ListShardsCursorCodec.cs#L8-L41); [`ShardIteratorTokenCodecTests`](../tests/Aws2Azure.UnitTests/Kinesis/ShardIteratorTokenCodecTests.cs#L161-L178), [`ListShardsHandlerTests`](../tests/Aws2Azure.UnitTests/Kinesis/ListShardsHandlerTests.cs#L64-L93) |

Performance baselines, generated reports, test fixtures, and transient workflow
artifacts are commit/run-scoped engineering records, not cross-release runtime
interfaces, unless a release note explicitly promotes one to a published
contract.

## Deprecation

A stable public contract must be marked deprecated in documentation and release
notes for at least **two consecutive MINOR releases and 90 days**, whichever is
longer, before removal in the next MAJOR release. The notice must identify the
replacement, migration, rollback impact, and last version that supports the old
form.

An actively exploited vulnerability, provider shutdown, or data-corruption risk
may require faster removal. The release must then include a security notice,
explicit operator action, and the safest available migration/rollback path.

## Prereleases

Prereleases use SemVer identifiers such as `-alpha.N`, `-beta.N`, and `-rc.N`.
They are outside the stable support window and are never an implicit upgrade
target for stable deployments.

- Alpha and beta artifacts may change configuration and persisted formats between
  builds; their notes must say so.
- An RC is immutable and intended for final qualification. Its release notes must
  declare the candidate profile/support matrix and any difference from the
  intended stable release.
- State written by a prerelease is rollback-compatible only when that prerelease's
  notes explicitly say so. A stable release may not assume operators tested an RC.

## Mandatory release notes

Every GitHub release, including a prerelease, must use
[`.github/RELEASE_TEMPLATE.md`](../.github/RELEASE_TEMPLATE.md). The completed
notes must:

- state stable/prerelease status and support window;
- list artifacts, checksums, provenance, and architectures;
- publish the workload-profile support matrix without implying general AWS
  parity;
- declare from/to upgrade, mixed-version, configuration, schema, persisted-format,
  and rollback status;
- list deprecations, removals, security exceptions, and required operator actions;
- link the exact gap, qualification, and approved-runtime evidence used for each
  GA claim; and
- say `None` rather than omit a mandatory section.

The current release workflow uses GitHub-generated notes when it creates a
missing release. Those notes are an asset-upload bootstrap, not a completed
release record. After the workflow finishes, the release owner must replace the
body with a completed template (for example, `gh release edit vX.Y.Z
--notes-file <completed-template>`) and verify that no placeholder remains
before the release is announced or treated as supported. Generated change notes
may be copied into the template's Changes section; they do not replace any
mandatory matrix or declaration. A workflow run is not release-complete until
this manual postcondition is satisfied.
