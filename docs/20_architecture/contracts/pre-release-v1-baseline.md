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

## Historical evidence boundary

`tests/fixtures/m1-target-observation/adr-0003-vectors.json` records the accepted decision inputs at commit `beada966b3c06e1b823e488472a9f515b87b0760`. Its baseline commit and registry digests are immutable historical provenance. Current-registry validation belongs to the separate M1 manifest and must never be represented by rewriting the ADR's historical fields.

## Production disposition

Issue #36's authoritative loading and generated-fact foundation is completed. The main branch also contains classification implementation work from the prematurely merged PR #54, but Issue #55 reopens the governing contract and baseline; that implementation is not accepted as satisfying Issue #37 until it is revalidated against this successor baseline. The Host candidate in this PR is explicitly pending and non-authorizing until a later exact main-reachable reconciliation.

| Issue | Disposition | Downstream responsibility |
| --- | --- | --- |
| #36 | completed | Authoritative loading/generated facts, normalized identity inputs, collision and conflict detection |
| #37 | active implementation | Profile classification, component/relation emission, unrepresentable classification failure, and successor-baseline revalidation |
| #38 | active implementation | Direct target/component observation, partial authority, malformed/unreadable handling |
| #39 | active implementation | Policy contributions and bounded target/component/generated evidence |
| #40 | active implementation | Result aggregation, canonical profile/provenance, uniqueness, ordering, amended validation |
| #41 | active validation | Cross-platform and end-to-end validation of the complete M1 audit slice |
