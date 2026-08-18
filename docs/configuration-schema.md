# Operator configuration schema

[`config.schema.json`](../config.schema.json) is the normative, machine-readable
contract for the binding-centric JSON document selected by
`AWS2AZURE_CONFIG_FILE`. It uses JSON Schema draft 2020-12 and is generated
deterministically without reflection:

```powershell
dotnet run --project tools/Aws2Azure.ConfigSchema
dotnet run --project tools/Aws2Azure.ConfigSchema -- --check
```

The generator and its schema validator are tooling/test dependencies only.
Shipping and Native AOT projects do not load a JSON Schema library at runtime.
Startup validation remains authoritative for checks that JSON Schema cannot
express, such as duplicate AWS access keys, named-identity reference resolution,
and workload-identity environment availability.

## Document shape

| Property | Required | Meaning |
|---|---:|---|
| `bindings` | yes | One or more mappings from an incoming `aws` identity to service-specific `azure` backends. |
| `services` | no | Module enablement and behavior. An omitted service is disabled. |
| `azureIdentities` | no | Named Entra identity pool for backend `auth.mode: reference`. Names are case-sensitive. |

Every binding requires `aws.accessKeyId`, `aws.secretAccessKey`, and an `azure`
object. Unknown properties are rejected. The schema's top-level `examples`
array contains a complete valid document exercising all six services,
`azureIdentities`, queue/topic/stream overrides, `eventGridFallback`,
`shardIteratorSigningKey`, and every DynamoDB behavior flag.

## Services and defaults

| JSON path | Type | Default / values |
|---|---|---|
| `services.<service>.enabled` | boolean | `false` |
| `services.s3.presignedTrustedSigningHosts` | string array | omitted; lowercase bare hosts only |
| `services.sns.defaultBackend` | enum | `ServiceBusTopics`; also `EventGrid` |
| `services.dynamodb.useStoredProcedures` | enum | `Disabled`; also `Preferred`, `Required` |
| `services.dynamodb.consistencyCheck` | enum | `Disabled`; also `Warn`, `Required` |
| `services.dynamodb.cosmosBinaryResponses` | boolean | `false` |
| `services.dynamodb.cosmosBinaryRequests` | boolean | `false` |
| `services.dynamodb.enableGlobalSecondaryIndexQueries` | boolean | `false` |
| `services.dynamodb.enableLocalSecondaryIndexNumericOrdering` | boolean | `false` |

## Azure backend matrix

Each `bindings[].azure.<service>` block separates non-secret topology in
`target` from credentials in `auth`. The `kind` discriminator selects the only
valid shape for that service.

| AWS service | `kind` | Required target | Accepted auth modes | Service-specific fields |
|---|---|---|---|---|
| `s3` | `blob` | `accountName` | `sharedKey` | optional `target.endpoint` |
| `sqs` | `serviceBus` | `namespace` | `sas` | `target.transport` (`Rest`/`Amqp`), `target.managementEndpoint`, `queues` |
| `dynamodb` | `cosmos` | absolute `endpoint`, `databaseName` | `sharedKey`, all Entra modes | `target.preferredRegions` |
| `sns` | `serviceBusTopics` | `namespace` | `sas`, all Entra modes | `topics`, optional Event Grid `eventGridFallback` |
| `sns` | `eventGrid` | HTTPS `endpoint`, or `namespace` + `topicName` | `sharedKey`, all Entra modes | no `eventGridFallback` |
| `kinesis` | `eventHubs` | `namespace` | `sas`, all Entra modes | `streams`, `shardIteratorSigningKey` |
| `secretsmanager` | `keyVault` | HTTPS `vaultUrl` | Entra modes only | none |

`queues`, `topics`, and `streams` are maps whose keys are AWS-facing names:

- Queue settings may override `transport`.
- Topic settings select `backend` and may set `serviceBusTopicName`,
  `eventGridTopicEndpoint`, or `eventGridAccessKey`.
- Stream settings may set `eventHubName`, `consumerGroup`, and a positive
  `partitionCount`.

The Kinesis `shardIteratorSigningKey` is a base64 HMAC key that must decode to
at least 32 bytes. Set it in production so opaque iterators survive restarts.

For a `serviceBusTopics` SNS binding, an Event Grid route must be complete:
either configure `eventGridFallback`, or give each Event Grid topic both
`eventGridTopicEndpoint` and `eventGridAccessKey`. Setting
`services.sns.defaultBackend` to `EventGrid` requires `eventGridFallback` on
every `serviceBusTopics` binding.

## Authentication shapes

| `auth.mode` | Required fields | Applicability |
|---|---|---|
| `sharedKey` | `key` | Blob, Cosmos, Event Grid |
| `sas` | `keyName`, `key` | Service Bus queues/topics, Event Hubs |
| `clientSecret` | `tenantId`, `clientId`, `clientSecret` | Entra-capable backends |
| `managedIdentity` | optional `clientId` | Entra-capable backends |
| `workloadIdentity` | none in JSON; uses `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_FEDERATED_TOKEN_FILE` | Entra-capable backends |
| `reference` | `identity` | Entra-capable backends; name must exist in `azureIdentities` |

A named `azureIdentities` entry uses `authMode` rather than `mode`. A
`clientSecret` identity requires all three inline fields, `managedIdentity`
allows only optional `clientId`, and `workloadIdentity` carries no inline
fields.

## Environment overrides

`AWS2AZURE__` overrides use `__` as a JSON path separator. The prefix is exact;
path segments are case-insensitive. Array indices are decimal path segments:

```text
AWS2AZURE__SERVICES__DYNAMODB__CONSISTENCYCHECK=Required
AWS2AZURE__BINDINGS__0__AWS__ACCESSKEYID=AKIA...
AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__0=West US
AWS2AZURE__BINDINGS__0__AZURE__SQS__QUEUES__orders__TRANSPORT=Amqp
```

Supported override leaves are service enablement and behavior fields; binding
AWS fields; backend `kind`; target fields; auth fields; Kinesis
`shardIteratorSigningKey`; indexed Cosmos `preferredRegions`; and SQS queue
`transport`. Queue names containing `__` are reconstructed from all segments
between `QUEUES` and `TRANSPORT`.

`azureIdentities`, SNS `topics`/`eventGridFallback`, and Kinesis `streams` are
currently JSON-file-only. Unknown or malformed override paths are ignored, so
validate the resulting file against the schema and rely on startup validation
before rollout.
