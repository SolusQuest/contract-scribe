# Contract lifecycle

## Purpose

ContractScribe uses machine-readable contracts for audit, proposal, patch, state, and publication artifacts. Those contracts need stable identities and executable conformance, but their integer artifact versions must not become counters for every design correction made before the first consumable release.

This document separates three concepts:

- **Artifact version** identifies a compatibility family that consumers may need to distinguish at runtime.
- **Repository revision** identifies the exact draft semantics at a commit.
- **Lifecycle status** states whether compatibility is still draft, milestone-baselined, or released.

The repository revision is authoritative for pre-release draft semantics. A version number alone never identifies an un-released draft precisely.

The amount of workflow, review, identity, and validation machinery maintained before release is governed by [Pre-release engineering proportionality](pre-release-engineering.md). Contract integrity does not justify extra pull requests, merges, review boundaries, or repeated validation unless each protects a distinct current failure.

## Lifecycle states

### Draft

A draft contract may change incompatibly in place while the owning capability is still being designed or implemented. The artifact version may remain unchanged when no supported consumer needs the previous and current shapes to coexist.

A draft contract has one current active shape by default. Remove superseded fields, identifiers, readers, writers, fixtures, aliases, and fallback paths as part of the coordinated change; do not retain a compatibility or migration surface for an unreleased repository revision. A one-time mechanical rewrite of checked-in state may support the change, but it must not become a shipped migration framework. Historical commits and accepted evidence remain available as history rather than supported runtime inputs.

Every behavioral change must update the normative specification, schema or registry, public fixtures, conformance oracle, and dependent contract references as one coordinated change. References to draft semantics must pin a full repository commit.

There is no promise that a current implementation can read an artifact produced by a different pre-release commit merely because both artifacts carry the same integer version.

Consumers must reject unsupported artifact versions. A workflow that persists, transfers, or consumes a draft artifact across repository revisions must also bind a separate contract-baseline or provenance identity and reject a missing or mismatched identity. The integer artifact version is not a substitute for that identity. A same-revision, commit-pinned workflow may rely on its verified source and tool baseline and does not need to add the repository revision to the canonical artifact unless the owning contract explicitly requires it.

### Milestone-baselined

A milestone baseline is the exact contract revision that passed the owning milestone's validation. It is stable enough for later repository work to depend on, but it is not yet an external compatibility promise.

An incompatible amendment after the milestone closes requires:

- an explicit decision or implementation issue;
- a complete coordinated contract change;
- identification of every affected milestone, artifact, and validation record;
- rerunning the affected conformance and integration gates;
- a new pinned baseline record.

The prior baseline remains valid historical evidence for its exact commit. The new amendment does not mutate that evidence.

### Released

A contract becomes released when it is included in a downstream-consumable release or is otherwise declared a supported external compatibility surface. From that point:

- a breaking change requires a new artifact version and identifier;
- the old version remains governed by the published support, migration, and deprecation policy;
- parsers fail closed on unsupported versions;
- compatibility and coexistence behavior must be executable.

The first external compatibility freeze is owned by the first downstream-consumable release gate, not by repository visibility or by an experimental milestone alone.

## Pre-release validation provenance

Pre-release contract and implementation validation is bound to the minimum provenance required by its owning contract, not to placeholder release authorization. Host Validation and other workflow-based validation execute the applicable exact checked-out commit with the protocol, schemas, fixtures, validators, and production entry points present at that revision. Their evidence records the applicable exact source and workflow revisions, run and attempt, runner and toolchain observations, and canonical digests of the manifests and artifacts generated during that run. Local snapshots, provider evaluations, campaigns, and controlled downstream runs use their own contract-defined identities rather than inheriting GitHub Actions identity.

Run-generated digests protect transfer and aggregation inside one execution. They are evidence fields, not a checked-in authorization lifecycle. They do not require a repository-wide protected-input manifest, artifact lock, bundle ID, pending or accepted review record, review-only commit, or independent certification before another pre-release pull request may change the implementation. Ordinary pull requests update the current implementation, contracts, fixtures, and tests coherently and rely on exact-head review plus affected CI.

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
- the owning milestone needs a coordinated amendment and revalidation;
- a documentation-only editorial change clarifies behavior without changing it.

If coexistence becomes necessary before public release, the compatibility need takes precedence over the pre-release simplification and a new version is required.

That exception must satisfy the current-boundary, evidence, ownership, lifetime, and removal-condition requirements in [Pre-release engineering proportionality](pre-release-engineering.md). A hypothetical future consumer does not qualify.

## Interpretation of the M0 contract baseline

M0 established a commit-pinned, cross-consistent policy, taxonomy, and audit-result baseline with schemas, registries, fixtures, and test-only conformance oracles. That baseline was sufficient to plan M1 and remains historical evidence for the commit that passed M0.

M0 did not create a downstream-consumable release or promise indefinite compatibility for every field and identifier. The current Policy/Configuration v1, Symbol and Evidence Taxonomy v1, and Audit Result v1 may therefore be amended in place before release to complete the initial product, including target-surface and documentation-observation semantics, provided the coordinated amendment and revalidation rules above are followed.

M1 closes with a new milestone baseline for the production audit contracts. M2 through M5 may trigger explicit amendments if real implementation evidence exposes a missing requirement. Those amendments remain version 1 unless a coexistence or released-compatibility condition requires otherwise.

The Issue #70 baseline at exact squash commit `67c149fbc105d2ccae94becd6b2158b68027cbfd` (`C2`), including the manifest identity it records, remains immutable historical contract-lineage evidence for that revision. Issue #75 does not reopen #70, regenerate that historical identity, or create a successor contract-baseline revision solely to remove the Host Validation bundle-authorization lifecycle. Current Host protocol and execution semantics are identified by the exact #75 source revision; a validator dependency that exists only to enforce the historical certification lifecycle is removed rather than carried into a successor lineage record. A future semantic amendment to Policy, Taxonomy, Audit Result, or another production contract may still require its own coordinated baseline decision under this lifecycle.

## Coordinated amendment protocol

A contract amendment must:

1. State the problem, affected artifacts, and whether behavior changes.
2. Identify every normative document, schema, registry, fixture, oracle, implementation, and dependent contract in scope.
3. Update those artifacts in one PR or in an explicitly ordered PR series with no ambiguous intermediate baseline.
4. Preserve exact historical references to prior evidence.
5. Run the full affected conformance suites and cross-contract checks.
6. Record the new current baseline commit and revalidation outcome.
7. Reject unsupported artifact versions. For draft artifacts that cross repository revisions, bind and validate a separate contract-baseline or provenance identity and fail closed when it is missing or mismatched; a verified same-revision workflow may rely on its pinned source and tool baseline.

Public issue and PR text must not imply that an un-released draft is a supported external compatibility promise.

## Future contract families

The same lifecycle applies to the planned contract families:

- Style Profile;
- Documentation Proposal;
- Patch Plan and Patch Validation Result;
- Work Plan and Campaign State;
- GitHub Publication Record.

Each family begins as a draft version 1 unless its design issue establishes a concrete coexistence requirement. Version growth follows consumer compatibility needs, not milestone count.
