# aws2azure vX.Y.Z

> **Release status:** Stable | Alpha | Beta | Release candidate
> **Support window:** Current minor `X.Y`; previous supported minor `X.(Y-1)` or `None`

Replace every `vX.Y.Z` placeholder, including documentation-link tags, before
publishing.

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
| Configuration | old schema | new schema | Compatible / Migration required | Exact non-secret edit |
| Published manifests/schemas | accepted/emitted versions | accepted/emitted versions | Compatible / Migration required | Details |
| Durable persisted formats | inventory rows | same backend | Compatible / Migration required | Details |
| Rolling-upgrade formats | inventory rows | active lifetime | Compatible / Drain required | Details |

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
