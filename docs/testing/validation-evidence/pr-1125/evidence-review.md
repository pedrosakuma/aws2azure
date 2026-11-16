# PR #1125 deterministic XML-order evidence review

Status: pending owner approval. This report does not authorize evidence
acceptance, workflow execution, labels, Azure spend, merge, or any change to
PR #1125.

## Exact binding

The reviewed policy tool is `validation-evidence-v1` at policy commit
`ce452480f5144f1779becc64d94c80addf94c096`. It ran from clean Linux/ext4
checkouts with `core.fileMode=true`.

- PR base and merge base:
  `ce452480f5144f1779becc64d94c80addf94c096`
- Base tree: `35970fbe8d978671435dff6d4c447f11a6f4d734`
- Reviewed target:
  `939a835075fdf46d60ee6510c21135210ce5f0ea`
- Reviewed target tree: `7db6d961c99273efae89d04f6b46694dab2f1ecb`
- Prior incomplete fix:
  `fe0347edcead04e991c7cdf07e40f8f3c10a5f2c`
- Prior tree: `88e6492c876ad5f18aaa94f7f17bd47ac2c8189c`
- Binary/full-index target diff SHA256:
  `c2ed0a2b3a1376bfb64fddd5978e983c2cf4dc0186627e479ece78f7f5785a95`
- Original strict plan SHA256:
  `0daee3de7ef733eeb35e895d5e2ed6394d6dc57bed7d49301a5d32869af5157f`
- Serialized strict-plan file SHA256:
  `46413a045f07aefd67c338a898e6dfd35b9478970504801229b4081fe84a676d`
- Serialized review-input file SHA256:
  `6397ad429cbaab3f41e094641ddc928e4512b66a87b9babb92c9f6c1992c994f`

The strict classifier requires `run-integration`, `run-perf`, and
`run-real-azure`. The proposed decision accepts only the `real-azure` gate;
the other required gates, their reasons, commands, labels, required checks,
and branch protection remain unchanged.

## Real-Azure failure history

The owner-authorized source-validation runs were inspected from GitHub:

- [run 43925077640](https://github.com/pedrosakuma/aws2azure/actions/runs/43925077640)
  at source `04a356736b9cc71cb1a426fe6bee3031d4c6131b`;
- [run 43943424124](https://github.com/pedrosakuma/aws2azure/actions/runs/43943424124)
  at source `fe0347edcead04e991c7cdf07e40f8f3c10a5f2c`.

In both runs the planned unit and integration suites, Azure provisioning,
canonical evidence upload, report generation, correctness-candidate
generation, and cleanup/deallocation completed successfully. The shared
happy-path evidence exporter alone failed:
`sns/fifo-throughput-scope-roundtrip` step `set-fifo-throughput-scope`
returned 502 because the Service Bus management API returned HTTP 400. Each
run recorded 5 passed and 1 failed shared evidence case. The first run exposed
the missing XML insertion anchor. The second run proved that handling only
`SupportOrdering` and `AutoDeleteOnIdle` was incomplete.

These failed runs are diagnostic context only. They are not successful live
Azure evidence and are not represented as such by this proposal.

## Exact Service Bus serializer order

The target integration project references
`Azure.Messaging.ServiceBus` `7.21.0`. The exact restored assembly was imported
and decompiled without execution:

- Assembly SHA256:
  `10eab238aeb9ba30e3ddba5886dbc0fc5e3e50e5b6e2dee69ba87c2877e6ca32`
- Module version ID:
  `69b250fa-8bd3-4255-b7c7-2b574927ce95`
- Decompiled type:
  `Azure.Messaging.ServiceBus.Administration.TopicPropertiesExtensions`
- Decompiled method: `Serialize(TopicProperties)`

The canonical serialized order after `ForwardTo` is:

1. `UserMetadata`
2. `SupportOrdering`
3. `AutoDeleteOnIdle`
4. `EnablePartitioning`
5. `EnableSubscriptionPartitioning`
6. `EnableExpress`
7. `MaxMessageSizeInKilobytes`

The target helper inserts missing `UserMetadata` before the first retained
property among all six properties that canonically follow it:
`SupportOrdering`, `AutoDeleteOnIdle`, `EnablePartitioning`,
`EnableSubscriptionPartitioning`, `EnableExpress`, and
`MaxMessageSizeInKilobytes`.

## Focused regression proof

The target regression test models the real-Azure response shape that omitted
`SupportOrdering` and `AutoDeleteOnIdle` while retaining later fields
`EnablePartitioning` and `EnableExpress`:

`Aws2Azure.UnitTests.Sns.SetTopicAttributesHandlerTests.Topic_update_inserts_missing_user_metadata_before_first_later_schema_property`

The exact target test file was run against the prior implementation at
`fe0347edcead04e991c7cdf07e40f8f3c10a5f2c`. It failed at the ordering
assertion with `Expected: True; Actual: False`. The same test passed against
target `939a835075fdf46d60ee6510c21135210ce5f0ea`.

Commands:

```text
dotnet test tests/Aws2Azure.UnitTests/Aws2Azure.UnitTests.csproj -c Release \
  --filter FullyQualifiedName=Aws2Azure.UnitTests.Sns.SetTopicAttributesHandlerTests.Topic_update_inserts_missing_user_metadata_before_first_later_schema_property
```

Prior result: 0 passed, 1 failed. Target result: 1 passed, 0 failed.

## Target validation

Validation ran from the exact clean Linux/ext4 target:

- Release solution build: succeeded with 0 warnings and 0 errors.
- Focused SNS unit tests: 289 passed, 0 failed, 0 skipped.
- Focused SNS conformance tests: 4 passed, 0 failed, 7 explicitly skipped.
- Focused SNS integration discovery: 0 failed; 25 environment-gated tests
  explicitly skipped because no emulator or real-Azure environment was
  supplied.
- `Aws2Azure.GapDocs --validate`: 146 operations, 6 service design docs,
  5 approved-runtime records, 5 RC observation policies, and the real-Azure
  matrix validated.
- `Aws2Azure.Documentation --check`: discovery artifacts are current.

The PR's free integration, conformance, documentation, gap-doc, container
build, footprint, and change-selection checks passed for the exact target.
The performance job remained in progress at final package preparation.

The free CI `build-test` job failed after a successful 0-warning Release build
when 4,569 of 4,570 unit tests passed and the unrelated SecretsManager test
`HandleAsync_DeleteSecret_force_delete_reuses_cached_recovery_level_across_conflict_retries`
timed out waiting for a background purge continuation. A single bounded
diagnostic comparison ran that exact test once on base and once on target
under the same local Linux SDK; both passed in 4 seconds. No workflow was
rerun. The failed required check remains failed and this evidence decision
does not waive, replace, or synthesize it.

## Complete diff review

The complete base-to-target diff contains 45 paths, 1,121 additions, and 132
deletions. It was reviewed as one bound binary/full-index diff, including the
SNS implementation, tests, gap sources, generated capability/site outputs,
authority revision, and documentation discovery outputs. The final target-only
delta from `fe0347edcead04e991c7cdf07e40f8f3c10a5f2c` changes only:

- `src/Aws2Azure.Modules.Sns/Management/ServiceBusAtomXml.cs`;
- `tests/Aws2Azure.UnitTests/Sns/SetTopicAttributesHandlerTests.cs`.

That delta expands the missing-`UserMetadata` insertion anchors to the full
canonical set and changes the focused fixture to reproduce the response shape
that defeated the first fix. No workflow, dependency, deployment, runtime
configuration, performance baseline, or qualification ledger is changed by
the final fix.

`git diff --check` reports one generated Markdown blank line at EOF in
`docs/site/design-gaps/sns/fifo-topics-have-bounded-service-bus-semantics.md`.
It is generated output already bound by GapDocs validation and is not part of
the final two-file fix.

## Scope and limitations

This evidence establishes only deterministic XML serialization ordering for
the exact reviewed target and exact base. It does not establish:

- successful live Azure execution of the target;
- current Azure or Service Bus health;
- renewed workload qualification;
- throughput, latency, or performance qualification;
- authorization to apply labels, trigger or rerun a workflow, spend on Azure,
  merge, or bypass branch protection.

A changed base, merge base, head, diff, classifier plan, tool semantics,
evidence bytes, approval reference, withdrawal, incident, or revocation
invalidates this proposal. Owner approval must explicitly pin the SHA256 of
the exact prospective final `accepted` decision bytes in
<https://github.com/pedrosakuma/aws2azure/pull/1125#issuecomment-6928504512>.
