# Campaign State v1

> **Status:** Current pre-release in-process draft introduced by M4-C2. It is the checkpoint contract consumed by the later reducer/store slice; it is not a storage implementation, migration format, or compatibility promise.

## Purpose and ownership

`CampaignCheckpointState` is one complete, bounded, privacy-safe snapshot of the current campaign lineage. `CampaignStateFactory` is the only public construction boundary and `CampaignStateJson` owns exact canonical bytes plus the artifact SHA-256. A consumer either accepts the whole checkpoint or fails closed; partial recovery and best-effort parsing are forbidden.

C2 owns the versioned state vocabulary, immutable typed state, validation, exact JSON projection, current-context revalidation, trusted M3-to-M2 proposal projection, M2 request reconstruction, and typed M2 result commitment. It does not own reducer transitions, compare-and-swap persistence, dispatch, retry selection, patch execution, repository mutation, compatibility aliases, migration, CLI wiring, publication, or GitHub state.

## Authority and identity

The initial factory receives the exact `CampaignPlanningInput` and the caller-accepted `CampaignWorkPlan`, reruns C1, and rejects any mismatch. The checkpoint separately binds:

- product/contract revision ID and content SHA;
- opaque snapshot binding, repository, explicit-input, and policy-authority commitments;
- target profile and C1 execution commitment;
- the complete typed campaign budget and Scribe limits;
- a target-independent, already validated style-configuration projection, represented only by a bounded ID and a domain-separated content commitment;
- one composite campaign-configuration commitment over all correctness-bearing C1 content authorities and ceilings.

Concrete Style Profiles remain per-work C1 facts and are rebound when a trusted proposal is created. C2 never invents or reverses a global Style Profile DSL. `RepositoryContextRef` is process-local and is never persisted. The caller supplies it transiently when reconstructing an M2 request.

The artifact digest is lowercase SHA-256 over the exact canonical UTF-8 JSON bytes, including the one trailing LF. It is a wrapper property and is not serialized into its own preimage.

## State model

Every C1 work item appears exactly once and in exact C1 order. Its closed status is:

- `planned`: no proposal or terminal outcome;
- `proposalComplete`: one fully validated trusted proposal;
- `accepted`: that proposal is part of the current known candidate;
- `closed`: no proposal and one planning/Scribe terminal outcome.

Per-work outer attempts and candidate attempts are persisted separately from lineage-wide charges. Charges distinguish observed values, conservative unobserved exposure, and total charged; `totalCharged = (observed ?? 0) + conservativeUnobserved` is checked. The single active reservation is separate from charged history and is either a provider reservation for one planned item or a patch reservation for the exact active M2 request.

Candidate observation records only accepted work keys, changed-file hashes/counts, and the exact request/result commitments. It contains no source or candidate bytes. Cumulative M2 outcome, campaign terminal outcome, and an optional bounded predecessor summary are independent facts; a predecessor summary is not an embedded historical checkpoint.

## Trusted proposal and M2 closure

`CreateTrustedProposal` first revalidates current C1 authority. It requires an active provider reservation whose work-item key, exact request SHA, and M3 attempt identity match the proposal being admitted, plus exact request/result/run-envelope correlation, the composition owner's current expected Tool Policy ID, the current work target, source, applicable components, and exact Style Profile. An absent provider reservation, a patch reservation, or any substituted work, request, or attempt fails closed. It projects only stable, source-free evidence metadata and the typed M2 block, including the bounded validated proposed structured content needed by M2. Prompt text, evidence or existing-documentation text, provider payloads, raw responses, diffs, source bytes, credentials, and process-local repository handles are excluded.

For every patch attempt, `ReconstructPatchRequest` selects exactly work in `proposalComplete` or `accepted` status, preserves C1 order, constructs the sorted distinct provenance catalog, writes the complete M2 request, and sends those exact bytes through `DocumentationPatchValidator.ParseRequest`. A patch reservation must reference that active request SHA. `ReconstructAcceptedPatchRequest` independently selects only `accepted` work and correlates the known complete candidate and cumulative outcome. This distinction permits a known accepted candidate to coexist with newly proposal-complete work without pretending the unresolved next request already completed. Both projections prove the whole request against M2's 1 MiB, 512-block, 4,096-provenance, 64-reference-per-block, component, content, ordering, uniqueness, path, span, and vocabulary rules.

`CreatePatchResultCommitment` accepts only a result that passes `DocumentationPatchValidator.ValidateResult` for the exact request. Its domain-separated typed commitment covers request identity, outcome, ordered target traces, changed-file facts, changed-block count, invariants, and bounded diagnostics.

A cumulative `accepted`, `rejected`, or `stale` outcome represents a completed typed M2 validation result and therefore requires that result's exact commitment. `host-failure`, `cancelled`, and `timeout` are host-level completion families without a typed M2 result and require a null result commitment. An accepted outcome additionally matches the complete candidate observation's request and result commitments.

## Canonical JSON and validation

The registry is `schemas/campaign-state/v1.schema.json`. Canonical JSON uses fixed writer order, no insignificant whitespace, JSON integers only, ordinal collection order, strict UTF-8 without BOM, and exactly one trailing LF. Parsing rejects malformed UTF-8/JSON, BOM, duplicate or unknown properties, unknown enum values, over-depth or over-byte artifacts, invalid bounds/references/correlation, and any byte sequence that is not the exact writer output.

The checkpoint is bounded to 4 MiB, depth 96, 4,096 work rows, 512 active M2 blocks, 4,096 evidence rows, 64 provenance references per block, 512 changed files, and 128 diagnostics. Numeric observations are checked before arithmetic. Identifiers and paths are bounded and machine-absolute, traversal, backslash, NUL, drive-root, and noncanonical repository paths fail closed. Failure messages are fixed structural text and never echo caller content.

## Privacy boundary

The persisted artifact may contain stable symbol IDs, repository-relative paths, spans, enum IDs, opaque contract IDs, hashes, counts, truncation flags, source-free evidence locators, and the bounded validated proposed M2 structured content. It must not contain source, existing-documentation, or evidence text; prompts; provider requests or raw responses; candidate bytes; full diffs; secrets; credentials; absolute machine paths; `RepositoryContextRef`; transcripts; environment values; or GitHub metadata.

## Conformance

The normative implementation lives under `src/ContractScribe.Core/Campaign/State/**`. Fixtures under `tests/fixtures/campaign/state/**` and `CampaignStateContractTests` cover fixed known-answer bytes/digest, culture/process stability, canonical round-trip, duplicate/unknown/version/whitespace/BOM/UTF-8/bounds mutations, fail-closed privacy, and current M2 projection correlation. Later M4 slices consume this contract but must not weaken or silently reinterpret it.
