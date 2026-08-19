# Configuration and adoption troubleshooting

Start with the exact artifact, configuration checksum, process environment,
caller endpoint/Host, AWS-native error code, UTC time window, and current
workload-certification identity. Do not paste signing secrets, Azure keys,
client secrets, federated tokens, or complete signed requests into logs or
tickets.

## Startup validation

**Symptoms:** process exits `1`; stderr starts with `Configuration is invalid:`
or reports invalid JSON and a JSON path.

1. Run `dotnet run --project tools/Aws2Azure.ConfigSchema -- --check` to confirm
   generated schema/reference drift is not involved.
2. Validate the authored JSON against
   [`config.schema.json`](../config.schema.json). The schema rejects unknown
   fields and invalid backend/auth combinations.
3. Read every accumulated startup error. Paths use the authoring vocabulary,
   such as `bindings[0].azure.dynamodb.auth.clientId`.
4. Check `AWS2AZURE_CONFIG_FILE`, file permissions, mounted-secret revision,
   and recognized [`AWS2AZURE__` overrides](configuration-environment.md).
5. Restart after a change. Configuration and credentials do not hot reload.

The schema is the canonical authoring profile. Runtime reads preserve v1
case-insensitive/unknown-member compatibility, but that is not permission to
author new legacy-shaped files.

## Host routing and probes

**Symptom:** `404` with `aws2azure: no service module matched host ...`.

- The request `Host` must match the module's accepted AWS endpoint forms.
  SQS, DynamoDB, Kinesis, and Secrets Manager accept `<service>.`,
  `<service>-`, or the bare service name; SNS accepts `sns.` or bare `sns`.
  S3 accepts path-style `s3.`, `s3-`, or bare `s3`, and recognizes
  `<bucket>.s3.`/`<bucket>.s3-` as virtual-hosted routing.
- Preserve that Host through ingress/proxy layers. Point the AWS SDK endpoint at
  a service-prefixed DNS name resolving to aws2azure.
- Configure S3 for path-style addressing. Although the router recognizes a
  preserved virtual-hosted S3 Host, v1 returns
  `VirtualHostedStyleNotSupported` rather than dispatching that request.
- Confirm the module is compiled with `/_aws2azure/modules` and enabled in
  `services.<service>.enabled`.

Call `/health`, `/ready`, and internal endpoints with a neutral Host such as
`localhost`. A service-prefixed Host deliberately routes as AWS traffic and
probe routes return `404`.

## SigV4 and AWS-facing `403`

Read the AWS-native code before assuming Azure RBAC:

- `InvalidAccessKeyId`/unknown access key means no exact
  `bindings[].aws.accessKeyId` matched.
- `SignatureDoesNotMatch` in XML means the matching binding secret, signed
  Host/path, signed headers/query, body hash, or credential scope differs.
- Clock skew has a separate protocol-specific code: XML services return
  `RequestTimeTooSkewed`; AWS-JSON services return
  `InvalidSignatureException` (with service-specific HTTP status).
- For presigned S3 URLs, preserve the signed Host or configure the narrow
  `presignedTrustedSigningHosts` allowlist documented in
  [Presigned URLs](presigned-urls.md).

Reproduce with the same SDK, endpoint, addressing style, region, request body,
and time source. Never "fix" SigV4 by bypassing authentication.

## Azure authentication and RBAC

**Symptoms:** AWS-native access denied, Azure `401`/`403`, or token acquisition
errors after SigV4 succeeds.

1. Identify the selected binding and `auth.mode`.
2. For workload identity, verify `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and the
   projected `AZURE_FEDERATED_TOKEN_FILE`; for managed identity, verify the
   assigned identity and optional client ID.
3. Check the data-plane role at the actual resource scope. Azure control-plane
   Contributor does not imply Cosmos DB data access.
4. Correlate Azure authorization/activity logs. Allow only the documented role
   propagation window; do not retry arbitrary authorization failures as
   propagation.
5. Confirm secret/reference name case, expiry, and the mounted secret revision.

Use the backend-specific role table in
[Azure authentication](azure-authentication.md#required-azure-rbac).

## TLS and connectivity

- `AWS2AZURE_INSECURE_TLS=1` is an emulator-only bypass for outbound
  certificates. Remove it from production and fix the trust chain, endpoint,
  DNS, firewall, or private-link configuration.
- Use HTTPS between workload and proxy when traffic leaves a trusted
  loopback/shared namespace.
- Separate DNS/connect/TLS latency from Azure server latency using proxy and
  Azure telemetry. A successful `/ready` does not prove outbound connectivity.
- Check required Entra authority access in addition to the Azure data endpoint
  for token-based auth.

## Readiness is not backend health

`/health` proves only that the process serves HTTP. `/ready` returns `200` when
at least one compiled module is enabled and at least one binding loaded. It does
not acquire a token, resolve DNS, call Azure, prove RBAC, or validate capacity.

If `/ready` is `503`, inspect its `services`, `hasCredentials`, and
`moduleCount`. If it is `200` while workload calls fail, run a representative
AWS operation and inspect backend/auth/network evidence.

## Throttling, timeouts, and `503`

- For `429`, compare the AWS-native error, Azure throttle/quota metrics,
  partition/hot-key distribution, request bursts, and SDK retry mode. Use
  bounded jittered retries only where operation idempotency permits.
- For timeout/`503`, compare end-to-end, module, and accumulated backend
  duration with Azure service latency and platform saturation. Accumulated
  parallel backend time is not proxy wall-clock overhead.
- Reduce concurrency or hot partitions, provision the correct Azure capacity,
  and re-run production-shaped staging. Do not raise a gate merely to accept a
  failed run.

## Stale workload evidence

Current adoption authority has this precedence:

1. generated live workload certification;
2. workload profile manifests;
3. gap docs;
4. immutable historical release notes;
5. explanatory guides, including this page.

On [live workload certification](site/workload-ga.md), verify
`evaluated_as_of_utc`, canonical-input revision, evaluator implementation
revision, and the workload verdict. The evaluation instant is exact: every
evidence/runtime/approval/rollback/revocation cutoff is evaluated at that same
UTC instant. A later `candidate`, `conditional`, or `blocked` verdict overrides
a historical GA release claim.

Treat missing, expired, stale, identity-mismatched, or insufficient evidence as
`candidate`/no-go until the authoritative pipeline produces reviewed current
evidence. Do not substitute an emulator run, PR build, rebuilt binary, similar
timestamp, or release-note statement for the exact sealed runtime and evidence
identity.

## Escalation record

Preserve the native AWS response, sanitized proxy startup/request logs, relevant
proxy metrics, Azure telemetry, exact artifact/config digests, binding/service
path, topology, workload profile, authority metadata, and a minimal
reproduction. Compare candidate and stable cohorts under equivalent conditions
before classifying a regression.
