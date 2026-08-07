# Coordinated pre-release v1 baseline

> **Status:** Historical Issue #55/#70 lineage description pending Issue #75 cleanup. The checked-in manifest, successor identities, protected-input maintenance, bundle/review lifecycle, and reopen rules below are not current development authority. Issue #75 removes their current-tree machinery while retaining the actual Policy, Taxonomy, Audit Result, fixture, cross-contract, and downstream-ownership coverage under the current draft contracts and tests.

## Status and identity

Issue #70 coordinates a one-time successor correction to the M1 Policy/Taxonomy/Audit contract baseline after Host Validation work changed an input still owned by the Issue #55 manifest. Issue #55 and its exact squash commit remain closed, immutable historical authority; this correction establishes a new current identity instead of reopening or rewriting them. The artifacts remain unreleased pre-release version `1` drafts because no released or coexisting consumer must distinguish the incompatible M0 and M1 shapes. M0 artifacts remain valid historical evidence only at their pinned M0 revision; there is no default profile, silent migration, or cross-revision compatibility promise.

The current machine-readable inventory is [`tests/fixtures/m1-contract-baseline/v1/manifest.json`](../../../tests/fixtures/m1-contract-baseline/v1/manifest.json). Its top-level coordinating issue is Issue #70 and `contractRevision: issue-70-host-validation-baseline-lineage-v1` identifies this successor candidate. The manifest binds every current coordinated input by SHA-256 and binds its immutable predecessor: Issue #55, revision `issue-55-classification-origin-closure-v1`, exact squash commit `95933c5dc134dfe6adeb92765920a8eb5c96d7db` (`S1`), the same manifest path, and predecessor manifest digest `e89c1769ca7f725bd813d345023bfcbcf57319ffc11268423d57b6b304999a85`. The top level intentionally contains neither its own path/digest nor its future squash commit.

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

This successor revision repairs baseline lineage and closes the Host-owned consumer seam with a structurally valid Host Validation candidate. The contract manifest now owns the dedicated full-document consumer test rather than the broader Host protocol lifecycle suite. The candidate binds the exact current protected inputs and bundle members, but its baseline disposition remains `pending-main-reconciliation` and its review remains pending and non-authorizing. Its bundle ID is a content identity, not a validated-host claim. `S1`, also called contract baseline `C1`, remains the exact Issue #55 squash commit. `C2` is the future Issue #70 squash commit that will own the new Issue #70 manifest identity; this candidate does not predict `C2`.

Issues #36-#40 are completed implementation history. Issue #41 remains active validation and blocked until the separate Host Validation certification Task has produced and recorded an accepted exact bundle after `C2` is main-reachable.

Every pull request in Issues #37-#40, #24, or another work item that changes a Host protected input must refresh the protected-input manifest, direct artifact inventory when required, artifact lock, candidate bundle ID, and matching pending review ID in that same pull request, then pass structural validation, both dry-runs, self-test, and ordinary CI. A pull request that changes no protected input records that reviewed no-change disposition. Each refreshed candidate supersedes the prior pending candidate as current lineage but does not inherit or require independent acceptance.

The certification Task waits until Issue #70 and every other native blocker are closed at accepted exact commits. It records the selected stable main revision after `C2` as certification base `P0'` in the certification PR and immutable Task closure record, proves ancestry `S1 → C2 → P0' → S2 → S3`, keeps `P0'` outside the baseline and bundle/review identity preimages, and reconciles `baseline.mergeCommit` to exact `C2`. It then produces final bundle target `S2` plus a separate review-record-only squash commit `S3`. Any protected-byte change computes a new candidate bundle ID; the Issue #55 and Issue #70 pending candidates remain historical non-authorizing lineage. Ordinary Host protected-input drift receives a pending-candidate refresh. A later change to an input owned by the Issue #70 contract manifest requires another explicit successor contract-amendment baseline rather than rewriting `S1`, `C2`, or their manifests. Issue #41 may execute or publish production evidence only after the accepted `S2` review is main-reachable at `S3`.

Any protected-input or bundle-member drift after `S2`, after `S3`, or before or during Issue #41 invalidates the accepted review and current-bundle claim. The certification Task must be reopened or replaced by a linked successor, restored as a native blocker of #41 before further execution, and completed again. Affected #41 evidence and dependent #30 integration or smoke evidence are marked stale or superseded and rerun in dependency order. This certification recovery rule does not authorize reopening closed contract-baseline issues.

## Historical evidence boundary

`tests/fixtures/m1-target-observation/adr-0003-vectors.json` records the accepted decision inputs at commit `beada966b3c06e1b823e488472a9f515b87b0760`. Its baseline commit and registry digests are immutable historical provenance. Current-registry validation belongs to the separate M1 manifest and must never be represented by rewriting the ADR's historical fields.

## Production disposition

Issues #36-#40 are completed on main. Issue #70 changes only baseline identity, ownership, validation, and lifecycle artifacts; it does not reopen their accepted implementation scope. The Host candidate in this PR is explicitly pending and non-authorizing. Its later main-reachable reconciliation and independent acceptance belong to Issue #57 rather than Issue #70.

| Issue | Disposition | Downstream responsibility |
| --- | --- | --- |
| #36 | completed | Authoritative loading/generated facts, normalized identity inputs, collision and conflict detection |
| #37 | completed | Profile classification, component/relation emission, unrepresentable classification failure, and successor-baseline revalidation |
| #38 | completed | Direct target/component observation, partial authority, malformed/unreadable handling |
| #39 | completed | Policy contributions and bounded target/component/generated evidence |
| #40 | completed | Result aggregation, canonical profile/provenance, uniqueness, ordering, amended validation |
| #41 | active validation | Cross-platform and end-to-end validation of the complete M1 audit slice |
