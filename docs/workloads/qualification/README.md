# Reviewed workload qualification policies

This directory is reserved for profile-owned, reviewed real-Azure SLO policies.
Issue #552 provides the common policy loader, repeated-run generator, immutable
provenance, operational evidence, workflow, and trend format. Profile
certification issues add `<profile>.yaml` only after production-shaped evidence
supports defensible throughput/latency thresholds.

A policy must exactly match the workload manifest's `required_scenarios`, set
freshness/sample/duration/failure/repeated-run rules, pin the reviewed
`load_shape` concurrency and duration, and include at least one blocking
`backend_capacity` throughput or latency signal. Thresholds are never copied
from emulator regression baselines or specialized A/B experiments.

An initial reviewed policy may mark that blocking signal with
`threshold_status: unresolved` and provide a non-empty `threshold_reason`.
Qualification records the live measurement as report-only and emits a blocking
`signal_threshold_unresolved` finding. Replace it with a reviewed `min_value` or
`max_value` only after the required comparable production-shaped runs exist.

Until a policy and repeated live-Azure load artifacts exist, the profile remains
`candidate`; rerunning the same evidence or weakening the policy is not a
resolution.
