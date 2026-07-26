# Coordinated pre-release v1 baseline

## Status and identity

Issue #35 coordinates the M1 Policy/Taxonomy/Audit contract amendment selected by ADR 0003. The artifacts remain unreleased pre-release version `1` drafts because no released or coexisting consumer must distinguish the incompatible M0 and M1 shapes. M0 artifacts remain valid historical evidence only at their pinned M0 revision; there is no default profile, silent migration, or cross-revision compatibility promise.

The current machine-readable inventory is [`tests/fixtures/m1-contract-baseline/v1/manifest.json`](../../../tests/fixtures/m1-contract-baseline/v1/manifest.json). Its registry digests identify the amended working-tree contract. It intentionally does not predict a merge commit. After merge, the repository owner records the main-reachable merge commit in Issue #35 or a later main-reachable baseline update.

## Coordinated artifacts

| Responsibility | Normative or executable artifacts |
| --- | --- |
| Policy profile, contribution identity, validation stages, and selector applicability | `policy-configuration-v1.md`, `schemas/policy-configuration/v1.schema.json`, `tests/fixtures/policy-configuration/v1`, `PolicyConfigurationConformanceTests.cs` |
| Taxonomy profile/run-failure vocabulary, generated identity/locators, component subjects, and observation commitment | `symbol-evidence-taxonomy-v1.md`, Taxonomy v1 registry/schema, Taxonomy fixtures, `SymbolEvidenceTaxonomyContractTests.cs` |
| Audit profile identity, repository/generated contributions, direct observation, authority, malformed XML, and canonical ordering | `audit-result-v1.md`, Audit Result v1 registry/schema, Audit fixtures, `AuditResultContractTests.cs` |
| ADR-to-contract completeness and cross-contract fail-closed behavior | `adr-0003-vectors.json`, `tests/fixtures/m1-contract-baseline/v1`, `M1TargetObservationDecisionTests.cs`, `M1ContractBaselineTests.cs` |
| Fresh-process determinism | `ContractScribe.ContractBaselineProbe`, `process-replay-input.json`, `M1ContractBaselineTests.cs` |

The current crosswalk has one row for every identified ADR annex decision row and uses set equality in both directions. Each row records disposition, normative source, machine surface, valid and invalid vector source, oracle, and downstream implementation owner.

## Historical evidence boundary

`tests/fixtures/m1-target-observation/adr-0003-vectors.json` records the accepted decision inputs at commit `beada966b3c06e1b823e488472a9f515b87b0760`. Its baseline commit and registry digests are immutable historical provenance. Current-registry validation belongs to the separate M1 manifest and must never be represented by rewriting the ADR's historical fields.

## Production disposition

This baseline contains no production Roslyn, host, CLI, or audit-runtime implementation.

| Issue | Downstream responsibility |
| --- | --- |
| #36 | Authoritative loading/generated facts, normalized identity inputs, collision and conflict detection |
| #37 | Profile classification, component/relation emission, unrepresentable classification failure |
| #38 | Direct target/component observation, partial authority, malformed/unreadable handling |
| #39 | Policy contributions and bounded target/component/generated evidence |
| #40 | Result aggregation, canonical profile/provenance, uniqueness, ordering, amended validation |
| #24 | Host terminal mapping and atomic artifact publication/invalidation |
| #25 and #30 | CLI disposition contract and production implementation |
| #26 | Cross-platform end-to-end validation |
