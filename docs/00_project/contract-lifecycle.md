# Contract lifecycle

## Purpose

ContractScribe uses machine-readable contracts where audit, proposal, patch, state, or publication boundaries need them. Those contracts need executable conformance, but their integer artifact versions and milestone history must not become counters or active authorities for every design correction made before the first consumable release.

This document separates four concepts:

- **Artifact version** identifies a compatibility family that consumers may need to distinguish at runtime.
- **Repository revision** identifies the exact draft semantics at a commit.
- **Release status** states whether compatibility is still draft or has become released.
- **Milestone evidence** records what an exact historical revision proved without becoming another compatibility state.

The repository revision is authoritative for pre-release draft semantics. A version number alone never identifies an un-released draft precisely.

The amount of workflow, review, identity, and validation machinery maintained before release is governed by [Pre-release engineering proportionality](pre-release-engineering.md). Contract integrity does not justify extra pull requests, merges, review boundaries, or repeated validation unless each protects a distinct current failure.

## Lifecycle states

### Draft

A draft contract may change incompatibly in place while the owning capability is still being designed or implemented. The artifact version may remain unchanged when no supported consumer needs the previous and current shapes to coexist.

A draft contract has one current active shape by default. Remove superseded fields, identifiers, readers, writers, fixtures, aliases, and fallback paths as part of the coordinated change; do not retain a compatibility or migration surface for an unreleased repository revision. A one-time mechanical rewrite of checked-in state may support the change, but it must not become a shipped migration framework. Historical commits and accepted evidence remain available as history rather than supported runtime inputs.

Every behavioral change updates the normative specification, producers, consumers, schemas or registries, fixtures, validators, tests, and dependent references that are actually affected. Do not create or touch an artifact merely to complete a standard checklist. Living references to current draft semantics may follow `main`; immutable historical evidence pins the exact repository commit that produced it.

There is no promise that a current implementation can read an artifact produced by a different pre-release commit merely because both artifacts carry the same integer version.

Consumers must reject unsupported artifact versions. A workflow adds a cross-revision provenance identity only when it actually persists or transfers an artifact produced by one revision for consumption by another and cannot safely bind that transfer through the workflow's source/run metadata. The owning contract names the concrete stale-state or substitution failure being prevented. A same-revision workflow relies on its verified source and relevant tool baseline and does not add repository revision fields to every canonical artifact.

### Historical milestone evidence

A milestone may record the exact contract revision and checks that passed when it closed. That record is historical evidence for that revision, not an active lifecycle status, compatibility promise, protected input, or authorization boundary for later development.

Later repository work uses the current main-reachable draft. An incompatible pre-release change ordinarily requires only one coherent change that:

- states any changed product semantics;
- updates the actually affected producer-consumer path;
- runs the affected conformance and integration coverage;
- leaves the prior milestone evidence unchanged at its original commit.

A separate decision issue is needed only when materially different semantics remain unresolved. A successor baseline record is needed only when a real external consumer, coexisting persisted state, or irreversible authority must distinguish the two revisions.

### Released

A contract becomes released when it is included in a downstream-consumable release or is otherwise declared a supported external compatibility surface. From that point:

- a breaking change requires a new artifact version and identifier;
- the old version remains governed by the published support, migration, and deprecation policy;
- parsers fail closed on unsupported versions;
- compatibility and coexistence behavior must be executable.

The first external compatibility freeze is owned by the first downstream-consumable release gate, not by repository visibility or by an experimental milestone alone.

## Pre-release validation provenance

Pre-release contract and implementation validation is bound to the minimum provenance required by the concrete claim, not to placeholder release authorization. Workflow-based validation executes the applicable checked-out commit with the production entry points, fixtures, and tests present at that revision. Its evidence normally consists of the source and workflow revisions, run and attempt, and required job conclusions. Record runner, toolchain, or artifact digests only when they affect the claim or protect an actual transfer.

When one job actually transfers an artifact to another, a small run-local digest or result envelope may protect that transfer. It is an evidence field, not a checked-in authorization lifecycle or a reason to reproduce the CI system as a second machine protocol. Ordinary pull requests update the current implementation, contracts, fixtures, and tests coherently and rely on exact-head review plus affected CI.

Historical validation bundles and reviews remain immutable evidence for the exact revisions that produced them, but they are not active inputs, compatibility surfaces, or authorization for current execution. Removing their current-tree machinery does not rewrite that history. A later pre-release change does not invalidate an older historical claim about its own exact revision; it only means new current evidence must run against the new exact revision when a downstream milestone depends on it.

The first downstream-consumable release gate owns any decision to introduce a persistent release-authorizing bundle, signed release attestation, release protected-input identity, signature, or released compatibility family. That decision must identify the concrete external consumer or irreversible publication boundary, build the identity from the then-current release inputs, and define support and replacement behavior. No pre-release milestone maintains placeholder release machinery in anticipation of that decision. This does not prohibit exact-revision milestone evidence, bounded public or private smoke attestations, draft contract-baseline or provenance identities, or snapshot, work-plan, batch, campaign, ledger, cursor, checkpoint, and operation identities required by M1 through M5.

## When to increment an artifact version

Increment the artifact version when any of the following is true:

- a released consumer must continue to read the previous incompatible shape;
- old and new incompatible artifacts must coexist in one implementation or state store;
- a persisted artifact cannot be safely interpreted without distinguishing the old semantics;
- a migration or compatibility policy needs a machine-visible boundary;
- a prior version has been declared supported outside a commit-pinned repository workflow.

Do not increment the artifact version only because:

- a draft schema gains a field before release;
- a draft identifier or precedence rule is corrected;
- an implementation experiment reveals a missing requirement;
- a milestone closed against an earlier draft revision;
- a documentation-only editorial change clarifies behavior without changing it.

If coexistence becomes necessary before public release, the compatibility need takes precedence over the pre-release simplification and a new version is required.

That exception must satisfy the current-boundary, evidence, ownership, lifetime, and removal-condition requirements in [Pre-release engineering proportionality](pre-release-engineering.md). A hypothetical future consumer does not qualify.

## Interpretation of the M0 contract baseline

M0 established commit-pinned, cross-consistent policy, taxonomy, and audit-result evidence with schemas, registries, fixtures, and test-only conformance oracles. It was sufficient to plan M1 and remains historical evidence for the commit that passed M0.

M0 did not create a downstream-consumable release or promise indefinite compatibility for every field and identifier. The current Policy/Configuration v1, Symbol and Evidence Taxonomy v1, and Audit Result v1 may therefore be amended in place before release to complete the initial product, including target-surface and documentation-observation semantics, provided the coherent draft-change and affected-validation rules below are followed.

M1 closes with links to the exact production audit revision and checks that satisfied its exit criteria. That closure does not create a new active compatibility state. M2 through M5 may change the current draft in place when implementation evidence exposes a missing requirement; those changes remain version 1 unless a coexistence or released-compatibility condition requires otherwise.

The Issue #70 evidence at exact squash commit `67c149fbc105d2ccae94becd6b2158b68027cbfd` (`C2`), including the manifest identity recorded at that revision, remains immutable history. Issue #75 does not reopen #70, regenerate that identity, or create a successor solely to remove Host Validation lifecycle machinery. Current Host semantics are defined by the current production source, contracts, and tests. A dependency that exists only to enforce historical certification or experiment state is removed rather than carried into a successor record.

## Coherent draft-contract change

A pre-release contract change must:

1. State the problem, affected artifacts, and whether behavior changes.
2. Identify the actually affected normative document, producer, consumer, schema or registry, fixture, validator, implementation, test, and dependent contract surfaces.
3. Update those surfaces in one coherent PR by default.
4. Preserve historical evidence at its original commit without keeping obsolete current-tree readers or manifests.
5. Run the affected conformance and cross-contract checks.
6. Reject unsupported artifact versions. Add cross-revision identity only when a concrete persisted or transferred artifact requires it.

Public issue and PR text must not imply that an un-released draft is a supported external compatibility promise.

## Future contract families

The same lifecycle applies to the planned contract families:

- Style Profile;
- Documentation Proposal;
- Patch Plan and Patch Validation Result;
- Work Plan and Campaign State;
- GitHub Publication Record.

Create each family only when its implementing milestone needs an executable producer-consumer boundary. It begins as draft version 1 unless a concrete coexistence requirement already exists. Version growth follows consumer compatibility needs, not milestone count or an earlier roadmap placeholder.
