# Coordinated pre-release v1 baseline

## Status and identity

Issue #55 coordinates the successor M1 Policy/Taxonomy/Audit contract baseline after the Issue #35 baseline exposed a classification-origin inconsistency. The artifacts remain unreleased pre-release version `1` drafts because no released or coexisting consumer must distinguish the incompatible M0 and M1 shapes. M0 artifacts remain valid historical evidence only at their pinned M0 revision; there is no default profile, silent migration, or cross-revision compatibility promise.

The current machine-readable inventory is [`tests/fixtures/m1-contract-baseline/v1/manifest.json`](../../../tests/fixtures/m1-contract-baseline/v1/manifest.json). `contractRevision: issue-55-classification-origin-closure-v1` identifies this successor candidate. The manifest binds every current coordinated input by SHA-256 and binds its immutable predecessor: Issue #35, revision `issue-35-pre-release-v1`, merge commit `bb4654edc180e2953dda6b89a29211b18778b78e`, and predecessor manifest digest `2872387ce9cfd8578c8f473ec26ab9f10dd44381edfbc0248e6fa370d797ab31`. It intentionally does not predict its own future squash commit.

## Coordinated artifacts

| Responsibility | Normative or executable artifacts |
| --- | --- |
| Policy profile, contribution identity, validation stages, and selector applicability | `policy-configuration-v1.md`, `schemas/policy-configuration/v1.schema.json`, `tests/fixtures/policy-configuration/v1`, `PolicyConfigurationConformanceTests.cs` |
| Taxonomy profile/run-failure vocabulary, generated identity/locators, component subjects, observation commitment, and origin/skip applicability | `symbol-evidence-taxonomy-v1.md`, Taxonomy v1 registry/schema, Taxonomy fixtures, `classification-origin-skip-vectors.json`, `ClassificationConformanceOracle.cs`, `SymbolEvidenceTaxonomyContractTests.cs` |
| Audit profile identity, repository/generated contributions, direct observation, authority, malformed XML, raw candidate-locator validation, and canonical ordering | `audit-result-v1.md`, Audit Result v1 registry/schema, Audit fixtures, `repository-candidate-locator-vectors.json`, `AuditResultConformance.cs`, `AuditResultContractTests.cs` |
| ADR-to-contract completeness and cross-contract fail-closed behavior | `adr-0003-vectors.json`, `tests/fixtures/m1-contract-baseline/v1`, `M1TargetObservationDecisionTests.cs`, `M1ContractBaselineTests.cs`, `AuditResultSemanticValidator.cs` |
| Fresh-process determinism | `ContractScribe.ContractBaselineProbe`, `process-replay-input.json`, `M1ContractBaselineTests.cs` |

The current crosswalk has one row for every identified ADR annex decision row and uses set equality in both directions. Each row records disposition, normative source, machine surface, valid and invalid vector source, oracle, and downstream implementation owner.

## Validation-bundle disposition

This successor revision closes the contract-content defect with a structurally valid Host Validation candidate. The candidate binds the exact current protected inputs and bundle members, but its baseline disposition remains `pending-main-reconciliation` and its review remains pending and non-authorizing. Its bundle ID is a content identity, not a validated-host claim. `S1`, also called contract baseline `C1`, is the exact squash commit that records this contract-content baseline and its `issue-55-classification-origin-closure-v1` manifest identity.

After exact-main closure, Issues #37-#40 are rebound to `S1`, the successor `contractRevision`, and the exact manifest path and digest while preserving their original sibling dependencies. Their implementation acceptance does not depend on an accepted Host bundle. Issue #37 must additionally revalidate the implementation already merged by PR #54 against exact `S1`. Issue #41 remains blocked until a separate Host Validation certification Task has produced and recorded an accepted exact bundle.

Every pull request in Issues #37-#40, #24, or another work item that changes a Host protected input must refresh the protected-input manifest, direct artifact inventory when required, artifact lock, candidate bundle ID, and matching pending review ID in that same pull request, then pass structural validation, both dry-runs, self-test, and ordinary CI. A pull request that changes no protected input records that reviewed no-change disposition. Each refreshed candidate supersedes the prior pending candidate as current lineage but does not inherit or require independent acceptance.

The certification Task waits until native blockers #24 and #37-#40, plus every later-discovered protected-input owner added as a blocker, are closed at accepted exact commits. It records the selected stable main revision as certification base `P0` in the certification PR and immutable Task closure record, proves ancestry `S1 → P0 → S2 → S3`, keeps `P0` outside the baseline and bundle/review identity preimages, keeps `S1/C1` as the contract baseline unless a later contract-amendment issue explicitly supersedes it, and produces final bundle target `S2` plus a separate review-record-only squash commit `S3`. Any protected-byte change computes a new candidate bundle ID; the Issue #55 pending candidate remains historical non-authorizing lineage. Ordinary Host protected-input drift receives a pending-candidate refresh, while drift in an `S1` contract-manifest-owned input requires an explicit successor contract-amendment baseline before certification may retain or replace `baseline.mergeCommit = S1`. Issue #41 may execute or publish production evidence only after the accepted `S2` review is main-reachable at `S3`.

Any protected-input or bundle-member drift after `S2`, after `S3`, or before or during Issue #41 invalidates the accepted review and current-bundle claim. The certification Task must be reopened or replaced by a linked successor, restored as a native blocker of #41 before further execution, and completed again. Affected #41 evidence and dependent #30 integration or smoke evidence are marked stale or superseded and rerun in dependency order.

## Historical evidence boundary

`tests/fixtures/m1-target-observation/adr-0003-vectors.json` records the accepted decision inputs at commit `beada966b3c06e1b823e488472a9f515b87b0760`. Its baseline commit and registry digests are immutable historical provenance. Current-registry validation belongs to the separate M1 manifest and must never be represented by rewriting the ADR's historical fields.

## Production disposition

Issue #36's authoritative loading and generated-fact foundation is completed. The main branch also contains classification implementation work from the prematurely merged PR #54, but Issue #55 reopens the governing contract and baseline; that implementation is not accepted as satisfying Issue #37 until it is revalidated against this successor baseline. The Host candidate in this PR is explicitly pending and non-authorizing. Its later main-reachable reconciliation and independent acceptance belong to the separate promotion Task rather than Issue #55.

| Issue | Disposition | Downstream responsibility |
| --- | --- | --- |
| #36 | completed | Authoritative loading/generated facts, normalized identity inputs, collision and conflict detection |
| #37 | active implementation | Profile classification, component/relation emission, unrepresentable classification failure, and successor-baseline revalidation |
| #38 | active implementation | Direct target/component observation, partial authority, malformed/unreadable handling |
| #39 | active implementation | Policy contributions and bounded target/component/generated evidence |
| #40 | active implementation | Result aggregation, canonical profile/provenance, uniqueness, ordering, amended validation |
| #41 | active validation | Cross-platform and end-to-end validation of the complete M1 audit slice |
