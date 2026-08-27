# Production configuration examples

These complete JSON documents are starting points for real Azure. Each example
is validated against [`config.schema.json`](../config.schema.json), loaded
through the source-generated `ConfigDocument` serializer, translated, and
checked by startup validation in the unit-test suite.

Replace every `replace-with-*` value and example identity/resource name. Store
the resulting file in a platform secret, mount it read-only, and select it with
`AWS2AZURE_CONFIG_FILE`.

The Kinesis example's `shardIteratorSigningKey` is also a deterministic
schema-valid placeholder. Replace it with a unique, cryptographically random
32-byte-or-longer key encoded as base64; reusing or publishing that key makes
opaque iterator tokens forgeable.

## Backend and authentication coverage

| AWS surface | Azure backend | Authentication demonstrated | Example |
|---|---|---|---|
| S3 | Blob (`blob`) | `sharedKey` | [`blob-shared-key.json`](configuration/examples/blob-shared-key.json) |
| SQS | Service Bus queues (`serviceBus`) | `sas` | [`service-bus-sas.json`](configuration/examples/service-bus-sas.json) |
| DynamoDB | Cosmos DB (`cosmos`) | `clientSecret` | [`cosmos-client-secret.json`](configuration/examples/cosmos-client-secret.json) |
| SNS | Service Bus topics (`serviceBusTopics`) plus Event Grid fallback (`eventGrid`) | `managedIdentity` plus Event Grid `sharedKey` | [`service-bus-topics-event-grid.json`](configuration/examples/service-bus-topics-event-grid.json) |
| Kinesis | Event Hubs (`eventHubs`) | `workloadIdentity` | [`event-hubs-workload-identity.json`](configuration/examples/event-hubs-workload-identity.json) |
| Secrets Manager | Key Vault (`keyVault`) | named `reference` to `managedIdentity` | [`key-vault-reference.json`](configuration/examples/key-vault-reference.json) |
| Multi-service, single binding | Cosmos + Event Hubs + Key Vault | named `reference` to `managedIdentity` | [`single-tenant-managed-identity.json`](configuration/examples/single-tenant-managed-identity.json) |
| Multi-environment proxy | Blob + Service Bus across two bindings | `sharedKey` plus `sas` per environment | [`multi-environment-bindings.json`](configuration/examples/multi-environment-bindings.json) |
| Mixed auth in one binding | Blob + Key Vault | `sharedKey` plus `managedIdentity` | [`mixed-auth-single-binding.json`](configuration/examples/mixed-auth-single-binding.json) |

Together these examples exercise every canonical backend kind and every backend
`auth.mode`: `sharedKey`, `sas`, `clientSecret`, `managedIdentity`,
`workloadIdentity`, and `reference`. The
[authentication matrix](configuration-schema.md#authentication-shapes) states
which other mode/backend combinations are valid.

## Production controls, not emulator shortcuts

The examples intentionally:

- use Azure resource names or public HTTPS endpoints rather than Docker service
  names;
- omit `target.endpoint` and `target.managementEndpoint` where the runtime can
  derive the public Azure endpoint;
- never set `AWS2AZURE_INSECURE_TLS`;
- use a persistent `shardIteratorSigningKey` for Kinesis;
- choose explicit identities and least-privilege policy names rather than
  emulator root credentials.

Emulator settings remain in [`docker/config.json`](../docker/config.json) and
the sidecar demo. Do not promote emulator hostnames, self-signed-certificate
bypasses, `devstoreaccount1`, or emulator root SAS keys into staging or
production.

## Combining examples

A production document may enable several services and place their backends in
one binding when one AWS signing identity should reach all of them. Keep
separate bindings when workloads need different Azure resources or privilege
boundaries. Every `aws.accessKeyId` must be unique.

For shared Entra credentials, define one `azureIdentities.<name>` and use
`auth.mode: reference` in each AAD-capable backend. Names are case-sensitive.
Configuration is loaded once at process start; rotate a file or environment
secret through a controlled restart rather than expecting hot reload.

[`single-tenant-managed-identity.json`](configuration/examples/single-tenant-managed-identity.json)
shows one AWS identity mapped to Cosmos DB, Event Hubs, and Key Vault through a
single named managed identity. Use this pattern when one workload owns one
tenant-scoped Azure trust boundary and you want the proxy to avoid stored Entra
client secrets; on AKS, keep the same binding shape and swap the named identity
to `authMode: workloadIdentity`.

[`multi-environment-bindings.json`](configuration/examples/multi-environment-bindings.json)
shows two complete bindings in one document so the same proxy instance can host
separate dev and staging credentials without sharing Azure resources. Use this
when each environment needs its own AWS signing key, Blob account, or Service
Bus namespace, but operationally you still want one proxy deployment.

[`mixed-auth-single-binding.json`](configuration/examples/mixed-auth-single-binding.json)
shows one AWS identity reaching multiple Azure services that do not share the
same credential shape: Blob Storage stays on an account `sharedKey` while Key
Vault uses Managed Identity. Use this when one application signs everything
with one AWS keypair but each downstream Azure service should keep its native,
least-privilege authentication mode.
