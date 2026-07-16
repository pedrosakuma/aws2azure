# aws2azure

> A transparent HTTP proxy that accepts requests in the **AWS wire protocol**
> (SigV4 + service-specific payloads) and translates them into equivalent calls
> against **Azure REST APIs**.

Point any application that uses the official AWS SDK — in **any language** — at
this proxy by setting its `endpoint_url`, and it runs against Azure with no code
changes. The goal is to reduce the cost and risk of migrating workloads from AWS
to Azure.

## Quickstart

```bash
git clone https://github.com/pedrosakuma/aws2azure.git
cd aws2azure
docker compose up --build

# In another terminal — S3 against the bundled Azurite emulator:
export AWS_ACCESS_KEY_ID=AKIADEVEXAMPLE
export AWS_SECRET_ACCESS_KEY=dev-secret-key-change-me
export AWS_DEFAULT_REGION=us-east-1

aws --endpoint-url http://s3.localhost:8080 s3 mb s3://demo
aws --endpoint-url http://s3.localhost:8080 s3 cp ./README.md s3://demo/readme
aws --endpoint-url http://s3.localhost:8080 s3 ls s3://demo/
```

See the **[Getting Started guide](./docs/getting-started.md)** for configuration,
enabling the other services, running from source, and pointing at real Azure. On
Azure compute you can authenticate without a stored secret via Managed Identity
or Workload Identity — see
[Azure authentication](./docs/azure-authentication.md).

## Scope

- **Direction:** AWS → Azure only (no reverse, no other clouds).
- **Runtime:** .NET 10, Native AOT — a single self-contained binary.
- **Process model:** one binary multiplexing services by Host header / path.
- **Azure integration:** direct REST calls (no Azure SDK dependency).
- **AWS integration:** wire protocol only (no AWS SDK dependency).

## Services

| AWS Service | Azure Target | Module | Compatibility |
|---|---|---|---|
| S3 | Blob Storage | Available | Workload-dependent — [review profile](./docs/site/workload-compatibility.md#s3) |
| SQS | Service Bus | Available | Workload-dependent — [review profile](./docs/site/workload-compatibility.md#sqs) |
| DynamoDB | Cosmos DB (NoSQL API) | Available | Workload-dependent — [review profile](./docs/site/workload-compatibility.md#dynamodb) |
| Kinesis | Event Hubs | Available | Workload-dependent — [review profile](./docs/site/workload-compatibility.md#kinesis) |
| SNS | Service Bus Topics / Event Grid | Available | Workload-dependent — [review profile](./docs/site/workload-compatibility.md#sns) |
| Secrets Manager | Key Vault | Available | Workload-dependent — [review profile](./docs/site/workload-compatibility.md#secretsmanager) |

An available module means the proxy can route that service's AWS wire protocol;
it does **not** imply full AWS service parity. The project-level meanings of
module, operation, real-Azure, conditional, and GA claims are defined in
[**Project maturity and support terms**](./docs/project-maturity.md). Start with
the generated
[**workload compatibility guide**](./docs/site/workload-compatibility.md), then
confirm every operation in the [coverage matrix](./docs/site/coverage.md).
Every operation and sub-feature is documented exhaustively under
[`docs/gaps/`](./docs/gaps/). Cross-cutting limitations — consistency model,
transaction scope, absent control-plane surfaces — are collected in
[**Design gaps**](./docs/site/design-gaps.md).

Architecture decisions that explain the project constraints are recorded in
[`docs/adr/`](./docs/adr/).

## Documentation

Start broad, then drill down — the map below is layered so you only open what you
need:

- **Get running:** [Getting Started](./docs/getting-started.md) ·
  [Azure authentication](./docs/azure-authentication.md) ·
  [Presigned URLs](./docs/presigned-urls.md)
- **What works & what doesn't:**
  [workload compatibility](./docs/site/workload-compatibility.md) (go/no-go) →
  [coverage matrix](./docs/site/coverage.md) (every operation) →
  per-service pages under [`docs/site/`](./docs/site/) (operation detail) →
  [design gaps](./docs/site/design-gaps.md) (architectural limits) →
  [real-Azure divergences](./docs/site/divergences.md) (verification state)
- **Why it's built this way:** [Architecture Decision Records](./docs/adr/)
- **Run it in production:** [production runbook](./docs/deployment/production-runbook.md)
  (qualification, go/no-go, canary, incidents, rollback) ·
  [sidecar deployment](./docs/deployment/sidecar.md) ·
  [build-time module selection](./docs/deployment/module-selection.md)
- **Performance & testing:** [perf baseline](./docs/perf/README.md) ·
  [nightly real-Azure tests](./docs/testing/real-azure-nightly.md)

The gap docs render into a browsable site via `mkdocs serve` (config in
[`mkdocs.yml`](./mkdocs.yml)); they are the single source of truth, validated and
regenerated in CI.

## Configuration

The proxy reads a JSON config file pointed to by `AWS2AZURE_CONFIG_FILE`. It is
organized around **bindings**: each binding maps one AWS identity (the access
key your clients sign with) to a set of Azure backends, where every backend
splits non-secret topology (`target`) from its secret (`auth`). The proxy
validates the incoming SigV4 signature, then calls Azure with the matching
backend's credentials. See [`docker/config.json`](./docker/config.json) for an
example and the [Getting Started guide](./docs/getting-started.md#configuration)
for the full schema.

## Operational endpoints

- `GET /health` — liveness probe.
- `GET /ready` — readiness probe.
- `GET /_aws2azure/metrics` — Prometheus metrics.

## Sidecar footprint

aws2azure is meant to run as a **sidecar**, so its resource cost is a
first-class concern. On a `linux-x64` runner the all-modules Native-AOT build
measures roughly:

| AOT binary | Idle RSS | Cold start |
|------------|----------|------------|
| ~22 MB     | ~34 MB   | ~70 ms     |

These are **measured and regression-gated** in CI — see
[`docs/perf/README.md` (footprint budget)](./docs/perf/README.md#footprint-budget-271)
for the harness, the live numbers, and the per-metric budget. Footprint numbers
are runner-bound; binary size is deterministic while cold start scales with
runner core count.

For an even leaner sidecar you can compile in only the service modules a
workload needs (e.g. `-p:Modules=s3`), trimming ~20–27% off the binary and idle
RSS — see
[**build-time module selection**](./docs/deployment/module-selection.md).

Ready-to-use sidecar manifests (native-sidecar Deployment, minimal Pod, and an
end-to-end Azurite demo) live in [`deploy/sidecar/`](./deploy/sidecar); the
[**sidecar deployment guide**](./docs/deployment/sidecar.md) covers resource
limits, readiness/ordering, and secret delivery.

## Non-Goals

- Not a 100% feature-compatible reimplementation. Gaps are **documented
  exhaustively** per operation/sub-feature.
- Not a control-plane / IaC tool (use Terraform/Crossplane for that).
- Not a fork or repackaging of existing projects (s3proxy, MinIO, etc.) — built
  from scratch.
- No reverse direction (Azure → AWS) and no other clouds.

## Project Tracking

The pinned **[Project Roadmap](https://github.com/pedrosakuma/aws2azure/issues/16)**
is the durable source of truth for scope and completed phases. Active
production-maturity and workload-GA work is tracked in
**[#542](https://github.com/pedrosakuma/aws2azure/issues/542)**. See
[Project maturity and support terms](./docs/project-maturity.md) for the
governance model and normative terminology. All work is tracked through GitHub
issues and milestones.

## License

MIT — see [LICENSE](./LICENSE).
