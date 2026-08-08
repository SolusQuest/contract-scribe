# Pre-release v1 draft coordination

> **Status:** Current pre-release guidance. Policy, Taxonomy, Audit Result, and Audit CLI version `1` are unreleased drafts. Their current authority is the smallest owning document, schema or registry, semantic fixture set, and direct test suite listed below. There is no aggregate baseline manifest, protected-input closure, bundle identity, certification record, refresh lifecycle, or compatibility layer.

## Current draft ownership

| Responsibility | Current authoritative and executable surfaces |
| --- | --- |
| Policy configuration | [`policy-configuration-v1.md`](policy-configuration-v1.md), `schemas/policy-configuration/v1.schema.json`, `tests/fixtures/policy-configuration/v1`, and `PolicyConfigurationConformanceTests.cs` |
| Symbol evidence taxonomy | [`symbol-evidence-taxonomy-v1.md`](symbol-evidence-taxonomy-v1.md), the Taxonomy v1 schema and registry, `tests/fixtures/symbol-evidence-taxonomy/v1`, `ClassificationConformanceOracle.cs`, and `SymbolEvidenceTaxonomyContractTests.cs` |
| Audit Result | [`audit-result-v1.md`](audit-result-v1.md), the Audit Result v1 schema and registry, `tests/fixtures/audit-result/v1`, `AuditResultConformance.cs`, and `AuditResultContractTests.cs` |
| Cross-contract target observation | `tests/fixtures/m1-target-observation/adr-0003-vectors.json` and `M1TargetObservationDecisionTests.cs` |
| Fresh-process determinism | `ContractScribe.ContractBaselineProbe` and `tests/fixtures/audit-result/v1/process-replay-input.json` |
| Audit CLI | [`../audit-cli.md`](../audit-cli.md), `tests/fixtures/m1-audit-cli`, and `M1AuditCliContractTests.cs` |

Each implementing issue changes only the owning surfaces needed by its behavior. Direct tests may consume more than one surface when they exercise a real cross-contract invariant, but that does not create a new coordinating manifest or digest identity.

Before a formal release, incompatible corrections amend these draft version `1` surfaces in place. Obsolete draft machinery is deleted; agents do not add migrations, fallbacks, compatibility aliases, successor identities, or post-merge certification work unless an actual released or simultaneously supported consumer requires them.

## Historical Issue #55 and #70 lineage

Issue #55 and squash commit [`95933c5dc134dfe6adeb92765920a8eb5c96d7db`](https://github.com/SolusQuest/contract-scribe/commit/95933c5dc134dfe6adeb92765920a8eb5c96d7db) introduced the first coordinated M1 baseline. Issue #70 and squash commit [`67c149fbc105d2ccae94becd6b2158b68027cbfd`](https://github.com/SolusQuest/contract-scribe/commit/67c149fbc105d2ccae94becd6b2158b68027cbfd) corrected its Host Validation lineage. Those commits and their deleted manifests remain immutable Git history and can be inspected when historical provenance matters.

Issue #75 removed their current-tree manifest, consumer handshake, bundle, and validation machinery because those mechanisms had become ordinary-change gates without a released compatibility need. Historical identities and digests must not be presented as current draft authority or recreated as a prerequisite for later implementation.
