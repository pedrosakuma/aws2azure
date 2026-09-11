#!/usr/bin/env bash
set -euo pipefail

append_env() {
  printf '%s=%s\n' "$1" "$2" >> "$GITHUB_ENV"
  export "$1=$2"
}

policy="docs/workloads/observation/$PROFILE.yaml"
[ -f "$policy" ] || {
  echo "::error::Missing committed RC observation policy for $PROFILE."
  exit 1
}
policy_min="$(sed -n 's/^minimum_window_minutes: //p' "$policy")"
policy_max="$(sed -n 's/^maximum_window_minutes: //p' "$policy")"
if [[ ! "$policy_min" =~ ^[0-9]+$ ]] ||
   [[ ! "$policy_max" =~ ^[0-9]+$ ]] ||
   [ "$WINDOW_MINUTES" -lt "$policy_min" ] ||
   [ "$WINDOW_MINUTES" -gt "$policy_max" ]; then
  echo "::error::Requested window violates the committed profile observation policy."
  exit 1
fi
case "$PROFILE" in
  s3-basic-object-crud)
    DEPLOYMENT_NAME=aws2azure-rc-observe-s3
    BICEP_PATH=deploy/realazure/s3-load.bicep
    TEST_FILTER=Category=S3RcObservation
    ;;
  secretsmanager-basic-lifecycle)
    DEPLOYMENT_NAME=aws2azure-rc-observe-secretsmanager
    BICEP_PATH=deploy/realazure/secretsmanager-load.bicep
    TEST_FILTER=Category=SecretsManagerRcObservation
    ;;
  *)
    echo "::error::Unsupported profile: $PROFILE"
    exit 1
    ;;
esac
append_env DEPLOYMENT_NAME "$DEPLOYMENT_NAME"
append_env BICEP_PATH "$BICEP_PATH"
append_env TEST_FILTER "$TEST_FILTER"

if [ -z "${AZURE_CLIENT_ID:-}" ] ||
   [ -z "${AZURE_TENANT_ID:-}" ] ||
   [ -z "${AZURE_SUBSCRIPTION_ID:-}" ]; then
  echo "::error::RC observation requires Azure OIDC; no skip or emulator fallback is allowed."
  exit 1
fi
if [ "$PROFILE" = secretsmanager-basic-lifecycle ] &&
   [ -z "${AZURE_CLIENT_OBJECT_ID:-}" ]; then
  echo "::error::Secrets Manager RC observation requires AZURE_CLIENT_OBJECT_ID."
  exit 1
fi

archive_root="$PRIVATE_ROOT/rc-archive"
archive_identity="$CAPTURE_ROOT/archive-artifact-identity.json"
archive_selection="$CAPTURE_ROOT/archive-input-selection.json"
archive_inputs="$archive_root/content/release-candidate-archive-inputs.json"
archive_attestations="$PRIVATE_ROOT/archive-attestations.json"
ledger_attestations="$PRIVATE_ROOT/approved-ledger-attestations.json"

install -d -m 0700 "$PRIVATE_ROOT"
mkdir -p "$CAPTURE_ROOT" "$OUTPUT_ROOT"
tag_ref_json="$PRIVATE_ROOT/candidate-tag-ref.json"
gh api -H "Accept: application/vnd.github+json" \
  "/repos/$GITHUB_REPOSITORY/git/ref/tags/$RELEASE_CANDIDATE_ID" > "$tag_ref_json"
tag_object_type="$(jq -er '.object.type' "$tag_ref_json")"
tag_object_sha="$(jq -er '.object.sha' "$tag_ref_json")"
for depth in $(seq 1 8); do
  if [ "$tag_object_type" = commit ]; then
    break
  fi
  if [ "$tag_object_type" != tag ] || [[ ! "$tag_object_sha" =~ ^[0-9a-f]{40}$ ]]; then
    echo "::error::Candidate ref does not resolve through commit or annotated-tag objects."
    exit 1
  fi
  tag_object_json="$PRIVATE_ROOT/candidate-tag-object-$depth.json"
  gh api -H "Accept: application/vnd.github+json" \
    "/repos/$GITHUB_REPOSITORY/git/tags/$tag_object_sha" > "$tag_object_json"
  tag_object_type="$(jq -er '.object.type' "$tag_object_json")"
  tag_object_sha="$(jq -er '.object.sha' "$tag_object_json")"
done
if [ "$tag_object_type" != commit ] || [ "$tag_object_sha" != "$CANDIDATE_SOURCE_SHA" ]; then
  echo "::error::Current protected candidate tag does not resolve to the selected source SHA."
  exit 1
fi

./eng/download-qualified-run-artifact.sh \
  --repository "$GITHUB_REPOSITORY" \
  --run-id "$ARCHIVE_RUN_ID" \
  --run-attempt "$ARCHIVE_RUN_ATTEMPT" \
  --expected-sha "$ARCHIVE_WORKFLOW_SOURCE_SHA" \
  --expected-ref refs/heads/main \
  --workflow ".github/workflows/release-candidate.yml" \
  --event workflow_dispatch \
  --profile release-candidate-archives \
  --destination "$archive_root" \
  --artifact-name "$ARCHIVE_ARTIFACT_NAME" \
  --artifact-id "$ARCHIVE_ARTIFACT_ID" \
  --identity-output "$archive_identity"

python3 eng/release-candidate-inputs.py validate "$archive_inputs"
jq -e \
  --arg candidate "$RELEASE_CANDIDATE_ID" \
  --arg source_sha "$CANDIDATE_SOURCE_SHA" \
  --arg source_ref "refs/tags/$RELEASE_CANDIDATE_ID" \
  --arg profile "$PROFILE" \
  --argjson run_id "$ARCHIVE_RUN_ID" \
  --argjson run_attempt "$ARCHIVE_RUN_ATTEMPT" \
  '
    .candidate.identifier == $candidate and
    .candidate.source.repository == env.GITHUB_REPOSITORY and
    .candidate.source.sha == $source_sha and
    .candidate.source.ref == $source_ref and
    .orchestration_source.repository == env.GITHUB_REPOSITORY and
    .orchestration_source.sha == env.ARCHIVE_WORKFLOW_SOURCE_SHA and
    .orchestration_source.ref == "refs/heads/main" and
    .producer.workflow == ".github/workflows/release-candidate.yml" and
    .producer.event_name == "workflow_dispatch" and
    .producer.run_id == $run_id and
    .producer.run_attempt == $run_attempt and
    .producer.source_sha == env.ARCHIVE_WORKFLOW_SOURCE_SHA and
    .producer.source_ref == "refs/heads/main" and
    .pending_interfaces.observation_evidence.status == "pending" and
    .pending_interfaces.observation_evidence.issue == 582 and
    ([.workloads[] | select(.profile.id == $profile)] | length) == 1
  ' "$archive_inputs" >/dev/null
content_digest="$(jq -er '.content_digest' "$archive_inputs")"
if [ "$content_digest" != "$ARCHIVE_CONTENT_DIGEST" ]; then
  echo "::error::Selected archive inputs do not match the approved content digest."
  exit 1
fi
expected_name="aws2azure-rc-archives-${RELEASE_CANDIDATE_ID}-${content_digest#sha256:}-run-${ARCHIVE_RUN_ID}-attempt-${ARCHIVE_RUN_ATTEMPT}"
if [[ "$ARCHIVE_ARTIFACT_NAME" != "$expected_name" ]]; then
  echo "::error::Selected RC archive artifact name is not bound to its canonical inputs digest and producer attempt."
  exit 1
fi
jq -e \
  --argjson artifact_id "$ARCHIVE_ARTIFACT_ID" \
  --arg artifact_name "$ARCHIVE_ARTIFACT_NAME" \
  --arg upload_digest "$ARCHIVE_ARTIFACT_DIGEST" \
  '.artifact.id == $artifact_id and .artifact.name == $artifact_name and .artifact.upload_digest == $upload_digest' \
  "$archive_identity" >/dev/null
archive_inputs_sha="$(sha256sum "$archive_inputs" | awk '{print $1}')"
gh attestation verify "$archive_inputs" \
  --repo "$GITHUB_REPOSITORY" \
  --signer-workflow "$GITHUB_REPOSITORY/.github/workflows/release-candidate.yml" \
  --source-digest "$ARCHIVE_WORKFLOW_SOURCE_SHA" \
  --source-ref refs/heads/main \
  --predicate-type "https://slsa.dev/provenance/v1" \
  --deny-self-hosted-runners \
  --format json > "$archive_attestations"
expected_archive_attempt_uri="https://github.com/${GITHUB_REPOSITORY}/actions/runs/${ARCHIVE_RUN_ID}/attempts/${ARCHIVE_RUN_ATTEMPT}"
jq -e \
  --arg repository "$GITHUB_REPOSITORY" \
  --arg source_sha "$ARCHIVE_WORKFLOW_SOURCE_SHA" \
  --arg source_ref refs/heads/main \
  --arg attempt_uri "$expected_archive_attempt_uri" \
  --arg digest "$archive_inputs_sha" \
  '[.[]
    | select(.verificationResult.statement.predicateType == "https://slsa.dev/provenance/v1")
    | select(.verificationResult.signature.certificate.githubWorkflowTrigger == "workflow_dispatch")
    | select(.verificationResult.signature.certificate.githubWorkflowRepository == $repository)
    | select(.verificationResult.signature.certificate.githubWorkflowRef == $source_ref)
    | select(.verificationResult.signature.certificate.githubWorkflowSHA == $source_sha)
    | select(.verificationResult.signature.certificate.sourceRepositoryDigest == $source_sha)
    | select(.verificationResult.signature.certificate.sourceRepositoryRef == $source_ref)
    | select(.verificationResult.signature.certificate.runInvocationURI == $attempt_uri)
    | select(.verificationResult.statement.predicate.runDetails.metadata.invocationId == $attempt_uri)
    | select(any(.verificationResult.statement.subject[]; .name == "release-candidate-archive-inputs.json" and .digest.sha256 == $digest))] | length == 1' \
  "$archive_attestations" >/dev/null

case "$PROFILE" in
  s3-basic-object-crud) ledger_name=s3-approved-runtime.json ;;
  secretsmanager-basic-lifecycle) ledger_name=secretsmanager-approved-runtime.json ;;
esac
approved_ledger="$archive_root/content/context/$ledger_name"
if [ ! -f "$approved_ledger" ] || [ -L "$approved_ledger" ]; then
  echo "::error::RC archive does not contain the exact regular approved-runtime export."
  exit 1
fi
approved_ledger_sha="$(sha256sum "$approved_ledger" | awk '{print $1}')"
gh attestation verify "$approved_ledger" \
  --repo "$GITHUB_REPOSITORY" \
  --signer-workflow "$GITHUB_REPOSITORY/.github/workflows/release-candidate.yml" \
  --source-digest "$ARCHIVE_WORKFLOW_SOURCE_SHA" \
  --source-ref refs/heads/main \
  --predicate-type "https://slsa.dev/provenance/v1" \
  --deny-self-hosted-runners \
  --format json > "$ledger_attestations"
jq -e \
  --arg repository "$GITHUB_REPOSITORY" \
  --arg source_sha "$ARCHIVE_WORKFLOW_SOURCE_SHA" \
  --arg source_ref refs/heads/main \
  --arg attempt_uri "$expected_archive_attempt_uri" \
  --arg subject_name "$ledger_name" \
  --arg digest "$approved_ledger_sha" \
  '[.[]
    | select(.verificationResult.statement.predicateType == "https://slsa.dev/provenance/v1")
    | select(.verificationResult.signature.certificate.githubWorkflowTrigger == "workflow_dispatch")
    | select(.verificationResult.signature.certificate.githubWorkflowRepository == $repository)
    | select(.verificationResult.signature.certificate.githubWorkflowRef == $source_ref)
    | select(.verificationResult.signature.certificate.githubWorkflowSHA == $source_sha)
    | select(.verificationResult.signature.certificate.sourceRepositoryDigest == $source_sha)
    | select(.verificationResult.signature.certificate.sourceRepositoryRef == $source_ref)
    | select(.verificationResult.signature.certificate.runInvocationURI == $attempt_uri)
    | select(.verificationResult.statement.predicate.runDetails.metadata.invocationId == $attempt_uri)
    | select(any(.verificationResult.statement.subject[]; .name == $subject_name and .digest.sha256 == $digest))] | length == 1' \
  "$ledger_attestations" >/dev/null
cp -- "$approved_ledger" "$CAPTURE_ROOT/current-approved-runtime.json"
chmod 0600 "$CAPTURE_ROOT/current-approved-runtime.json"
jq -S '.approved_ledger_source' "$archive_inputs" > "$CAPTURE_ROOT/approved-ledger-source.json"

jq -n --slurpfile archive "$archive_inputs" --slurpfile identity "$archive_identity" --arg profile "$PROFILE" '
  ($archive[0].workloads[] | select(.profile.id == $profile)) as $workload
  | {
      schema_version: 1,
      candidate_id: $archive[0].candidate.identifier,
      content_digest: $archive[0].content_digest,
      source_sha: $archive[0].candidate.source.sha,
      source_ref: $archive[0].candidate.source.ref,
      producer: {
        repository: $archive[0].candidate.source.repository,
        workflow_path: $archive[0].producer.workflow,
        event_name: $archive[0].producer.event_name,
        run_id: $archive[0].producer.run_id,
        run_attempt: $archive[0].producer.run_attempt,
        attempt_url: $archive[0].producer.attempt_url,
        source_sha: $archive[0].producer.source_sha,
        source_ref: $archive[0].producer.source_ref
      },
      artifact: {
        id: $identity[0].artifact.id,
        name: $identity[0].artifact.name,
        upload_digest: $identity[0].artifact.upload_digest
      },
      workload: {
        profile_id: $workload.profile.id,
        profile_version: $workload.profile.version,
        workload_manifest_digest: $workload.profile.digest,
        approved_runtime_ledger_digest: $workload.approved_runtime.ledger_record_digest,
        approved_runtime_source_sha: $workload.approved_runtime.source_sha,
        approved_runtime_aggregate_digest: $workload.approved_runtime.aggregate_digest,
        approved_runtime_executable_digest: $workload.approved_runtime.executable_digest,
        approved_runtime_artifact: $workload.approved_runtime.artifact
      }
    }' > "$archive_selection"

ghcr_root="$PRIVATE_ROOT/rc-ghcr"
ghcr_identity="$CAPTURE_ROOT/ghcr-artifact-identity.json"
ghcr_selection="$CAPTURE_ROOT/ghcr-input-selection.json"
identity_selection="$CAPTURE_ROOT/canonical-identity-selection.json"
ghcr_inputs="$ghcr_root/content/release-candidate-ghcr-inputs.json"
archive_selection="$CAPTURE_ROOT/archive-input-selection.json"
ghcr_attestations="$PRIVATE_ROOT/ghcr-input-attestations.json"

./eng/download-qualified-run-artifact.sh \
  --repository "$GITHUB_REPOSITORY" \
  --run-id "$GHCR_RUN_ID" \
  --run-attempt "$GHCR_RUN_ATTEMPT" \
  --expected-sha "$GHCR_WORKFLOW_SOURCE_SHA" \
  --expected-ref refs/heads/main \
  --workflow ".github/workflows/release-candidate-image.yml" \
  --event workflow_dispatch \
  --profile release-candidate-ghcr \
  --destination "$ghcr_root" \
  --artifact-name "$GHCR_ARTIFACT_NAME" \
  --artifact-id "$GHCR_ARTIFACT_ID" \
  --identity-output "$ghcr_identity"
(
  cd "$ghcr_root/content"
  sha256sum -c SHA256SUMS
)
python3 eng/release-candidate-image.py validate-ghcr-input "$ghcr_inputs"
jq -e \
  --arg candidate "$RELEASE_CANDIDATE_ID" \
  --arg candidate_sha "$CANDIDATE_SOURCE_SHA" \
  --arg workflow_sha "$GHCR_WORKFLOW_SOURCE_SHA" \
  --argjson run_id "$GHCR_RUN_ID" \
  --argjson run_attempt "$GHCR_RUN_ATTEMPT" \
  '.candidate.identifier == $candidate and .candidate.source.repository == env.GITHUB_REPOSITORY and .candidate.source.sha == $candidate_sha and .candidate.source.ref == ("refs/tags/" + $candidate) and .producer.workflow == ".github/workflows/release-candidate-image.yml" and .producer.event_name == "workflow_dispatch" and .producer.run_id == $run_id and .producer.run_attempt == $run_attempt and .producer.workflow_source_sha == $workflow_sha' \
  "$ghcr_inputs" >/dev/null
content_digest="$(jq -er '.content_digest' "$ghcr_inputs")"
if [ "$content_digest" != "$GHCR_CONTENT_DIGEST" ]; then
  echo "::error::Selected GHCR inputs do not match the approved content digest."
  exit 1
fi
expected_name="aws2azure-rc-ghcr-${RELEASE_CANDIDATE_ID}-${content_digest#sha256:}-run-${GHCR_RUN_ID}-attempt-${GHCR_RUN_ATTEMPT}"
if [ "$GHCR_ARTIFACT_NAME" != "$expected_name" ]; then
  echo "::error::Selected GHCR artifact name is not bound to its canonical inputs digest and producer attempt."
  exit 1
fi
jq -e --argjson artifact_id "$GHCR_ARTIFACT_ID" --arg artifact_name "$GHCR_ARTIFACT_NAME" --arg upload_digest "$GHCR_ARTIFACT_DIGEST" '.artifact.id == $artifact_id and .artifact.name == $artifact_name and .artifact.upload_digest == $upload_digest' "$ghcr_identity" >/dev/null
archive_manifest_sha="sha256:$(sha256sum "$archive_inputs" | awk '{print $1}')"
jq -e --slurpfile archive "$archive_inputs" --slurpfile selection "$archive_selection" --arg archive_manifest_sha "$archive_manifest_sha" '.candidate == $archive[0].candidate and .archive_input.producer.workflow == $archive[0].producer.workflow and .archive_input.producer.event_name == $archive[0].producer.event_name and .archive_input.producer.run_id == $archive[0].producer.run_id and .archive_input.producer.run_attempt == $archive[0].producer.run_attempt and .archive_input.producer.attempt_url == $archive[0].producer.attempt_url and .archive_input.producer.source_sha == $archive[0].producer.source_sha and .archive_input.producer.source_ref == $archive[0].producer.source_ref and .archive_input.content_digest == $archive[0].content_digest and .archive_input.manifest_sha256 == $archive_manifest_sha and .archive_input.artifact.id == $selection[0].artifact.id and .archive_input.artifact.name == $selection[0].artifact.name and .archive_input.artifact.upload_digest == $selection[0].artifact.upload_digest' "$ghcr_inputs" >/dev/null || {
  echo "::error::GHCR inputs do not consume the exact selected archive interface."
  exit 1
}
ghcr_inputs_sha="$(sha256sum "$ghcr_inputs" | awk '{print $1}')"
gh attestation verify "$ghcr_inputs" \
  --repo "$GITHUB_REPOSITORY" \
  --signer-workflow "$GITHUB_REPOSITORY/.github/workflows/release-candidate-image.yml" \
  --source-digest "$GHCR_WORKFLOW_SOURCE_SHA" \
  --source-ref refs/heads/main \
  --predicate-type "https://slsa.dev/provenance/v1" \
  --deny-self-hosted-runners \
  --format json > "$ghcr_attestations"
expected_ghcr_attempt_uri="https://github.com/${GITHUB_REPOSITORY}/actions/runs/${GHCR_RUN_ID}/attempts/${GHCR_RUN_ATTEMPT}"
jq -e --arg repository "$GITHUB_REPOSITORY" --arg source_sha "$GHCR_WORKFLOW_SOURCE_SHA" --arg source_ref refs/heads/main --arg attempt_uri "$expected_ghcr_attempt_uri" --arg digest "$ghcr_inputs_sha" '[.[] | select(.verificationResult.statement.predicateType == "https://slsa.dev/provenance/v1") | select(.verificationResult.signature.certificate.githubWorkflowTrigger == "workflow_dispatch") | select(.verificationResult.signature.certificate.githubWorkflowRepository == $repository) | select(.verificationResult.signature.certificate.githubWorkflowRef == $source_ref) | select(.verificationResult.signature.certificate.githubWorkflowSHA == $source_sha) | select(.verificationResult.signature.certificate.sourceRepositoryDigest == $source_sha) | select(.verificationResult.signature.certificate.sourceRepositoryRef == $source_ref) | select(.verificationResult.signature.certificate.runInvocationURI == $attempt_uri) | select(.verificationResult.statement.predicate.runDetails.metadata.invocationId == $attempt_uri) | select(any(.verificationResult.statement.subject[]; .name == "release-candidate-ghcr-inputs.json" and .digest.sha256 == $digest))] | length == 1' "$ghcr_attestations" >/dev/null
jq -S -n --slurpfile ghcr "$ghcr_inputs" --slurpfile identity "$ghcr_identity" --slurpfile archive "$archive_selection" '{schema_version: 1, candidate_id: $ghcr[0].candidate.identifier, content_digest: $ghcr[0].content_digest, source_sha: $ghcr[0].candidate.source.sha, producer: {repository: $ghcr[0].candidate.source.repository, workflow_path: $ghcr[0].producer.workflow, event_name: $ghcr[0].producer.event_name, run_id: $ghcr[0].producer.run_id, run_attempt: $ghcr[0].producer.run_attempt, attempt_url: $ghcr[0].producer.attempt_url, source_sha: $ghcr[0].producer.workflow_source_sha, source_ref: "refs/heads/main"}, artifact: {id: $identity[0].artifact.id, name: $identity[0].artifact.name, upload_digest: $identity[0].artifact.upload_digest}, archive_content_digest: $ghcr[0].archive_input.content_digest, archive_artifact: $archive[0].artifact, index_digest: $ghcr[0].container.index_digest}' > "$ghcr_selection"
descriptor="$PRIVATE_ROOT/rc-archive/content/rc-identity-descriptor.json"
receipt="$CAPTURE_ROOT/release-candidate-identity.json"
jq -S -n --slurpfile archive "$archive_inputs" --slurpfile ghcr "$ghcr_inputs" '{schema_version: 1, candidate: $archive[0].candidate, producer: $archive[0].producer, platforms: $archive[0].platforms, container: $ghcr[0].container, workloads: $archive[0].workloads, compatibility_policy: $archive[0].compatibility_policy}' > "$descriptor"
python3 eng/release-candidate-manifest.py identity "$descriptor" "$receipt"
python3 eng/release-candidate-manifest.py validate-identity "$receipt"
jq -S -n --slurpfile receipt "$receipt" --arg archive_digest "$ARCHIVE_CONTENT_DIGEST" --arg ghcr_digest "$GHCR_CONTENT_DIGEST" '{schema_version: 1, artifact_kind: $receipt[0].artifact_kind, candidate_id: $receipt[0].candidate.identifier, identity_digest: $receipt[0].identity_digest, content_digest: $receipt[0].content_digest, archive_inputs_digest: $archive_digest, ghcr_inputs_digest: $ghcr_digest}' > "$identity_selection"

sealed_env="$PRIVATE_ROOT/sealed-runtime.env"
: > "$sealed_env"
gap_docs="tools/Aws2Azure.GapDocs/bin/Release/net10.0/Aws2Azure.GapDocs.dll"
current_ledger="$CAPTURE_ROOT/current-approved-runtime.json"
prior_ledger="$CAPTURE_ROOT/prior-approved-runtime.json"
candidate_identity="$CAPTURE_ROOT/candidate-runtime.json"
prior_identity="$CAPTURE_ROOT/prior-runtime.json"
candidate_sha="$(jq -er '.record.runtime.source_sha' "$current_ledger")"
if [ "$candidate_sha" != "$CANDIDATE_SOURCE_SHA" ]; then
  echo "::error::The RC tag SHA does not equal the profile's exact approved runtime source SHA."
  exit 1
fi
jq -e --slurpfile selection "$CAPTURE_ROOT/archive-input-selection.json" '$selection[0].workload.profile_id == .record.profile.id and $selection[0].workload.profile_version == .record.profile.version and $selection[0].workload.approved_runtime_ledger_digest == .ledger_record_digest and $selection[0].workload.approved_runtime_source_sha == .record.runtime.source_sha and $selection[0].workload.approved_runtime_aggregate_digest == .record.runtime.aggregate_digest and $selection[0].workload.approved_runtime_executable_digest == .record.runtime.executable_digest and $selection[0].workload.approved_runtime_artifact.id == .record.artifact.id and $selection[0].workload.approved_runtime_artifact.name == .record.artifact.name and $selection[0].workload.approved_runtime_artifact.upload_digest == .record.artifact.upload_digest' "$current_ledger" >/dev/null || {
  echo "::error::RC archive inputs do not bind the current profile's exact approved runtime."
  exit 1
}
candidate_run_id="$(jq -er '.record.producer.run_id' "$current_ledger")"
candidate_attempt="$(jq -er '.record.producer.run_attempt' "$current_ledger")"
candidate_ref="$(jq -er '.record.attestation.source_ref' "$current_ledger")"
candidate_artifact_id="$(jq -er '.record.artifact.id' "$current_ledger")"
./eng/resolve-sealed-runtime.sh --repository "$GITHUB_REPOSITORY" --run-id "$candidate_run_id" --run-attempt "$candidate_attempt" --expected-sha "$candidate_sha" --expected-ref "$candidate_ref" --profile "$PROFILE" --profile-version 1 --role candidate --artifact-id "$candidate_artifact_id" --destination "$PRIVATE_ROOT/sealed-candidate" --identity-output "$candidate_identity" --github-env "$sealed_env"
dotnet exec "$gap_docs" export-approved-runtime --profile "$PROFILE" --rollback-target --candidate "$candidate_identity" --ledger-json "$current_ledger" --output "$prior_ledger"
prior_run_id="$(jq -er '.record.producer.run_id' "$prior_ledger")"
prior_attempt="$(jq -er '.record.producer.run_attempt' "$prior_ledger")"
prior_sha="$(jq -er '.record.runtime.source_sha' "$prior_ledger")"
prior_ref="$(jq -er '.record.attestation.source_ref' "$prior_ledger")"
prior_artifact_id="$(jq -er '.record.artifact.id' "$prior_ledger")"
./eng/resolve-sealed-runtime.sh --repository "$GITHUB_REPOSITORY" --run-id "$prior_run_id" --run-attempt "$prior_attempt" --expected-sha "$prior_sha" --expected-ref "$prior_ref" --profile "$PROFILE" --profile-version 1 --role prior --artifact-id "$prior_artifact_id" --ledger-json "$prior_ledger" --destination "$PRIVATE_ROOT/sealed-prior" --identity-output "$prior_identity" --github-env "$sealed_env"
set -a
. "$sealed_env"
set +a
candidate_runtime="$(jq -er '.runtime.aggregate_digest' "$candidate_identity")"
prior_runtime="$(jq -er '.runtime.aggregate_digest' "$prior_identity")"
if [ "$candidate_runtime" = "$prior_runtime" ]; then
  echo "::error::Candidate and trusted prior runtime digests must be distinct."
  exit 1
fi
append_env AWS2AZURE_SEALED_RUNTIME_MODE rollback
append_env AWS2AZURE_QUALIFICATION_SHA "$candidate_sha"
chmod 0644 "$current_ledger" "$prior_ledger" "$candidate_identity" "$prior_identity"

az group create -n "$RG_NAME" -l "$AZURE_LOCATION" \
  --tags purpose=aws2azure-rc-observation profile="$PROFILE" cohort="$COHORT" rc="$RELEASE_CANDIDATE_ID" run-id="$GITHUB_RUN_ID" run-attempt="$GITHUB_RUN_ATTEMPT" created="$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  -o none
args=(az deployment group create -g "$RG_NAME" -n "$DEPLOYMENT_NAME" -f "$BICEP_PATH")
if [ "$PROFILE" = secretsmanager-basic-lifecycle ]; then
  args+=( -p bootstrapPrincipalId="$AZURE_CLIENT_OBJECT_ID" )
fi
"${args[@]}" -o none

wait_for_vault_rbac() {
  local vault_name="$1"
  local ready=false
  for _ in $(seq 1 30); do
    if az keyvault secret list --vault-name "$vault_name" --maxresults 1 -o none 2>/dev/null; then
      ready=true
      break
    fi
    sleep 10
  done
  [ "$ready" = true ]
}
if [ "$PROFILE" = secretsmanager-basic-lifecycle ]; then
  endpoint="$(az deployment group show -g "$RG_NAME" -n "$DEPLOYMENT_NAME" --query properties.outputs.keyVaultUri.value -o tsv)"
  name="$(az deployment group show -g "$RG_NAME" -n "$DEPLOYMENT_NAME" --query properties.outputs.keyVaultName.value -o tsv)"
  if [ -z "$endpoint" ] || [ -z "$name" ]; then
    echo "::error::Key Vault deployment outputs are incomplete."
    exit 1
  fi
  if ! wait_for_vault_rbac "$name"; then
    echo "::error::Key Vault RBAC did not become ready within five minutes."
    exit 1
  fi
  append_env AZURE_KEYVAULT_URL "$endpoint"
else
  account="$(az deployment group show -g "$RG_NAME" -n "$DEPLOYMENT_NAME" --query properties.outputs.storageAccountName.value -o tsv)"
  endpoint="$(az deployment group show -g "$RG_NAME" -n "$DEPLOYMENT_NAME" --query properties.outputs.blobEndpoint.value -o tsv)"
  key="$(az storage account keys list -g "$RG_NAME" -n "$account" --query '[0].value' -o tsv)"
  if [ -z "$account" ] || [ -z "$endpoint" ] || [ -z "$key" ]; then
    echo "::error::Blob Storage deployment outputs are incomplete."
    exit 1
  fi
  echo "::add-mask::$key"
  append_env AZURE_BLOB_ACCOUNT "$account"
  append_env AZURE_BLOB_KEY "$key"
  append_env AZURE_BLOB_ENDPOINT "$endpoint"
fi

if [ "$PROFILE" = secretsmanager-basic-lifecycle ]; then
  token_dir="$GITHUB_WORKSPACE/$PRIVATE_ROOT/tokens"
  install -d -m 0700 "$token_dir"
  token_file="$token_dir/azure-federated-token.jwt"
  (
    umask 077
    curl -fsS \
      -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
      "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=api://AzureADTokenExchange" |
      python3 -c 'import json,pathlib,sys; value=json.load(sys.stdin).get("value"); assert value; pathlib.Path(sys.argv[1]).write_text(value)' "$token_file"
  )
  chmod 0600 "$token_file"
  append_env AZURE_FEDERATED_TOKEN_FILE "$token_file"
fi

candidate_concurrency="$(awk '$1 == "candidate_concurrency:" { print $2 }' "$policy")"
stable_concurrency="$(awk '$1 == "stable_concurrency:" { print $2 }' "$policy")"
operation_mix_identity="$(awk '$1 == "operation_mix_identity:" { print $2 }' "$policy")"
append_env AWS2AZURE_RC_OBSERVATION_CANDIDATE_CONCURRENCY "$candidate_concurrency"
append_env AWS2AZURE_RC_OBSERVATION_STABLE_CONCURRENCY "$stable_concurrency"
append_env AWS2AZURE_RC_OBSERVATION_OPERATION_MIX_IDENTITY "$operation_mix_identity"
append_env AWS2AZURE_RC_OBSERVATION_SYNC_AT_UTC "$OBSERVATION_SYNC_AT_UTC"
append_env AWS2AZURE_RC_OBSERVATION_COHORT_ROLE "$COHORT"

cohort_capture="$CAPTURE_ROOT/cohort-capture.json"
test_timeout=$((WINDOW_MINUTES * 60 + 1200))
AWS2AZURE_RC_OBSERVATION_COHORT_CAPTURE_PATH="$cohort_capture" \
AWS2AZURE_RC_OBSERVATION_COHORT_ROLE="$COHORT" \
AWS2AZURE_RC_OBSERVATION_SYNC_AT_UTC="$OBSERVATION_SYNC_AT_UTC" \
AWS2AZURE_RC_OBSERVATION_WINDOW_MINUTES="$WINDOW_MINUTES" \
timeout --signal=TERM --kill-after=120s "${test_timeout}s" \
  dotnet test tests/Aws2Azure.IntegrationTests \
    -c Release --no-build --nologo \
    --filter "$TEST_FILTER" \
    --logger "console;verbosity=normal"

jq -e --arg profile "$PROFILE" --arg cohort "$COHORT" '
  .schema_version == 1 and
  .profile.id == $profile and
  .profile.version == 1 and
  .cohort.role == $cohort and
  (.metrics | length) == 2 and
  .load_shape.candidate_concurrency > 0 and
  .load_shape.stable_concurrency > 0 and
  (($cohort == "candidate" and .restoration != null and .restoration.verified == true)
   or ($cohort == "stable" and .restoration == null))
' "$cohort_capture" >/dev/null
