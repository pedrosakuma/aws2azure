# Secrets Manager basic lifecycle profile

This version 1 profile covers `CreateSecret`, `DescribeSecret`,
`GetSecretValue`, `PutSecretValue`, `UpdateSecret`, `ListSecrets`, and
`DeleteSecret` against Azure Key Vault.

Its current generated verdict is `candidate` because the previously reviewed
real-Azure qualification evidence is stale. Check
[workload GA certification](../site/workload-ga.md) before adoption; historical
qualification and approved-runtime records do not override the live verdict.

## Required deployment contract

- Configure Entra authentication and grant the binding's identity the required
  Key Vault data-plane permissions: `Key Vault Secrets Officer` for this
  read/write/delete profile, or the legacy access-policy permissions
  `secrets: get/set/list/delete`. `Key Vault Secrets User` is read-only, and
  `Key Vault Reader` / `Key Vault Contributor` do not grant secret-value
  access.
- Configure Key Vault soft-delete retention for the intended recovery posture.
  `RecoveryWindowInDays` and `ForceDeleteWithoutRecovery` cannot override vault
  policy, purge protection, or missing purge permission.
- Treat proxy-owned `aws2azure-*` version tags as reserved implementation data.
  Out-of-band edits to those tags are unsupported.
- Use a single writer per secret when the application requires ordering
  stronger than Key Vault can provide without an external coordinator.

## Version and stage semantics

Key Vault versions and tags model AWS version stages. `PutSecretValue` accepts
the profile's documented partial semantics: version creation, inventory, and
stage-tag updates are not one transaction. Contended cross-instance writes can
return `ResourceExistsException`; callers should retry or read after propagation
settles.

Before adoption, exercise token idempotency, rapid successive writes, custom
stage movement, restart replay, credential rotation, pagination, deletion and
purge permissions, throttling, timeout, cancellation, and rollback against the
exact vault configuration.

Lambda-driven `RotateSecret`, resource policies, and cross-account sharing are
outside this profile. Run rotation through an external Azure Function or
pipeline that writes the new version through the proxy.

## Adoption decision

Adopt only when the generated profile verdict is acceptable and fresh
real-Azure evidence exists for the release and topology being deployed. Review
the accepted deletion-recovery and version-stage design gaps in the generated
[Secrets Manager capability page](../site/secretsmanager.md).

## Report-only operation timing diagnostics

The lifecycle qualification and RC observation/calibration harnesses also write
`<evidence-file>.<candidate|stable>.operation-timings.json` beside their existing
JSON evidence. These independent schema-version-1 artifacts have
`artifact_kind: operation_timing_diagnostics` and `promotable: false`.
They are not qualification inputs, calibration recommendations, or changes to
any release decision, floor, ceiling, or baseline. Existing strict evidence
schemas and their consumers are unchanged. The existing raw-artifact directory
uploads retain the sidecars, including the separate candidate/stable cohort
uploads; the dedicated calibration-report upload still contains only its
authoritative report. Consult the **raw capture** upload for calibration timings.
No additional workflow dispatch or Azure resources are needed.

Each worker still runs the serial nine-action schedule:
`Create → Describe → Get → Put → Get → Update → Get → List → Delete`.
Thus `GetSecretValue` successes/second measures **mixed lifecycle throughput**,
not isolated GET capacity. Compare reports only with their concurrency, runtime
digest, schedule, elapsed duration and deployment context; a historical
qualification floor is not a universal capacity claim.

### Measurement contract

- A sample is one **completed client action**, including SDK retries, response
  checks and, for GET, expected-value polling. Attempts equal successes plus
  errors; they are not HTTP request counts or downstream Key Vault attempts.
  Throttles count actions ending in a throttle error, not internally retried
  throttles. Latencies include both successful and failed actions.
- `lifecycle` covers the actual monotonic worker wall-clock interval, including
  draining iterations and time spent on fallback cleanup. `measurement` covers
  the shorter of requested duration and actual elapsed time; `drain` covers
  the remainder. Counts are assigned by **completion time**: an action crossing
  the cutoff contributes its entire latency to drain. Measurement and drain
  partition lifecycle counts and cumulative latency; window rows overlap
  measurement and must not be added to those totals.
- TPS uses each row's `duration_seconds`, not summed latency or configured
  duration when a run ends early. Zero-duration rates and empty-sample
  percentiles/means/error rates are `null`. Cumulative milliseconds sum client
  action time across workers and may exceed elapsed wall-clock time.
- Fallback `DeleteSecret` after an unsuccessful lifecycle is recorded only as
  `cleanup`, not as a legacy gate completion/failure. Cleanup can overlap
  measurement and drain on different workers, so its rate denominator is the
  whole worker wall-clock interval, **not** exclusive cleanup time. Normal
  lifecycle Delete remains a lifecycle action.
- There is no explicit warmup (`warmup: not_performed`). Setup, network probes,
  canary setup/restoration/teardown, credential rotation, synchronization wait,
  and post-load qualification scenarios are excluded. Diagnostics do not
  introduce a readiness barrier or alter existing synchronization. Split-cohort
  reports distinguish actual `started_at_utc` from `scheduled_start_utc`;
  timestamp offsets and rates use the actual start and monotonic clock.
- Each operation exports successes, settled attempts, errors, throttles, success
  and attempt TPS, error fraction, exact mean/cumulative latency, and approximate
  p50/p95/p99. Fixed 512-bucket histograms give upper-bound estimates within 5%
  or 1 ms (extreme overflow uses the observed maximum). This approximation
  affects diagnostics only; pre-existing exact gate percentiles are untouched.
- Measurement windows have width `max(60 seconds, requested duration / 60)`,
  at most 60 per operation. Only nonempty windows appear; omitted windows have
  zero completions. The last window's denominator ends at the actual/requested
  cutoff, not the nominal bucket end. Drain and cleanup have aggregate rows.
  Additional collector memory is bounded by operation/window counts (512
  counters per histogram), not request volume. The pre-existing tracker still
  retains its exact latency queue for legacy decisions; this delivery does not
  claim to bound that queue.
- First failures include monotonic offset, derived UTC completion timestamp,
  category, HTTP status and sanitized error-code/type token only. No exception
  messages, secret values/names, request IDs, URLs, or bodies are exported.
  Run identity, harness SHA and runtime digest tie each sidecar to its sibling
  evidence; an empty runtime digest denotes source validation, not a sealed
  candidate identity.

Reports are written after workers settle, before restoration or qualification
checks can fail. `workers_completed: false` marks an interrupted/faulted worker
run, not an accepted observation. A hard process kill can prevent any report;
a reporting failure emits a safe warning without adding a gate. The sidecar does not
prove that its sibling authoritative evidence was produced or accepted.

### Deliberately deferred attribution

These are client-action diagnostics, **not proxy CPU or downstream HTTP traces**.
Per-action Key Vault fan-out, proxy CPU time, server queue time and network time
cannot be inferred from them. Adding that attribution to sealed runtimes would
require separately scoped, opt-in shipping instrumentation and validation.
This first delivery adds no shipping instrumentation or dependencies.
[Delivery 2's isolated seven-operation capacity harness](../perf/secretsmanager-isolated-capacity.md)
is opt-in, finite-budget and report-only; it has offline validation, not an
asserted live-Azure capacity result. Controlled comparisons/readiness
improvements and evidence-calibrated gates remain subsequent deliveries.
