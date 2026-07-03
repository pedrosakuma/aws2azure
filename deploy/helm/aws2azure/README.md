# aws2azure Helm chart

Deploys the [aws2azure](https://github.com/pedrosakuma/aws2azure) proxy — a
transparent AWS-wire-protocol → Azure-REST proxy — to Kubernetes.

## TL;DR

```bash
# 1. Put your real config in a values file (see Configuration below).
helm install aws2azure ./deploy/helm/aws2azure -f my-values.yaml

# 2. Smoke-test the release.
helm test aws2azure
```

The chart is not yet published to a Helm repository; install it from a checkout
of the repo (or `helm package` it yourself).

## Configuration

The proxy reads a single JSON config file (the `AWS2AZURE_CONFIG_FILE`). Because
it contains Azure account keys / secrets, the chart **always** sources it from a
Kubernetes `Secret` — never a ConfigMap. You have two options:

### 1. Let the chart manage the Secret (default)

Provide the config object under `config.content`; the chart serializes it to JSON
and stores it in a generated Secret. A `checksum/config` pod annotation rolls the
Deployment automatically when the config changes.

```yaml
config:
  create: true
  content:
    services:
      s3: { enabled: true }
    bindings:
      - aws:
          accessKeyId: AKIADEVEXAMPLE
          secretAccessKey: a-strong-secret
        azure:
          s3:
            kind: blob
            target:
              accountName: mystorageaccount
            auth:
              mode: sharedKey
              key: "<azure-storage-key>"
```

See the [Configuration reference](../../../docs/getting-started.md#configuration)
for the full schema (SQS/SNS/DynamoDB/Kinesis/Secrets Manager backends, multiple
bindings, etc.).

### 2. Reference a Secret you manage yourself

Keep secrets out of your values / Git by creating the Secret out-of-band (sealed
secrets, External Secrets Operator, CSI driver, …) and pointing the chart at it:

```yaml
config:
  create: false
  existingSecret: aws2azure-config
  existingSecretKey: config.json
```

## Exposing the proxy

AWS SDKs route by the `Host` header, so each AWS service needs a hostname that
starts with its name (`s3.*`, `sqs.*`, `dynamodb.*`, …). With the Ingress
enabled, give each service it own host:

```yaml
ingress:
  enabled: true
  className: nginx
  hosts:
    - host: s3.aws2azure.example.com
      paths: [{ path: /, pathType: Prefix }]
    - host: sqs.aws2azure.example.com
      paths: [{ path: /, pathType: Prefix }]
  tls:
    - secretName: aws2azure-tls
      hosts: [s3.aws2azure.example.com, sqs.aws2azure.example.com]
```

Then point your client's `endpoint_url` at the matching host.

## Observability

The proxy exposes Prometheus metrics at `/_aws2azure/metrics`. With the
Prometheus Operator installed, enable the bundled `ServiceMonitor`:

```yaml
serviceMonitor:
  enabled: true
  labels:
    release: kube-prometheus-stack   # match your Prometheus selector
```

## Security defaults

- Runs as the non-root UID baked into the chiseled image (`1654`).
- `readOnlyRootFilesystem: true`, `allowPrivilegeEscalation: false`, all Linux
  capabilities dropped, `seccompProfile: RuntimeDefault`.
- The `serviceAccount.annotations` field is the hook for Azure Workload Identity.

## Values

| Key | Default | Description |
|---|---|---|
| `replicaCount` | `1` | Replicas (ignored when `autoscaling.enabled`). |
| `image.repository` | `ghcr.io/pedrosakuma/aws2azure` | Image repo. |
| `image.tag` | `""` (chart `appVersion`) | Image tag. |
| `image.pullPolicy` | `IfNotPresent` | Pull policy. |
| `imagePullSecrets` | `[]` | Pull secrets for private registries. |
| `config.create` | `true` | Render `config.content` into a chart-managed Secret. |
| `config.content` | demo S3 config | Proxy config object (serialized to JSON). |
| `config.existingSecret` | `""` | Use a pre-existing Secret (when `create=false`). |
| `config.existingSecretKey` | `config.json` | Key in the existing Secret. |
| `extraEnv` | `[]` | Extra container env vars. |
| `service.type` | `ClusterIP` | Service type. |
| `service.port` | `8080` | Service / container port. |
| `ingress.enabled` | `false` | Create an Ingress. |
| `resources` | 100m/64Mi req, 256Mi limit | Container resources. |
| `autoscaling.enabled` | `false` | Create an HPA. |
| `serviceMonitor.enabled` | `false` | Create a Prometheus-Operator ServiceMonitor. |
| `podDisruptionBudget.enabled` | `false` | Create a PDB. |
| `serviceAccount.create` | `true` | Create a ServiceAccount. |
| `serviceAccount.annotations` | `{}` | SA annotations (Workload Identity). |

See [`values.yaml`](./values.yaml) for the complete list including probes,
security contexts, node selectors, tolerations, affinity and topology spread.
