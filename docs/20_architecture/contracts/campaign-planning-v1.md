# Campaign Planning v1

> **Status:** Current pre-release in-process draft introduced by M4-C1. Governing M4 design authority: repository revision `9853f5e234cd7c245b058e7573b8c53e51c188a9`. This document defines the current `ContractScribe.Core` planning semantics. It is not a persisted or external interchange schema and makes no compatibility or migration commitment.

## Purpose and boundary

`CampaignPlanner.Plan` deterministically converts one snapshot's validated M1 authority into complete-block documentation work. It is pure planning: no filesystem, workspace, Roslyn, Patching, provider, process, environment, credential, network, persistence, or GitHub dependency is permitted.

M4-C1 owns:

- validation and exact correlation of current Classification, Observation, Audit, owner, source, Style Profile, snapshot, and execution-policy authority;
- selection of every and only canonical Audit violation;
- physical-owner collapse without loss of target/component association;
- executable versus terminal disposition;
- canonical total ordering;
- one composite execution commitment and snapshot-scoped work-item keys;
- bounded, privacy-safe immutable output.

M4-C1 does not own checkpoint bytes, cursor or batch identity, state reducers, consumed-budget accounting, provider dispatch, M2 proposal or patch execution, CLI composition, publication, GitHub state, retry ledgers, pull-request generations, or cross-snapshot reconciliation. Those are later contracts.

## Inputs

The planner accepts one `CampaignPlanningInput` containing:

1. `CampaignPlanningSnapshot`: bounded campaign lineage and opaque snapshot binding; lowercase SHA-256 repository, explicit-input, and policy-authority commitments; and one closed `TargetProfile`.
2. `CampaignPlanningExecutionPolicy`: the complete validated `DocumentationScribeRunLimits`, typed campaign ceilings, cost enforcement settings, and content authority for proposal, agent protocol, context selection, tool policy/registry, provider/model request, retry, M2 projection, cost-rate policy when enabled, and product/contract revision.
3. The exact current `ClassificationSet`, `DocumentationObservationSet`, canonical `AuditDocument`, and `CampaignPlanningEvidenceAuthority` rows that preserve the exact `BoundObservationEvidence` used to produce each Audit subject.
4. `CampaignPlanningOwnerAuthoritySet`: every and only supported target, partitioned into snapshot-local physical-owner rows. Each target carries its exact classification, authoritative declaration ID, source kind and commitment, repository path and encoding or generated identity, requested/canonical/owner/documentation spans, block state, writability, ordered applicable components and names, the exact owner `SymbolRef` set, multi-declarator and primary-constructor facts, and an exact target-valid `DocumentationScribeStyleProfile` when the target could be executable.

`CampaignPlanningContentAuthority.CreateValidatedJsonProjection` is the only content-authority constructor. It accepts one closed family and one already validated JSON object projection. The closed family IDs are proposal contract, agent protocol, context selection, tool policy/registry, provider/model request profile, retry policy, M2 projection policy, product/contract revision, and cost-rate policy. Each family projection contains every correctness-bearing value selected by that family's composition owner; absent optional values are omitted, explicit JSON null remains null, integers remain JSON integers, strings remain exact Unicode scalar content, and arrays retain their contract-defined order. Objects are canonicalized recursively by `AuditCanonicalJson`; duplicate properties, non-integer numbers, invalid Unicode, or any non-object root are rejected. The content digest hashes domain `contract-scribe/campaign-configuration-authority/v1`, the closed family ID, and the canonical strict-UTF-8 JSON projection through the same explicit length framing used by campaign commitments. A visible configuration ID alone has no authority, and the same projection under another family has another digest. Changing canonical content while retaining the ID changes the execution commitment.

The opaque snapshot binding is caller-attested. Core binds it but does not discover Git or prove a live repository. Observation source hashes bind normalized observed declaration content; they do not replace the exact file-byte/source commitment and encoding carried by owner authority.

## Validation

Validation completes before identity construction. The planner fails closed with one `CampaignPlanningValidationException.Code` from the closed `CampaignPlanningValidationCode` vocabulary.

The authority graph must provide every and only current classification, observation, bound-evidence row, canonical Audit row, supported target, applicable component, and owner association once. Target profile, classification fields including traits and skip facts, observation subjects and values, bound evidence authority/completeness/rows/IDs, Audit row subjects/outcomes/reasons/unresolved locators, source identity, authoritative declaration ID, component parent/kind/identity/name, Style Profile component sequence, and owner membership must correlate exactly. Missing, extra, duplicate, stale, substituted, contradictory, default, or unsupported data invalidates the plan.

Repository physical-owner identity is derived by Core from source kind, canonical repository path, exact file-content commitment, repository encoding, and owner span. Generated physical-owner identity is derived from source kind, producer ID, output ID, exact generated-content commitment, and owner span. Callers cannot supply an owner-equivalence identifier. Every owner row must contain exactly one derived physical descriptor; no descriptor may appear in multiple rows; unrelated descriptors cannot share a row; and every target in the row must carry exactly the row's canonical sorted `SymbolRef` set. Core derives the exposed `campaign-owner.<sha256>` from the validated physical descriptor and sorts rows by that descriptor before any owner-scoped validation or identity construction.

Each source authority binds a non-empty requested declaration span, non-empty canonical declaration span, non-empty owner span, and one authoritative declaration ID. The requested and canonical spans must lie within the owner span. Documentation span presence must agree with block state. Exactly one observation declaration must match the declaration ID, source kind and identity, block state, requested span, and documentation span. A structurally possible but uncorrelated caller projection is invalid owner authority.

All identifiers, collections, paths, spans, enum values, numeric ceilings, SHA-256 values, and UTF-8 text are bounded and validated. Output-bearing opaque IDs are 1–512 UTF-16 code units, start with an ASCII letter or digit, and thereafter contain only ASCII letters, digits, `.`, `_`, `:`, or `-`; therefore drive roots, POSIX roots, separators, whitespace, and machine paths cannot enter output. Repository paths use canonical repository-relative slash form and cannot contain a drive, root, backslash, traversal segment, NUL, or machine-absolute path. Cost currency and rate authority are both present exactly when cost enforcement is enabled. Arithmetic-bearing limits use checked integral values supplied through typed contracts.

Finite aggregate ceilings are 4,096 owners, 16,384 targets, 65,536 components, 65,536 unresolved classifications, 147,456 Audit rows, 81,920 bound-evidence rows, 65,536 owner-symbol memberships, and 131,072 Audit violations. Campaign patch bytes are at most 1 TiB; campaign input/output tokens and cost are at most `10^12`, `10^12`, and `10^15` microunits respectively; campaign elapsed time is at most 31 days. Scribe input/output tokens are at most `10^9`, elapsed milliseconds at most `2,000,000,000`, provider requests at most `10^6`, and attempts/candidates at most 1,000. Uncached tokens cannot exceed total input tokens. These are validation bounds, not permission to consume the maximum.

Validation category precedence is fixed and independent of caller collection order: root/snapshot/configuration and aggregate bounds; canonical Audit materialization; Classification/Observation exact-set validation; bound-evidence exact-set validation; Audit correlation; then owner partition, source/declaration correlation, component, and Style Profile validation. Within owner validation, rows and targets are first ordered by their canonical physical descriptors and symbol keys; source checks precede component checks, which precede Style Profile checks. Multiple defects therefore cannot change the surfaced validation category merely by permuting input rows.

For current M1 semantics, target presence is authoritative when any declaration has `ParentSubstantive`, including a malformed block. Component presence requires a well-formed `ComponentMatch.Present`; without that positive fact, complete malformed component authority is unavailable. The canonical Audit observation must agree with that accepted observation authority. Valid unsupported work becomes terminal; contradictory authority is not silently reclassified.

Failure text is bounded structural diagnostics. It cannot contain source, trivia, documentation, prompt, provider response, diff, credential, or arbitrary caller content.

## Selection and complete-block collapse

Selection policy `campaign.selection.every-current-violation.v1` selects every Audit row whose outcome is `Violation` and no compliant or skipped row as a cause.

Each violation resolves through its parent target to exactly one owner row. All target and component violations for that physical owner collapse into one work item. The nested result preserves:

- every target classification and source fact belonging to the owner;
- each target's complete parent-scoped applicable-component sequence;
- the exact target Audit row outcome, reason, and row commitment, including a compliant parent selected only by a component violation;
- every violation cause with its parent, optional component identity, Audit reason, and Audit-row commitment.

The collapse never flattens components across targets. Shared physical owners remain one terminal item containing all target facts. Under the current M2/M3 boundary an executable item has exactly one eligible method target.

## Disposition

Every selected work item is exactly one of:

- `campaign.work.executable`, with `campaign.edit.insert` for `NoBlock` or `campaign.edit.replace` for `WhitespaceOnly`/`WellFormed`; or
- `campaign.work.terminal`, with no edit capability and an ordered deduplicated reason set.

Executable work requires one unambiguous, single-symbol, supported repository method; writable exact source authority; no multi-declarator or primary-constructor condition; supported block state; only `RequiredAbsent` causes; and a Style Profile whose component policies exactly equal that target's component closure. An empty plan and terminal-only targets require no fictitious Style Profile.

Terminal reasons use this fixed primary precedence, which is also their closed enumeration order:

1. `campaign.terminal.ambiguous-owner`
2. `campaign.terminal.shared-owner`
3. `campaign.terminal.multi-declarator`
4. `campaign.terminal.primary-constructor-alias`
5. `campaign.terminal.primary-constructor`
6. `campaign.terminal.non-repository-source`
7. `campaign.terminal.non-writable-source`
8. `campaign.terminal.unsupported-target-kind`
9. `campaign.terminal.unsupported-removal`
10. `campaign.terminal.unsupported-block-state`

All applicable reasons and the primary reason are retained and identity-bearing. For example, a substantive malformed target under forbidden policy is valid terminal work with `unsupported-removal` and `unsupported-block-state`; `unsupported-removal` is primary.

## Ordering and identities

Ordering policy `campaign.order.complete-owner.ordinal.v1` sorts complete physical-owner descriptors by explicit source-kind rank, ordinal locator/generated identity, exact source commitment and repository encoding, numeric spans, authoritative declaration ID, target `SymbolRef` and classification, ordered owner symbols and parent-scoped components, target Audit facts, violation causes, and terminal reasons. No array position, caller owner-equivalence label, culture, display name, clock, random state, machine path, or OS filesystem comparer participates.

The execution commitment is lowercase SHA-256 over domain `contract-scribe/campaign-execution-commitment/v1`. The preimage consists of explicit labels and values; every label and strict-UTF-8 text value and every big-endian integer is prefixed with a four-byte big-endian byte length. It binds:

- planning, selection, and ordering revisions;
- all snapshot commitments, including the opaque binding directly;
- exact canonical `AuditJson.Write` bytes through their SHA-256;
- every typed Scribe/campaign ceiling and correctness-bearing content authority;
- the canonical ordered complete work graph, exact per-executable-target Style Profile content, and disposition.

Work-item keys use domain `contract-scribe/campaign-work-item/v1` and hash the exact execution commitment plus the complete item descriptor. The exposed key is `campaign-work.` followed by lowercase SHA-256. Keys are unique within one plan and snapshot-scoped; they are not permanent symbol or cross-repository identities. A changed opaque snapshot changes the execution commitment and every key even when Audit content is byte-identical.

## Output and privacy

`CampaignWorkPlan` contains campaign lineage, opaque snapshot binding, Audit digest, execution commitment, target profile, canonically ordered work items, and bounded summary counts. Work items contain only committed identity/source/component/Audit/Style/disposition facts required by later M4 work.

The plan does not contain source bytes or excerpts, declaration/trivia/documentation text, prompts, context content, provider requests or responses, candidates, patches or diffs, credentials, transcripts, absolute machine paths, persistence state, checkpoint history, cursor, GitHub issue/branch/commit/pull-request state, or mutation instructions.

## Conformance

The normative implementation is `CampaignPlanner` and its supporting types under `src/ContractScribe.Core/Campaign/Planning/**`. Conformance tests cover real M1 authority correlation, complete-block collapse, split/merged/extra owner attacks, exact bound-evidence and declaration correlation, substantive malformed targets, zero-component and multi-owner cases, known-answer commitments and keys, opaque-snapshot collision, exact source byte/encoding commitments, canonical configuration JSON, finite bounds, opaque-ID privacy, Style Profile content binding and target association, valid and invalid input permutation, independent-process culture replay, empty work, stable fail-closed codes, privacy markers, and infrastructure dependency exclusion. Synthetic known-answer and adversarial case catalog facts live in `tests/fixtures/campaign/planning/vectors.json`; that fixture is test evidence, not a runtime schema.
