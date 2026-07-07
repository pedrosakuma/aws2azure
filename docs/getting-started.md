# Getting Started

`aws2azure` is a transparent HTTP proxy that speaks the **AWS wire protocol**
(SigV4 + the service-specific request/response shapes) and translates each
request into the equivalent **Azure REST API** call. Point any AWS SDK, the AWS
CLI, or boto3 at the proxy by setting its endpoint URL — your code keeps using
AWS APIs while the data lives in Azure.

This guide gets you from zero to a working S3 → Azure Blob round-trip in a few
minutes, then shows how to enable the other services and point at real Azure.

---

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) with the Compose plugin
  (`docker compose`), **or**
- the [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source.
- An AWS client to talk to the proxy: the AWS CLI, boto3, or any AWS SDK.

You do **not** need an Azure subscription for the quickstart — it runs against
the [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
storage emulator.

---

## Quickstart — S3 → Azurite with Docker Compose

The default Compose stack starts the proxy plus Azurite. S3 works immediately;
Azurite creates containers and blobs on demand, so there is nothing to
provision.

```bash
git clone https://github.com/pedrosakuma/aws2azure.git
cd aws2azure
docker compose up --build
```

The proxy listens on `http://localhost:8080`. Wait for it to report healthy:

```bash
curl -fsS http://localhost:8080/health
# {"status":"healthy"}
```

### Talk to it with the AWS CLI

S3 requests are routed by the `Host` header (it must start with `s3.`), so set
the endpoint to an `s3.*` hostname that resolves to the proxy. `s3.localhost`
works on most modern systems; if it doesn't resolve on yours, use
`s3.127.0.0.1.nip.io` instead.

The dev credentials are defined in [`docker/config.json`](../docker/config.json):

```bash
export AWS_ACCESS_KEY_ID=AKIADEVEXAMPLE
export AWS_SECRET_ACCESS_KEY=dev-secret-key-change-me
export AWS_DEFAULT_REGION=us-east-1

ENDPOINT=http://s3.localhost:8080

aws --endpoint-url $ENDPOINT s3 mb s3://demo
echo "hello from aws2azure" > hello.txt
aws --endpoint-url $ENDPOINT s3 cp hello.txt s3://demo/hello.txt
aws --endpoint-url $ENDPOINT s3 ls s3://demo/
aws --endpoint-url $ENDPOINT s3 cp s3://demo/hello.txt -
```

The object is stored in Azurite as a blob — the AWS SDK never knew it left S3.

### Talk to it with boto3

```python
import boto3
from botocore.config import Config

s3 = boto3.client(
    "s3",
    endpoint_url="http://s3.localhost:8080",
    aws_access_key_id="AKIADEVEXAMPLE",
    aws_secret_access_key="dev-secret-key-change-me",
    region_name="us-east-1",
    config=Config(s3={"addressing_style": "path"}),
)

s3.create_bucket(Bucket="demo")
s3.put_object(Bucket="demo", Key="hello.txt", Body=b"hello from aws2azure")
print(s3.get_object(Bucket="demo", Key="hello.txt")["Body"].read())
```

Stop the stack with `docker compose down`.

---

## Configuration

The proxy reads a single JSON config file pointed to by the
`AWS2AZURE_CONFIG_FILE` environment variable. It has **three** top-level
concerns, kept separate on purpose:

| Section | Answers | Contains |
|---|---|---|
| `services` | *which* modules run, and how they behave | per-service `enabled` + behavior knobs |
| `bindings` | *who* maps to *what* | each AWS access key → its Azure backends |
| `azureIdentities` *(optional)* | shared AAD identities | a named pool referenced from `bindings` |

The **binding** is the central concept: each entry maps one AWS identity
(`aws`) to a set of Azure backends (`azure`). Inside every backend, the
non-secret **`target`** (where — commit-safe topology) is split from the secret
**`auth`** (how — keys / identity), and a `kind` says which Azure backend it is.

```jsonc
{
  // 1) Which modules run (+ their behavior settings, folded per service).
  "services": {
    "s3":       { "enabled": true },
    "sqs":      { "enabled": true },
    "dynamodb": { "enabled": true },
    "sns":      { "enabled": true, "defaultBackend": "ServiceBusTopics" },
    "kinesis":  { "enabled": false },  // no local emulator — see the table below
    "secretsmanager": { "enabled": false }  // Key Vault; no local emulator — real Azure only
  },
  // 2) Bindings: one AWS identity -> its Azure backends. Add more entries to
  //    map different AWS keys to different Azure accounts.
  "bindings": [
    {
      // AWS-facing identity your clients sign with (SigV4).
      "aws": {
        "accessKeyId": "AKIADEVEXAMPLE",
        "secretAccessKey": "dev-secret-key-change-me"
      },
      // Azure backends this identity maps to. Only include the services you
      // enable above. Each entry = kind + target (topology) + auth (secret).
      "azure": {
        // S3 -> Blob. target.endpoint is optional; omit it for real Azure to
        // use https://<accountName>.blob.core.windows.net.
        "s3": {
          "kind": "blob",
          "target": { "accountName": "devstoreaccount1", "endpoint": "http://azurite:10000/devstoreaccount1" },
          "auth":   { "mode": "sharedKey", "key": "..." }
        },
        // SQS -> Service Bus (queues).
        "sqs": {
          "kind": "serviceBus",
          "target": { "namespace": "http://servicebus-emulator:5672/", "transport": "Amqp" },
          "auth":   { "mode": "sas", "keyName": "RootManageSharedAccessKey", "key": "..." }
        },
        // SNS -> Service Bus (topics). Optionally set eventGridFallback
        // (kind: eventGrid, its own target+auth) alongside a
        // serviceBusTopics-kind binding to give services.sns.defaultBackend
        // = "EventGrid" or a per-topic backend: "EventGrid" override (that
        // doesn't set its own eventGridTopicEndpoint/eventGridAccessKey) a
        // shared destination to resolve against.
        "sns": {
          "kind": "serviceBusTopics",
          "target": { "namespace": "sbemulatorns", "endpoint": "http://servicebus-emulator:5672/", "managementEndpoint": "http://servicebus-emulator:5300/" },
          "auth":   { "mode": "sas", "keyName": "RootManageSharedAccessKey", "key": "..." }
        },
        // DynamoDB -> Cosmos DB (NoSQL). The database must already exist.
        "dynamodb": {
          "kind": "cosmos",
          "target": { "endpoint": "http://cosmos:8081/", "databaseName": "aws2azure" },
          "auth":   { "mode": "sharedKey", "key": "..." }
        },
        // Kinesis -> Event Hubs (no local emulator; real Azure only).
        "kinesis": {
          "kind": "eventHubs",
          "target": { "namespace": "..." },
          "auth":   { "mode": "sas", "keyName": "RootManageSharedAccessKey", "key": "..." }
        },
        // Secrets Manager -> Key Vault (no local emulator; real Azure only).
        // Key Vault uses Entra ID (AAD), not a shared key — see
        // docs/azure-authentication.md for the managed-identity /
        // workload-identity / client-secret options.
        "secretsmanager": {
          "kind": "keyVault",
          "target": { "vaultUrl": "https://my-vault.vault.azure.net/" },
          "auth":   { "mode": "managedIdentity" }
        }
      }
    }
  ]
}
```

The committed [`docker/config.json`](../docker/config.json) is this shape wired
to the Compose emulator hostnames (S3, SQS, SNS and DynamoDB), with `kinesis`
and `secretsmanager` disabled (no emulator).

Key points:

- **The binding is the core idea.** Each binding's `aws` half (what your clients
  present and sign with) maps to its `azure` half. The proxy validates the
  incoming SigV4 signature against `aws.secretAccessKey`, then calls Azure using
  the matching backend's `auth`.
- **`target` vs `auth`.** Topology (`target`) is non-secret and safe to commit;
  keys and identities live under `auth`, selected by `auth.mode`
  (`sharedKey` / `sas` / `managedIdentity` / `clientSecret` / `workloadIdentity`
  / `reference`). See [Azure authentication](./azure-authentication.md).
- You can define **multiple** bindings to map different AWS keys to different
  Azure accounts.
- The config is **validated on startup** — the proxy fails loud and refuses to
  start on a malformed or ambiguous config.
- Settings can be overridden with environment variables, mirroring the JSON
  shape (e.g. `AWS2AZURE__SERVICES__S3__ENABLED=true`,
  `AWS2AZURE__BINDINGS__0__AZURE__S3__AUTH__KEY=...`).

---

## Enabling the other services

Heavier emulators are opt-in via Compose profiles so the default start stays
fast. Enabling a service in `config.json` is independent of starting its
emulator — the proxy boots regardless and only connects on the first request.

| AWS service | Azure target | Local emulator | Turnkey? |
|---|---|---|---|
| **S3** | Blob Storage | Azurite (default) | ✅ Yes — dynamic containers/blobs |
| **DynamoDB** | Cosmos DB (NoSQL) | Cosmos emulator (`--profile dynamodb`) | ⚠️ Database must be created first; serves a self-signed TLS cert |
| **SQS** | Service Bus | Service Bus emulator (`--profile messaging`) | ⚠️ Queues must be pre-declared in the emulator config |
| **SNS** | Service Bus Topics / Event Grid | Service Bus emulator (`--profile messaging`) | ⚠️ Topics must be pre-declared |
| **Kinesis** | Event Hubs | *(none)* | ❌ No emulator — point at a real Event Hubs namespace |
| **Secrets Manager** | Key Vault | *(none)* | ❌ No emulator — point at a real Key Vault (Entra ID auth; see [Azure authentication](./azure-authentication.md)) |

```bash
docker compose --profile dynamodb up    # adds the Cosmos DB emulator
docker compose --profile messaging up   # adds SQL Edge + the Service Bus emulator
docker compose --profile full up         # adds everything
```

The Service Bus emulator does **not** create queues/topics dynamically — declare
them up front in
[`deploy/emulators/servicebus/Config.json`](../deploy/emulators/servicebus/Config.json).
The Cosmos emulator needs ~3 GB of RAM, takes 1–2 minutes to start, and serves a
self-signed certificate.

> **Emulators are not behavior-equivalent to real Azure.** They diverge on
> consistency, throttling, auth edge cases, and feature surface. Treat "works
> against the emulator" as necessary, not sufficient — validate against real
> Azure before relying on an operation.

---

## Running the container directly

A published image is available from GitHub Container Registry:

```bash
docker run --rm -p 8080:8080 \
  -v "$PWD/docker/config.json:/app/config.json:ro" \
  -e AWS2AZURE_CONFIG_FILE=/app/config.json \
  ghcr.io/pedrosakuma/aws2azure:latest
```

The image is a Native-AOT build on a chiseled (distroless) base, runs as a
non-root user, ships a `HEALTHCHECK` that calls the binary's built-in
`--health-check` probe, and is published as a multi-arch manifest
(`linux/amd64` + `linux/arm64`) so the right architecture is pulled
automatically.

---

## Prebuilt release binaries

Each tagged release attaches a self-contained Native-AOT binary for
`linux-x64` and `linux-arm64` (plus a SHA-256 checksum) to the
[GitHub Releases page](https://github.com/pedrosakuma/aws2azure/releases):

```bash
VERSION=v0.1.0   # pick a release tag
ARCH=linux-x64   # or linux-arm64
curl -fsSL -O "https://github.com/pedrosakuma/aws2azure/releases/download/${VERSION}/aws2azure-${VERSION}-${ARCH}.tar.gz"
curl -fsSL -O "https://github.com/pedrosakuma/aws2azure/releases/download/${VERSION}/aws2azure-${VERSION}-${ARCH}.tar.gz.sha256"
sha256sum -c "aws2azure-${VERSION}-${ARCH}.tar.gz.sha256"
tar -xzf "aws2azure-${VERSION}-${ARCH}.tar.gz"
cd "aws2azure-${VERSION}-${ARCH}"

AWS2AZURE_CONFIG_FILE=config.example.json \
ASPNETCORE_URLS=http://localhost:8080 \
./aws2azure
```

The archive bundles the `aws2azure` binary, a `config.example.json` to copy and
edit, the README, and the license. No .NET runtime install is required.

---

## Deploying to Kubernetes (Helm)

A Helm chart lives at [`deploy/helm/aws2azure`](../deploy/helm/aws2azure). It
deploys the proxy with production-friendly defaults: non-root chiseled image,
read-only root filesystem, `/health` + `/ready` probes, the config sourced from
a `Secret` (chart-managed or your own), and optional Ingress, HPA, PodDisruption
Budget, and a Prometheus-Operator `ServiceMonitor` scraping `/_aws2azure/metrics`.

```bash
helm install aws2azure ./deploy/helm/aws2azure -f my-values.yaml
helm test aws2azure
```

Because AWS SDKs route by `Host` header, expose each service on a host that
starts with its name (`s3.*`, `sqs.*`, …). See the
[chart README](../deploy/helm/aws2azure/README.md) for the full values reference,
the config-as-Secret options, and Ingress host routing.

---

## Running as a sidecar

The first-class deployment shape is a **sidecar**: aws2azure runs as an extra
container in your app's pod, and the app reaches it over the pod's shared
loopback. AWS SDKs route by `Host` header, so the app sets `AWS_ENDPOINT_URL` to
a service-prefixed host mapped to loopback via `hostAliases` (e.g.
`http://s3.localhost:8080`). No standalone Service, no per-language library.

Try the self-contained end-to-end demo (app + sidecar + Azurite, no Azure
account needed):

```bash
kubectl apply -f deploy/sidecar/demo-azurite.yaml
kubectl logs -f pod/aws2azure-sidecar-demo -c demo   # watch an S3 round-trip via Azure Blob
kubectl delete -f deploy/sidecar/demo-azurite.yaml
```

For real workloads, the [`deploy/sidecar/`](../deploy/sidecar) manifests cover a
production Deployment (native sidecar + startup ordering), a minimal Pod, and
the credential `Secret`. The **[sidecar deployment guide](./deployment/sidecar.md)**
documents resource requests/limits (justified by the measured footprint),
minimal images via [module selection](./deployment/module-selection.md),
readiness/restart semantics, and secret delivery.

---

## Building and running from source

```bash
# Build + run with the .NET SDK
AWS2AZURE_CONFIG_FILE=docker/config.json \
ASPNETCORE_URLS=http://localhost:8080 \
dotnet run --project src/Aws2Azure.Proxy

# Or produce the self-contained Native-AOT binary
dotnet publish src/Aws2Azure.Proxy -c Release -r linux-x64
```

---

## Pointing at real Azure

Replace the emulator values in each binding's `azure` backends with real Azure
values:

- **Blob (`kind: blob`):** set `target.accountName` and `auth.key` (the account
  key) and drop `target.endpoint` (the proxy uses the public
  `https://<accountName>.blob.core.windows.net` endpoint).
- **Cosmos (`kind: cosmos`):** set `target.endpoint` to your account URI,
  `auth.mode: "sharedKey"` + `auth.key` to an account key (or use an AAD mode —
  see below), and `target.databaseName` to a database that already exists.
- **Service Bus / Service Bus Topics / Event Hubs:** set `target.namespace` to
  the real namespace and supply `auth.mode: "sas"` with `auth.keyName` +
  `auth.key` (or an AAD mode); drop the emulator `target.endpoint` /
  `target.managementEndpoint` overrides.

The AWS-facing `aws.accessKeyId` / `aws.secretAccessKey` are arbitrary values you
choose — they are the identity your clients sign with and are independent of any
real AWS account.

### Authenticating without a stored secret

On Azure compute you can drop the long-lived `clientSecret` and let the proxy
acquire short-lived tokens via **Managed Identity** (IMDS) or **AKS Workload
Identity** instead. Each AAD-capable backend's `auth` block takes a `mode`
(`clientSecret` / `managedIdentity` / `workloadIdentity`), and a shared identity
can be named once in a top-level `azureIdentities` pool. See
[Azure authentication: Managed Identity & Workload Identity](./azure-authentication.md)
for per-scenario config, required RBAC roles, and the startup-validation rules.

---

## Operational endpoints

| Path | Purpose |
|---|---|
| `GET /health` | Liveness probe (`{"status":"healthy"}`). |
| `GET /ready` | Readiness probe — `200` when credentials are present and at least one service is enabled, else `503`. |
| `GET /_aws2azure/metrics` | Prometheus metrics (request/error/latency per service, plus runtime gauges). |
| `GET /_aws2azure/modules` | Enabled service modules. |
| `GET /_aws2azure/capabilities` | Per-operation capability matrix (generated from the gap docs). |

---

## Where coverage is documented

aws2azure is **not** a 100% reimplementation of AWS. Every operation and
sub-feature has a gap doc under [`docs/gaps/`](./gaps/) describing its status and
any behavioral differences. The rendered coverage matrix is published from
[`docs/site/`](./site/). When you hit a gap, check the relevant gap doc first —
it is the single source of truth.
