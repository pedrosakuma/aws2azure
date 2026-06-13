# Running aws2azure as a sidecar

The expected deployment shape for aws2azure is a **sidecar**: it runs as an
extra container *in the same pod* as your application, and the app talks to it
over the pod's shared loopback. Point any AWS SDK, the AWS CLI, or boto3 at it
with `AWS_ENDPOINT_URL` — AWS SDKs route by `Host` header, so the endpoint is a
service-prefixed host mapped to loopback via `hostAliases` (e.g.
`http://s3.localhost:8080`; see [Topology](#topology)). Your code keeps calling
AWS APIs while the data lives in Azure, with no per-language library to maintain.

This guide covers the sidecar-specific concerns: topology, resource sizing,
image footprint, startup ordering, and config/secret delivery. For the proxy's
configuration schema see [getting-started.md](../getting-started.md#configuration);
for standalone (non-sidecar) Kubernetes deployment use the
[Helm chart](../../deploy/helm/aws2azure).

Copy-pasteable manifests live in [`deploy/sidecar/`](../../deploy/sidecar/):

| File | What it is |
|------|------------|
| `secret.yaml` | The credential map, delivered as a `Secret`. |
| `deployment.yaml` | Production template — a Deployment with aws2azure as a **native sidecar**. |
| `pod.yaml` | The minimal two-container Pod (simplest illustration). |
| `demo-azurite.yaml` | Self-contained end-to-end demo against the Azurite emulator. |

---

## Try it end-to-end (Azurite, no Azure account)

The demo pod runs Azurite, the aws2azure sidecar, and an AWS CLI container that
performs a full S3 round-trip through the proxy:

```bash
kubectl apply -f deploy/sidecar/demo-azurite.yaml
kubectl logs -f pod/aws2azure-sidecar-demo -c demo
# ... bucket created, object uploaded, listed, downloaded — all S3, served from Azurite.
kubectl delete -f deploy/sidecar/demo-azurite.yaml
```

---

## Topology

```
┌─────────────────────── Pod (shared network namespace) ───────────────────────┐
│                                                                               │
│   ┌─────────────┐    AWS wire protocol     ┌───────────────┐   Azure REST     │
│   │     app     │ ───────────────────────► │   aws2azure   │ ───────────────► Azure
│   │             │ http://s3.localhost:8080 │   (sidecar)   │  (Blob, Service  │
│   └─────────────┘                          └───────────────┘   Bus, Cosmos…)  │
│       AWS_ENDPOINT_URL=http://s3.localhost:8080                                │
│       hostAliases: s3.localhost → 127.0.0.1                                    │
└───────────────────────────────────────────────────────────────────────────────┘
```

AWS SDKs route by the **`Host` header**: the proxy dispatches to the S3 module
only when the host starts with `s3.` (likewise `sqs.`, `dynamodb.`, …). So the
app must reach the sidecar at a service-prefixed host that resolves to the pod's
loopback. Map it with `hostAliases`:

```yaml
spec:
  hostAliases:
    - ip: "127.0.0.1"
      hostnames:
        - s3.localhost        # add sqs.localhost, dynamodb.localhost, … as needed
```

Then wire the app to the sidecar:

```yaml
env:
  - name: AWS_ENDPOINT_URL          # SDK v2 / botocore >= 1.31
    value: http://s3.localhost:8080
  # Multiple services: one per-service var + hostAlias each, e.g.
  #   AWS_ENDPOINT_URL_S3:  http://s3.localhost:8080
  #   AWS_ENDPOINT_URL_SQS: http://sqs.localhost:8080
  - name: AWS_ACCESS_KEY_ID         # validated against the proxy's credential map
    value: AKIADEVEXAMPLE
  - name: AWS_SECRET_ACCESS_KEY
    value: change-me
  - name: AWS_REGION
    value: us-east-1
```

**S3 addressing style:** the proxy supports **path-style** S3 only
(virtual-hosted requests are rejected with an explicit S3 error). Configure
path-style in the app — a config/env setting, not a code change:
`aws configure set default.s3.addressing_style path` for the CLI, or
`Config(s3={"addressing_style": "path"})` for boto3.

The app still presents AWS credentials: the proxy validates the SigV4 signature
against its credential map and maps that access key to the corresponding Azure
credentials. The AWS secret never reaches Azure.

---

## Resource requests and limits

A sidecar with no limits — or limits set too low — is an adoption hazard, so set
them deliberately from the [measured footprint](../perf/README.md#footprint-budget-271):

| Build | Idle RSS | AOT binary |
|-------|----------|-----------|
| all modules | ~34 MB | ~22 MB |
| single module (e.g. `s3`) | ~27 MB | ~16 MB |

Recommended starting point (in the manifests):

```yaml
resources:
  requests:
    cpu: 50m       # near-idle proxying is cheap; raise under load
    memory: 64Mi   # covers idle RSS (~34 MB) with headroom
  limits:
    memory: 128Mi  # burst headroom; the proxy uses pooled buffers, not per-request heap
    # No CPU limit: a CPU limit throttles the request path under burst. Prefer a
    # CPU *request* for scheduling and leave the limit unset (or generous).
```

How to tune:

- **Memory** scales mainly with concurrency and payload size (pooled buffers),
  not with idle module count. Watch `container_memory_working_set_bytes` and the
  proxy's own `/_aws2azure/metrics`; raise the limit if you see OOMKills under
  peak payload sizes.
- **CPU**: the AOT hot path is allocation-light. Start with a request only; add a
  limit only if you must cap noisy-neighbor cost, and size it well above observed
  p99 to avoid throttling latency-sensitive calls.
- Numbers above are **runner-bound** (see the footprint caveat); measure on your
  own nodes before committing tight limits.

---

## Minimal image

The published image is a chiseled (distroless) .NET runtime-deps base carrying
only the single Native-AOT binary — no shell, no package manager, non-root
(`$APP_UID=1654`). Its size is measured and regression-gated by the
[footprint gate](../perf/README.md#footprint-budget-271).

To shrink it further for a single-purpose sidecar, compile in only the modules
the workload uses (see [build-time module selection](./module-selection.md)):

```bash
docker build --build-arg MODULES=s3 -t my-registry/aws2azure:s3 .
```

A single-module build trims ~20–27% off the binary and ~20% off idle RSS.

---

## Startup ordering and readiness

Two classic sidecar races: the app issuing requests before the proxy is up, and
the proxy being killed while the app is still draining. The production template
(`deployment.yaml`) avoids both with a **native sidecar** — an `initContainer`
with `restartPolicy: Always` (Kubernetes ≥ 1.29, GA in 1.33):

- Native sidecars start **before** the app containers and are torn down
  **after** them, so the proxy is up before the first request and outlives the
  app's drain.
- A `startupProbe` on `/ready` gates startup until the proxy is actually serving.

```yaml
initContainers:
  - name: aws2azure
    image: ghcr.io/pedrosakuma/aws2azure:latest
    restartPolicy: Always         # promotes this initContainer to a sidecar
    startupProbe:
      httpGet: { path: /ready, port: aws2azure }
      periodSeconds: 1
      failureThreshold: 30
```

**Older clusters (< 1.29):** move the container from `initContainers` to
`containers` (a plain second container) and keep a `startupProbe` on the **app**
targeting port `8080`. You then get best-effort ordering instead of guaranteed
ordering — the app may briefly see connection refusals until the proxy binds.

### Health endpoints

| Path | Meaning |
|------|---------|
| `/health` | Liveness — `200` once the process is up. |
| `/ready` | Readiness — `200` only when ≥ 1 service is enabled **and** a credential is configured; `503` otherwise. |

Use `/ready` for the startupProbe/readinessProbe and `/health` for the
livenessProbe. Native AOT cold start is ~70 ms, so readiness is near-instant.

### Restart semantics

The sidecar shares the pod's lifetime. If the proxy container crashes, a native
sidecar (or a plain container) is restarted in place by the kubelet; the app
container is unaffected but will see failed AWS calls until the proxy is back —
the app should treat proxy calls with the same retry/backoff it would apply to
AWS itself (the AWS SDKs already do).

---

## Config and secrets in a sidecar

The proxy reads its config (the service toggles **and** the credential map) from
the file named by `AWS2AZURE_CONFIG_FILE`. Because the credential map contains
Azure account/SAS keys, deliver it as a **`Secret`**, never a `ConfigMap`:

```bash
kubectl apply -f deploy/sidecar/secret.yaml      # edit the Azure values first
kubectl apply -f deploy/sidecar/deployment.yaml
```

The Secret is mounted read-only at `/etc/aws2azure` and pointed at via
`AWS2AZURE_CONFIG_FILE=/etc/aws2azure/config.json`.

**Security note:** the secret lives in the pod. Anyone who can read the Secret,
exec into the pod, or read the proxy's memory can recover the Azure
credentials. Scope RBAC to the namespace, prefer
[Azure Workload Identity](../azure-authentication.md) over stored account keys
where the backend supports it, and treat the proxy's pod as a credential
boundary.
