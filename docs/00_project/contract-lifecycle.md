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

## Validation-bundle authorization lifecycle

Contract revision and validation-bundle authorization are related but separate. A pre-release contract draft does not need an independent production-evidence certification after every coordinated amendment. Authorization protects the canonical bundle content that a consumer actually executes; it does not manufacture a second commit-identity lifecycle around review metadata.

The active validation-bundle states are:

| State | Meaning | Permitted use |
| --- | --- | --- |
| Stale or invalid | Protected bytes do not match a structurally closed identity, or structural validation failed. This is a validator result, not a serialized lifecycle state. | Diagnose drift and rebuild the current bundle; no evidence-consuming or publishing operation is authorized. |
| Locked with pending review | The exact protected inputs and bundle members produce one canonical bundle ID, and the checked-in review has the one canonical non-authorizing pending shape. | Structural validation, deterministic dry-runs, harness self-test, and continued implementation. |
| Content-reviewed | An independent accepted review binds the exact canonical bundle ID with zero blocking findings. | Evidence execution and aggregation permitted by the owning protocol, subject to that execution's separate source, workflow, run, runner, and artifact identities. |

Review authorization is content-bound. A `reviewedSourceRevision` may record the exact source revision inspected for audit, but review validation treats it as lowercase 40-hex metadata only; it must not query Git objects, ancestry, trees, or blobs to turn the review into commit-bound authorization. Execution provenance may independently prove Git source and workflow relationships where the evidence claim actually needs them.

Any protected-input or bundle-member drift computes a new bundle identity and invalidates the older review for current execution. Restore the current pending identity and review the new content in the same pull request that owns the change. Do not reopen a closed issue, add a compatibility alias, preserve an obsolete lifecycle reader, or create a required post-merge mutation simply to refresh pre-release current content. Historical commits and evidence remain immutable without remaining active inputs.

For one primary pre-release outcome, the default is one issue, one pull request, one human merge, and zero post-merge mutation stages. When the review record is intentionally outside the bundle preimage, the same pull request may use two commits: Commit A contains all protected content and a pending record; after substantive review of exact A, Commit B changes only that review record. CI and any explicit execution gate run on exact B before merge. A protected-content change after review restarts the review within the same pull request.

A design requiring additional pull requests, human merges, post-merge mutations, or a closed-issue reopen must pass the pre-release process-complexity checkpoint and identify the concrete irreversible consumer or authorization failure that content binding cannot prevent. Tracker reachability, convenient sequencing, or a desire to certify review metadata are not sufficient reasons.

This simplification does not relax released compatibility or evidence integrity. An accepted review authorizes only the exact bundle content it binds, and exact-revision execution evidence must still bind and validate the source, workflow run, runner cells, terminal artifacts, and aggregate it actually claims.

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
