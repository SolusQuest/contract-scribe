# Contract lifecycle

Apply Solus Book's [Contract lifecycle](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/contract-lifecycle.md) for draft replacement, supported obligations, and historical evidence. This document owns ContractScribe's integer-version scheme, transfer provenance, release freeze, and milestone interpretation. [Pre-release engineering](pre-release-engineering.md) owns the local process budget.

## Integer artifact versions

Machine contracts begin at integer version `1` when their implementing milestone needs an executable producer-consumer boundary. This applies to Style Profile, Documentation Proposal, Patch Plan and Patch Validation Result, Work Plan and Campaign State, and GitHub Publication Record; a roadmap placeholder alone does not create a family.

The repository revision identifies exact draft semantics. Equal integer versions do not promise that one pre-release commit can read another's artifacts. Consumers reject unsupported artifact versions.

Increment the version when a supported consumer must read an earlier incompatible shape, incompatible shapes must coexist, persisted data cannot be safely interpreted without distinguishing semantics, or a compatibility/migration policy needs a machine-visible boundary. A version declared supported outside a commit-pinned repository workflow also establishes that boundary. Coexistence requires a new version even before public release, with its current obligation, owner, lifetime, and removal condition recorded under the shared lifecycle.

## Released

A contract becomes released through a downstream-consumable release or an explicit supported external surface. Its first external compatibility freeze belongs to that release gate. A breaking change then requires a new artifact version and identifier, published old-version support/migration/deprecation disposition, fail-closed rejection of unsupported versions, and executable compatibility/coexistence behavior.

## Coherent draft-contract change

Use the shared [Draft changes](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/contract-lifecycle.md#draft-changes) rule. A ContractScribe change states the problem, affected artifacts, and whether product behavior changes; updates the actually affected normative specification, producer, consumer, schema/registry, fixture, validator, implementation, tests, and dependent references in one coherent PR; and runs affected conformance, cross-contract, and integration checks. It must not imply that an unreleased draft is externally supported.

Living work uses the current main-reachable draft under [Source of truth](source-of-truth.md). A separate decision issue is required only for unresolved materially different semantics; a successor baseline is required only for an external consumer, coexisting persisted state, or irreversible authority that must distinguish revisions.

## Pre-release validation provenance

Host Validation and other workflow validation bind the applicable source and workflow revisions, unique run and attempt, and required job conclusions. Runner/toolchain facts or artifact digests are recorded only when they affect the claim or protect an actual transfer. Other capabilities use the minimum provenance in their own contracts and do not inherit GitHub Actions identity merely because Host Validation uses it.

Add a cross-revision provenance identity only for an artifact actually persisted or transferred for another revision to consume, when source/run metadata cannot safely bind the transfer. Its contract names the stale-state or substitution failure prevented. Same-revision workflows use verified source and relevant tool baseline without adding revision fields to every canonical artifact. A real cross-job transfer may use a small run-local digest/result envelope; it remains an evidence field.

Repository-only milestone evidence, internal handoffs, ordinary CI artifacts, or later issues do not justify checked-in Host Validation or release-authorizing bundles, artifact locks, protected-input manifests, pending/accepted review fixtures, review-only commits, or independent bundle certification. Historical validation records remain evidence at their original revisions; they are not active protected inputs or execution authority.

The first downstream-consumable release gate owns any persistent release-authorizing bundle, signed attestation, protected-input identity, or signature. It must identify the external consumer or irreversible publication boundary, use then-current release inputs, and define support and replacement. This restriction permits exact-revision milestone evidence, bounded public/private smoke attestations, necessary draft contract-baseline/provenance identities, and M1–M5 snapshot, work-plan, batch, campaign, ledger, cursor, checkpoint, and operation identities for real deterministic replay, stale-state rejection, or idempotent mutation. Define them at the implementing state transition, rather than reserving placeholder identity families.

## Interpretation of the M0 contract baseline

M0 established commit-pinned, cross-consistent Policy/Configuration, Symbol and Evidence Taxonomy, and Audit Result evidence with schemas, registries, fixtures, and test-only conformance oracles. It was sufficient to plan M1 and remains evidence for its passing commit; it did not release those contracts.

The current v1 contracts may be amended in place before release, including target-surface and documentation-observation semantics, through the coherent change and affected checks above. M1 closes with the exact production audit revision and passing exit checks. M2–M5 can correct missing draft requirements while retaining version `1` unless coexistence or supported compatibility requires a new version.

Issue #70 evidence at squash commit `67c149fbc105d2ccae94becd6b2158b68027cbfd` (`C2`), including its recorded manifest identity, remains immutable history. Issue #75 did not reopen #70, regenerate that identity, or create a successor merely to remove Host Validation lifecycle machinery. Current Host semantics belong to production source, contracts, and tests; dependencies maintained solely for historical certification or experiment state were removed.
