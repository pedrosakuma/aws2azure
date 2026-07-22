# aws2azure vX.Y.Z

> **Release status:** Stable | Alpha | Beta | Release candidate
> **Support window:** Current minor `X.Y`; previous supported minor `X.(Y-1)` or `None`

Replace every `vX.Y.Z` placeholder, including documentation-link tags, before
announcing or treating the release as supported.

> **Automation note:** Stable `v1+` releases use the immutable RC promotion
> workflow and a completed notes file derived from this template. The promotion
> gate rejects placeholders, rebuilds, and existing stable identities. Do not
> announce or mark a release supported until every mandatory section is
> complete.

## Artifacts and provenance

| Artifact | Platform | Immutable digest/checksum | Provenance/attestation |
|---|---|---|---|
| Native AOT binary | linux-x64 | `sha256:...` | URL |
| Native AOT binary | linux-arm64 | `sha256:...` | URL |
| Container | multi-arch | `sha256:...` | URL |

## Supported workload profiles

This matrix announces workload-scoped compatibility, **not full AWS service
parity**.

| Profile | Profile version | Status | Minimum proxy | Azure target/topology | Qualification and approved-runtime evidence | Required accepted gaps |
|---|---:|---|---|---|---|---|
| `profile-id` | 1 | GA / conditional / candidate | `vX.Y.Z` | Description | URLs | Links or `None` |

## Upgrade and rollback compatibility

| Contract | From | To | Status | Operator action/evidence |
|---|---|---|---|---|
| In-place upgrade | `v...` | `vX.Y.Z` | Supported / Unsupported | Steps or `None` |
| Mixed-version rollout | `v...` + `vX.Y.Z` | Same backend | Supported / Unsupported | Maximum coexistence/drain period |
| Rollback | `vX.Y.Z` | `v...` | Supported / Unsupported | Candidate-write/previous-read evidence |
| Configuration/startup environment | old schema and controls | new schema and controls | Compatible / Migration required | Exact non-secret edit |
| Published manifests/schemas | accepted/emitted versions | accepted/emitted versions | Compatible / Migration required | Details |
| Durable persisted formats | inventory rows | same backend | Compatible / Migration required | Details |
| Rolling-upgrade formats | inventory rows | active lifetime | Compatible / Drain required | Details |

## Persisted-format compatibility

- DynamoDB persisted-format contract: inventory `v1`, `sha256:<64 lowercase hex>`.
- Changes from previous supported release: None / describe every changed format,
  writer version, reader span, stored-procedure identity, and operator action.
- Adjacent-runtime validation: candidate-write/previous-read and
  previous-write/candidate-read evidence URL, or `None` with justification.
- Historical incompatible-state export/import: Not required / required, with the
  runbook record and write-freeze or reverse-synchronization evidence.

## Changes

### Added

- None.

### Changed

- None.

### Fixed

- None.

### Security

- None.

## Deprecations and removals

| Contract | Status | Replacement | First deprecated release/date | Earliest removal | Migration and rollback |
|---|---|---|---|---|---|
| None | — | — | — | — | — |

## Known incompatibilities and operator actions

- None.

## Evidence and documentation

- [Versioning and compatibility policy](https://github.com/pedrosakuma/aws2azure/blob/vX.Y.Z/docs/versioning-and-compatibility.md)
- [Project maturity and support terms](https://github.com/pedrosakuma/aws2azure/blob/vX.Y.Z/docs/project-maturity.md)
- [Workload compatibility](https://github.com/pedrosakuma/aws2azure/blob/vX.Y.Z/docs/site/workload-compatibility.md)
- [Coverage matrix](https://github.com/pedrosakuma/aws2azure/blob/vX.Y.Z/docs/site/coverage.md)
- [Production runbook](https://github.com/pedrosakuma/aws2azure/blob/vX.Y.Z/docs/deployment/production-runbook.md)
