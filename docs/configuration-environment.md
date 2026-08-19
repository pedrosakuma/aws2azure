# Process environment and configuration overrides

The proxy first loads the JSON file, applies recognized `AWS2AZURE__...`
overrides as a typed overlay, translates the document, and runs startup
validation. Overrides are useful for secret injection and small deployment
differences; use a schema-validated JSON file for the stable topology.

## Process-level environment variables

| Variable | Type / accepted value | Default | Applicability | Validation and behavior |
|---|---|---|---|---|
| `AWS2AZURE_CONFIG_FILE` | File path | `config.json` beside the binary, then release `config.example.json` | All deployments | Selected file must be readable JSON. Missing explicit paths result in an empty document and startup validation failure. |
| `ASPNETCORE_URLS` | Semicolon-separated listener URLs | ASP.NET Core host default; repository deployments set `http://+:8080` | HTTP listener and built-in `--health-check` | Use valid Kestrel URL prefixes. The health-check uses the first URL and replaces `+`/`*` with `localhost`. |
| `AWS2AZURE_INSECURE_TLS` | Exact string `1` | Off | Local self-signed emulators only | Disables all outbound Azure certificate validation and logs a warning. Never set in production. Other values leave validation enabled. |
| `AWS2AZURE_MAX_CONNECTIONS_PER_SERVER` | Positive integer | `64` | Outbound Azure HTTP | Invalid, zero, or negative values use `64`. |
| `AWS2AZURE_SB_SESSION_IDLE_SECONDS` | Integer seconds | `300` | AMQP FIFO session receivers | Positive values set idle eviction; zero/negative disables idle eviction. Invalid text logs a warning and uses `300`. |
| `AWS2AZURE_AMQP_TIMING` | Exact string `1` | Off | On-demand AMQP diagnosis | Emits allocating, synchronous timing rows to stderr. Never leave enabled in production. |
| `AZURE_TENANT_ID` | Non-empty string | None | `workloadIdentity` | Required at startup for every workload-identity auth block. |
| `AZURE_CLIENT_ID` | Non-empty string | None | `workloadIdentity` | Required at startup; identifies the federated Entra application. |
| `AZURE_FEDERATED_TOKEN_FILE` | File path | None | `workloadIdentity` | A non-empty path is required at startup. The projected token file must be readable and non-empty when a token is acquired and is re-read on refresh. |
| `AZURE_AUTHORITY_HOST` | Absolute authority URI | `https://login.microsoftonline.com/` | `workloadIdentity`; sovereign clouds | Use an absolute HTTP(S) authority. Malformed/relative values fail token-source construction or the first token request. |
| `IDENTITY_ENDPOINT` | Absolute URI | IMDS `http://169.254.169.254/...` | Managed identity on App Service / Container Apps | Used only together with `IDENTITY_HEADER`; otherwise IMDS is used. |
| `IDENTITY_HEADER` | Non-empty header secret | None | Managed identity on App Service / Container Apps | Used only together with `IDENTITY_ENDPOINT`; a missing/empty member of the pair falls back to IMDS. |

Standard ASP.NET Core host/logging variables remain governed by ASP.NET Core
configuration and are not part of the operator JSON contract.

## `AWS2AZURE__` path rules

The prefix is exact and case-sensitive; path segments after it are
case-insensitive. `__` separates JSON path segments. Binding and list indices
are decimal integers from `0` through `1023`. New overrides should use invariant
booleans, integers, and the canonical enum strings from the
[generated field reference](configuration-reference.md). Runtime parsing also
preserves the v1-defined numeric and whitespace-padded enum/backend forms.

| Override path pattern | Type / default | Applicability | Validation |
|---|---|---|---|
| `AWS2AZURE__SERVICES__<service>__ENABLED` | Boolean; JSON/default `false` | `s3`, `sqs`, `dynamodb`, `sns`, `kinesis`, `secretsmanager` | `true` or `false`; invalid values are ignored. |
| `...__SERVICES__SNS__DEFAULTBACKEND` | Enum; `ServiceBusTopics` | SNS | `ServiceBusTopics` or `EventGrid`. |
| `...__SERVICES__DYNAMODB__USESTOREDPROCEDURES` | Enum; `Disabled` | DynamoDB | `Disabled`, `Preferred`, or `Required`. |
| `...__SERVICES__DYNAMODB__CONSISTENCYCHECK` | Enum; `Disabled` | DynamoDB | `Disabled`, `Warn`, or `Required`. |
| `...__SERVICES__DYNAMODB__COSMOSBINARYRESPONSES` | Boolean; `false` | DynamoDB opt-in | `true` or `false`. |
| `...__SERVICES__DYNAMODB__COSMOSBINARYREQUESTS` | Boolean; `false` | DynamoDB opt-in | `true` or `false`. |
| `...__SERVICES__DYNAMODB__ENABLEGLOBALSECONDARYINDEXQUERIES` | Boolean; `false` | DynamoDB GSI queries | `true` or `false`. |
| `...__SERVICES__DYNAMODB__ENABLELOCALSECONDARYINDEXNUMERICORDERING` | Boolean; `false` | DynamoDB LSI numeric ordering | `true` or `false`. |
| `AWS2AZURE__BINDINGS__<i>__AWS__ACCESSKEYID` | String; no default | Binding identity | Startup requires non-whitespace and uniqueness. |
| `AWS2AZURE__BINDINGS__<i>__AWS__SECRETACCESSKEY` | String; no default | Binding identity | Startup requires non-whitespace. |
| `...__AZURE__<service>__KIND` | String; no default | Backend discriminator | Values by service: `blob`, `serviceBus`, `cosmos`, `serviceBusTopics`, `eventHubs`, `keyVault`. SNS also accepts legacy standalone `eventGrid` at runtime. |
| `...__AZURE__S3__TARGET__ACCOUNTNAME` / `ENDPOINT` | String; account has no default, endpoint omission derives public Azure | Blob | Account name is required; endpoint must be absolute HTTP(S) when set. |
| `...__AZURE__SQS__TARGET__NAMESPACE` / `MANAGEMENTENDPOINT` / `TRANSPORT` | String / enum; namespace has no default, endpoint derives, transport defaults `Rest` | Service Bus queues | Namespace required; endpoint absolute HTTP(S); transport `Rest` or `Amqp`. |
| `...__AZURE__DYNAMODB__TARGET__ENDPOINT` / `DATABASENAME` | String; no default | Cosmos DB | Both required; endpoint absolute HTTP(S). |
| `...__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__<i>` | String list item; no default | Cosmos DB | Non-empty. Index `0` is the transaction authority; later regions are non-transaction fallbacks. |
| `...__AZURE__SNS__TARGET__ENDPOINT` / `NAMESPACE` / `MANAGEMENTENDPOINT` | String; namespace has no default, endpoints derive | Service Bus Topics | Namespace required; endpoint HTTP(S)/AMQP(S); management endpoint HTTP(S). |
| `...__AZURE__KINESIS__TARGET__NAMESPACE` / `ENDPOINT` | String; namespace has no default, endpoint derives | Event Hubs | Namespace required; endpoint HTTP(S)/AMQP(S). |
| `...__AZURE__SECRETSMANAGER__TARGET__VAULTURL` | String; no default | Key Vault | Required absolute HTTPS URL. |
| `...__AZURE__<service>__AUTH__MODE` | Enum; backend-specific default | Selected backend | `sharedKey` may be omitted only where the authoring schema shows that default; other usable modes must be explicit. Applied only when the backend supports the mode. |
| `...__AZURE__<service>__AUTH__KEY` / `KEYNAME` | String; no default | Key/SAS-capable backend | Applied only where the selected mode uses it; startup validates required pairs. |
| `...__AZURE__<service>__AUTH__TENANTID` / `CLIENTID` / `CLIENTSECRET` / `IDENTITY` | String; no default | Entra-capable backend | Applied only where supported; startup validates the selected auth shape/reference. |
| `...__AZURE__KINESIS__SHARDITERATORSIGNINGKEY` | Base64 string; no default | Event Hubs | Startup requires valid base64 decoding to at least 32 bytes. |
| `...__AZURE__SQS__QUEUES__<queue-name>__TRANSPORT` | Enum; inherits backend transport | Per-queue SQS transport | `Rest` or `Amqp`; queue names containing `__` are reconstructed. |
| `...__AZURE__SNS__TARGET__ENDPOINT` / `NAMESPACE` / `TOPICNAME` | String; no default | v1-compatible standalone `kind: eventGrid` only | `endpoint` must be absolute HTTPS, or `namespace` and `topicName` must both be non-empty. This runtime-only shape is not canonical authoring. |

Use the full `AWS2AZURE__` prefix in place of `...`. For example:

```text
AWS2AZURE__SERVICES__DYNAMODB__CONSISTENCYCHECK=Required
AWS2AZURE__BINDINGS__0__AZURE__DYNAMODB__TARGET__PREFERREDREGIONS__0=East US
AWS2AZURE__BINDINGS__0__AZURE__SQS__QUEUES__priority__TRANSPORT=Amqp
```

`azureIdentities`, S3 `presignedTrustedSigningHosts`, SNS
`topics`/`eventGridFallback`, and Kinesis `streams` are JSON-file-only.
Malformed paths, unsupported leaves/modes, invalid values, indices above 1023,
and trailing segments are ignored without creating partial objects. This is a
v1 compatibility behavior, not a validation mechanism: validate the base file
and confirm the effective configuration through startup.
