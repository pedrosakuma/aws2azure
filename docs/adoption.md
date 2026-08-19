# Shortest-path adoption checklist

The shortest safe path is **manifest → compatibility → staging → go/no-go**.
It is workload-specific; module availability alone is never approval.

## 1. Write the workload manifest

Inventory the application, owner, exact AWS SDK/runtime, endpoint/addressing
style, every operation (including startup/cleanup/admin paths), required
sub-features, request sizes, concurrency, retries, idempotency, consistency,
ordering, transactions, and SLOs. Select the closest versioned
[workload profile](workloads/README.md), or keep an equivalent review record
when no profile matches.

**Stop:** an unknown required operation or semantic assumption is not ready for
compatibility review.

## 2. Check current compatibility

1. Read [live workload certification](site/workload-ga.md) first and record its
   exact evaluation instant and input/evaluator identities.
2. Check the [workload compatibility guide](site/workload-compatibility.md).
3. Verify every operation/sub-feature in [operation coverage](site/coverage.md)
   and accept or mitigate each linked [design gap](site/design-gaps.md).
4. Check [real-Azure evidence](site/divergences.md).

Live certification is the current authority. Profile manifests and gap docs
are normative inputs; release notes are historical; guides are explanatory.
A current `candidate`, `conditional`, or `blocked` verdict overrides an older
GA claim.

**Stop:** any required unsupported/stub operation or unaccepted semantic gap is
no-go.

## 3. Build a production-shaped staging plan

- Start from the schema-validated
  [production examples](configuration-examples.md), select least-privilege
  identity/RBAC, and retain immutable artifact/config checksums.
- Match production topology, Azure region/SKU/capacity, identity mode, network,
  TLS, SDK settings, payloads, concurrency, and retry budgets.
- Exercise every required operation plus auth failure, throttling, timeout,
  restart, credential rotation, and exact-prior rollback.
- Compare workload SLOs and business invariants with proxy and Azure telemetry.

Emulators are regression aids only. They do not prove Azure authorization,
throttling, consistency, capacity, or identity behavior.

**Stop:** staging evidence tied to different bytes/config/topology, or stale at
the live authority instant, is not adoption evidence.

## 4. Record go/no-go

Record the immutable candidate and rollback target, config checksum, workload
manifest/profile, authority metadata, accepted gaps, staging results, SLO and
rollback thresholds, owners, observation window, and exception expiry.

**Go** only when every required behavior is supported or explicitly accepted,
real-Azure staging meets workload gates, identity/RBAC/TLS/observability pass
review, rotation and rollback succeed, and current authority does not block the
workload.

**No-go** on an unsupported dependency, emulator-only proof, missed SLO,
unresolved authorization/TLS/observability risk, unavailable exact rollback
target, stale evidence, or a current blocking verdict.

Continue with the full [production runbook](deployment/production-runbook.md)
for canary, alerting, rotation, rollback, and incident procedures.
