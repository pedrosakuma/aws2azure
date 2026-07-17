# Reviewed workload qualification policies

This directory is reserved for profile-owned, reviewed real-Azure SLO policies.
Issue #552 provides the common policy loader, repeated-run generator, immutable
provenance, operational evidence, workflow, and trend format. Profile
certification issues add `<profile>.yaml` only after production-shaped evidence
supports defensible throughput/latency thresholds.

A policy must exactly match the workload manifest's `required_scenarios`, set
freshness/sample/duration/failure/repeated-run rules, and include at least one
blocking `backend_capacity` throughput or latency signal. Thresholds are never
copied from emulator regression baselines or specialized A/B experiments.

Until a policy and repeated live-Azure load artifacts exist, the profile remains
`candidate`; rerunning the same evidence or weakening the policy is not a
resolution.
