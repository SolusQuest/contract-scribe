# Coordinated pre-release v1 baseline

## Status and identity

Issue #75 owns the current M1 Policy/Taxonomy/Audit contract-baseline identity used by Host Validation. The artifacts remain unreleased pre-release version `1` drafts: no released or coexisting consumer needs a compatibility layer, migration, alias, or fallback for an older repository revision. Earlier manifests and commits remain immutable historical evidence, not supported runtime inputs.

The current machine-readable inventory is [`tests/fixtures/m1-contract-baseline/v1/manifest.json`](../../../tests/fixtures/m1-contract-baseline/v1/manifest.json). Its top-level identity is Issue #75 and `contractRevision: m1-host-validation-content-bound-execution-v1`. The manifest binds every current coordinated input by SHA-256 and binds its immutable predecessor: Issue #70, revision `issue-70-host-validation-baseline-lineage-v1`, exact squash commit `67c149fbc105d2ccae94becd6b2158b68027cbfd`, the same manifest path, and predecessor digest `4ca9d7d7ba60650a1a3838486fc80f6d44e22cfbf451f07c47e4aa4796d5c7b2`. The top level intentionally contains neither its own path/digest nor a predicted merge commit.

## Coordinated artifacts

| Responsibility | Normative or executable artifacts |
| --- | --- |
| Policy profile, contribution identity, validation stages, and selector applicability | `policy-configuration-v1.md`, `schemas/policy-configuration/v1.schema.json`, `tests/fixtures/policy-configuration/v1`, `PolicyConfigurationConformanceTests.cs` |
| Taxonomy profile/run-failure vocabulary, generated identity/locators, component subjects, observation commitment, and origin/skip applicability | `symbol-evidence-taxonomy-v1.md`, Taxonomy v1 registry/schema, Taxonomy fixtures, `classification-origin-skip-vectors.json`, `ClassificationConformanceOracle.cs`, `SymbolEvidenceTaxonomyContractTests.cs` |
| Audit profile identity, repository/generated contributions, direct observation, authority, malformed XML, raw candidate-locator validation, and canonical ordering | `audit-result-v1.md`, Audit Result v1 registry/schema, Audit fixtures, `repository-candidate-locator-vectors.json`, `AuditResultConformance.cs`, `AuditResultContractTests.cs` |
| ADR-to-contract completeness and cross-contract fail-closed behavior | `adr-0003-vectors.json`, `tests/fixtures/m1-contract-baseline/v1`, `M1TargetObservationDecisionTests.cs`, `M1ContractBaselineTests.cs`, `AuditResultSemanticValidator.cs` |
| Fresh-process determinism | `ContractScribe.ContractBaselineProbe`, `process-replay-input.json`, `M1ContractBaselineTests.cs` |

The current crosswalk has one row for every identified ADR annex decision row and uses set equality in both directions. Each row records disposition, normative source, machine surface, valid and invalid vector source, oracle, and downstream implementation owner.

## Validation-bundle authorization

The Host Validation bundle is authorized by its canonical content identity, `bundleId`. The artifact lock records the exact path and digest of every protected bundle member; the review record remains outside that content preimage to avoid self-reference. Neither authorization nor pending state contains an active baseline disposition, merge-commit reconciliation, main-reachability gate, or post-merge review stage.

The checked-in non-authorizing state is one canonical pending review with verdict `pending`, null review metadata, and exactly one blocking finding ID: `independent-review.pending`. A substantive independent review examines the exact Commit A protected content and canonical bundle ID. Its accepted record binds that same `bundleId`, independent Relay provenance, zero blocking findings, and a lowercase 40-hex `reviewedSourceRevision`. That revision is audit metadata for what the reviewer saw; review validation deliberately performs no Git object, ancestry, tree, or blob lookup for it.

Issue #75 uses one issue, one implementation pull request, one human merge, and no post-merge mutation. Commit A contains all protected content and the canonical pending record. After the exact Commit A bundle passes substantive review, Commit B changes only `tests/fixtures/m1-host-validation/v1/independent-review.json` to the accepted record. The exact Commit B head must pass ordinary CI and the explicit Host Validation execution workflow before human merge. Because the review file is outside the bundle preimage, Commit B does not change the reviewed content identity. Any other protected-content change after review invalidates the authorization and requires a new Commit A review within the same pull request.

The correction run proves that the accepted bundle, repository-owned materializer, Ubuntu X64 and Windows X64 cells, exact artifact set, aggregation, and final passing gate work together on the reviewed source. It is not Issue #41's final production evidence claim. After Issue #75 merges, Issue #41 owns final exact-revision Host evidence; #30 and #42 own their later integration and independent-smoke evidence. No closed baseline or certification issue is reopened to refresh current content.

## Historical evidence boundary

Issue #55, Issue #70, Issue #57, and their exact commits and bundle/review records remain immutable historical evidence. Issue #57's two-commit `S2`/`S3` acceptance topology describes that completed run only and is not an active lifecycle or reusable requirement. Current validators must not accept its obsolete pending-main-reconciliation fields, main-reachability states, review field names, supersession model, or post-merge second-PR behavior.

`tests/fixtures/m1-target-observation/adr-0003-vectors.json` records the accepted decision inputs at commit `beada966b3c06e1b823e488472a9f515b87b0760`. Its baseline commit and registry digests are immutable historical provenance. Current-registry validation belongs to the current M1 manifest and must never be represented by rewriting the ADR's historical fields.

## Production disposition

Issues #36-#40 are completed on main. Issue #75 changes Host Validation identity, review authorization, materialization, evidence aggregation, and workflow execution; it does not reopen their accepted implementation scope.

| Issue | Disposition | Downstream responsibility |
| --- | --- | --- |
| #36 | completed | Authoritative loading/generated facts, normalized identity inputs, collision and conflict detection |
| #37 | completed | Profile classification, component/relation emission, unrepresentable classification failure, and successor-baseline revalidation |
| #38 | completed | Direct target/component observation, partial authority, malformed/unreadable handling |
| #39 | completed | Policy contributions and bounded target/component/generated evidence |
| #40 | completed | Result aggregation, canonical profile/provenance, uniqueness, ordering, amended validation |
| #41 | blocked by #75 | Final cross-platform and end-to-end validation of the complete M1 audit slice |
