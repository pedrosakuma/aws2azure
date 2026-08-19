# Operator configuration schema

[`config.schema.json`](../config.schema.json) is the canonical, machine-readable
authoring profile for the binding-centric JSON document selected by
`AWS2AZURE_CONFIG_FILE`. It uses JSON Schema draft 2020-12 and is generated
deterministically without reflection:

When `AWS2AZURE_CONFIG_FILE` is unset, the proxy loads the bundled
`config.json`, or `config.example.json` in release archives that use that
filename. ASP.NET host settings remain separate in `appsettings.json` and are
not part of this operator contract.

Schema validation proves the document's structure and startup semantics; it
cannot prove that placeholder credentials, Azure resources, or endpoints are
live. Replace example credentials before deployment.

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

Every canonical binding requires `aws.accessKeyId`, `aws.secretAccessKey`, and
an `azure` object. Unknown properties are rejected by the authoring profile.
The schema's top-level `examples`
array contains a complete valid document exercising all six services,
`azureIdentities`, queue/topic/stream overrides, `eventGridFallback`,
`shardIteratorSigningKey`, and every DynamoDB behavior flag.

Property names in the authoring profile are case-sensitive and use canonical
spellings, including `dynamodb` and `secretsmanager`. Serialization always emits
those names. Runtime reads remain a compatibility superset of the profile:
property matching is case-insensitive and unknown extension members are
ignored, preserving configurations accepted by v1. New and edited
configurations should validate against the canonical profile; compatibility
acceptance does not make legacy spellings canonical.

Enum values and backend `kind` discriminators remain case-insensitive for
compatibility. The spellings shown in this document and the schema defaults are
canonical. The authoring profile requires JSON strings without surrounding
whitespace. Runtime reads additionally preserve v1 inputs that use a defined
numeric enum token or numeric string, whitespace-padded enum names, or
surrounding whitespace on a backend `kind`; serialization emits enum names, and
new documents should use the canonical strings. Undefined numeric values and
comma-composed enum names are rejected. Runtime compatibility also ignores
known fields that do not apply to the selected backend/auth mode, while the
canonical schema rejects them. Nullable optional properties may be set to JSON
`null`, with the same effect as omission; semantically required values do not
accept `null`.

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

The canonical authoring profile models Event Grid as a routing fallback for a
`serviceBusTopics` binding. SNS control-plane operations require the Service
Bus Topics backend even when Event Grid is selected for publication.

For v1 compatibility, runtime JSON reads and environment overrides continue to
accept a standalone `bindings[].azure.sns.kind: eventGrid` backend. It translates
directly to Event Grid credentials, accepts Event Grid target/auth fields, and
serializes the discriminator as canonical `eventGrid`, but it remains outside
the canonical authoring profile because it cannot provide the SNS control
plane. Migrate an existing standalone block by configuring the primary SNS
backend as `serviceBusTopics` and moving its Event Grid `target` and `auth`
objects into `eventGridFallback`. New configurations should use that preferred
shape. To preserve standalone publish routing, also set
`services.sns.defaultBackend: EventGrid` (or set `topics.<pattern>.backend:
EventGrid` for selected topics).

## Authentication shapes

| `auth.mode` | Required fields | Applicability |
|---|---|---|
| `sharedKey` | `key` | Blob, Cosmos, Event Grid |
| `sas` | `keyName`, `key` | Service Bus queues/topics, Event Hubs |
| `clientSecret` | `tenantId`, `clientId`, `clientSecret` | Entra-capable backends |
| `managedIdentity` | optional `clientId` | Entra-capable backends |
| `workloadIdentity` | none in JSON; uses `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_FEDERATED_TOKEN_FILE` | Entra-capable backends |
| `reference` | `identity` | Entra-capable backends; name must exist in `azureIdentities` |

A shared-key auth block may omit `mode`; it defaults to `sharedKey`. Other
backend auth modes require an explicit discriminator.

A named `azureIdentities` entry uses `authMode` rather than `mode`. A
`clientSecret` identity requires all three inline fields, `managedIdentity`
allows only optional `clientId`, and `workloadIdentity` carries no inline
fields. A named identity may omit `authMode`; it defaults to `clientSecret`.

## Environment overrides

`AWS2AZURE__` overrides use `__` as a JSON path separator. The prefix is exact;
path segments are case-insensitive. Array indices are decimal path segments:

```text
AWS2AZURE__SERVICES__DYNAMODB__CONSISTENCYCHECK=Required
AWS2AZURE__BINDINGS__0__AWS__ACCESSKEYID=AKIA...
AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__0=West US
AWS2AZURE__BINDINGS__0__AZURE__SQS__QUEUES__orders__TRANSPORT=Amqp
```

Supported override leaves are service enablement, SNS `defaultBackend`, and
DynamoDB behavior fields; binding AWS fields; backend `kind`; target fields;
auth fields; Kinesis `shardIteratorSigningKey`; indexed Cosmos
`preferredRegions`; and SQS queue `transport`. Queue names containing `__` are
reconstructed from all segments between `QUEUES` and `TRANSPORT`.

For the legacy standalone SNS `eventGrid` kind, backend overrides accept only
Event Grid target fields (`endpoint`, `namespace`, `topicName`) and shared-key
or Entra auth fields/modes. Override validation resolves the effective `kind`
before applying leaves, so dictionary/environment enumeration order does not
change the result. Service Bus-only leaves and SAS mode are ignored without
mutating the backend.

Environment values retain v1 parser compatibility: defined numeric enum values,
whitespace-padded enum names, and whitespace-padded recognized backend kinds are
accepted. New overrides should use the canonical names shown above. Undefined
numeric enums and unrecognized kinds remain ignored without mutation.

`azureIdentities`, S3 `presignedTrustedSigningHosts`, SNS
`topics`/`eventGridFallback`, and Kinesis `streams` are currently
JSON-file-only. Unknown or malformed override paths are ignored without
creating bindings, backends, services, queues, or region entries. This includes
negative/non-decimal indices, unsupported leaves, invalid scalar values,
indices greater than 1023, and trailing path segments. Validate the resulting
file against the schema and rely on startup validation before rollout.
