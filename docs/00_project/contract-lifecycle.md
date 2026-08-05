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

## Validation-bundle certification lifecycle

Contract revision and validation-bundle certification are related but separate lifecycles. A current pre-release contract draft does not need to become an independently accepted production-evidence baseline after every coordinated amendment.

The validation-bundle lifecycle is:

| State | Meaning | Permitted use |
| --- | --- | --- |
| Stale or invalid | A protected input changed without a matching current identity, or structural validation otherwise failed. This is a validator result, not an authorizing serialized state. | Diagnose drift and rebuild a candidate; no evidence-consuming or publishing operation is authorized. |
| Candidate or pending reconciliation | The exact current protected inputs, bundle members, bundle identity, and pending review are structurally closed, but the bundle has not been independently accepted. | Structural validation, deterministic dry-runs, harness self-test, and continued pre-release implementation only. |
| Accepted | One exact main-reachable bundle has an independent accepted review bound to its content and reviewed revision. | Production evidence execution, acceptance, aggregation, and publication allowed by the owning protocol. |

A coordinated pre-release contract amendment may close with a candidate bundle when its downstream implementation can proceed without consuming production validation evidence. The owning roadmap must name the later evidence-consuming gate and the issue responsible for promotion. Promotion is required before the first such gate, not immediately after every draft amendment.

Any protected-input or bundle-member drift invalidates the prior current identity and cannot inherit an older accepted review. When a repository keeps structural bundle validation in ordinary CI, every pull request that changes a protected input must restore a structurally valid pending candidate in that same pull request: regenerate the protected-input manifest, update the direct artifact inventory when its closed set changes, regenerate the artifact lock and candidate bundle identity, regenerate the matching non-authorizing pending review and review identity, and pass the structural validator, deterministic dry-runs, harness self-test, and ordinary CI. A pull request that does not change protected inputs does not refresh the candidate or create a separate no-change record; its diff and ordinary CI provide that evidence. The replaced pending bundle remains historical candidate lineage and gains no authorizing status.

That mechanical candidate refresh is not independent acceptance. A milestone promotion selects one stable main-reachable candidate only after every native protected-input-owner blocker needed by the evidence gate is closed at an accepted exact commit. Any later-discovered protected-input owner is added as a native blocker and must also close before promotion begins.

A certification design that requires more than one pull request for one primary outcome, whether serial or parallel, more than one human merge, or any required post-merge mutation stage must pass the pre-release process-complexity checkpoint. Prefer content-bound acceptance when it protects the actual integrity boundary. If exact post-merge commit identity is required, document the irreversible consumer or authorization failure that a content identity cannot prevent, and use proportional validation for any later review-record-only change.

This separation does not relax released compatibility, evidence integrity, or exact-revision requirements. An accepted bundle authorizes only the exact identity it binds, and an older accepted baseline remains historical evidence for its exact revision rather than authority for changed current bytes.

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
