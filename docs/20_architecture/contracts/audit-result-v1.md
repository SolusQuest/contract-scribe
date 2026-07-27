# Audit Result v1

## Authority and boundary

Audit Result v1 is a provisional, repository-neutral artifact for one deterministic documentation-audit result set. This contract composes the M0.1 policy/configuration and M0.3 symbol/evidence taxonomy contracts; it does not redefine their identifiers or implement their evaluators.

The normative rules in this document own audit outcomes, observations, policy aggregation, result-level evidence binding, reason precedence, and canonical property order. `schemas/audit-result/v1.schema.json` owns the result envelope shape. `schemas/audit-result/v1.registry.json` owns audit identifiers. The test-only oracle owns cross-contract and semantic validation, including full validation of embedded M0.3 records and evidence bundles against their checked-in schema and registry.

This artifact is a pre-release draft governed by [Contract lifecycle](../../00_project/contract-lifecycle.md). M0 validated the exact commit-pinned v1 baseline. Before the first downstream-consumable release, a coordinated Policy/Taxonomy/Audit amendment may retain the v1 artifact number when no implementation must distinguish incompatible revisions at runtime. The amendment must update every normative artifact and rerun cross-contract conformance. Released or coexisting incompatible semantics require a new artifact version.

A consumer rejects an unsupported `auditResultVersion`. A workflow that persists, transfers, or consumes this draft result across repository revisions must separately bind and validate the contract-baseline or provenance identity; `auditResultVersion: 1` alone cannot distinguish incompatible draft revisions. A verified same-revision workflow may rely on its pinned source and tool baseline.

## Document shape

The top-level object has `auditResultVersion`, `policyConfigurationVersion`, and `taxonomyRegistryVersion`, all integer `1`, followed by required `targetProfile` and `results`. `targetProfile` is exactly the Policy-selected `profile.external-api` or `profile.assembly-visible`; there is no default and every result belongs to that one run-level profile. Every result contains one embedded M0.3 `TargetClassification`, `ComponentClassification`, or `UnresolvedClassification`; `RelationObservation` is not an audit result in v1.

The result field order is `classification`, `policyContributions`, `policyExpectation`, `policyResolution`, `documentationObservation`, `auditOutcome`, `reasonCode`, `evidenceIds`, conditionally required `evidenceAuthority`, and `evidenceBundle`. M0.2 owns the canonical property order of embedded M0.3 objects while M0.3 owns their shape and semantics:

- `TargetClassification`: `recordType`, `symbolRef`, `primaryKind`, `traits`, `origin`, `supportStatus`, `skipReason`.
- `ComponentClassification`: `recordType`, `parentSymbolRef`, `componentKind`, `identity`, `origin`, `supportStatus`, `skipReason`.
- `UnresolvedClassification`: `recordType`, `compilationContextRef`, `origin`, `supportStatus`, `skipReason`, `candidateLocator`.
- `SymbolRef`: `compilationContextRef`, `documentationCommentId`.
- Candidate locators: `repository(path, span)`, `generatedSource(generatorId, hintNameId, span)`, `toolGenerated(producerId, outputId, span)`, `synthetic(fixtureId)`.
- Evidence bundle: `evidenceBundleVersion`, `availabilityStatus`, `omissionReason`, `items`, conditionally required `observationSubject`.
- Evidence item: `evidenceId`, `subject`, `kind`, `relation`, `excerpt`, `sha256`, `originalUtf8ByteCount`, `includedUtf8ByteCount`, `omittedUtf8ByteCount`, `isTruncated`, `locator`.
- Evidence locators: repository `path, span`; generated output `producerKind, producerId, outputId, sourceSha256, span`; metadata `assemblyIdentity, documentationCommentId`; synthetic `fixtureId`; span `start, end`.

M0.3 optional properties are omitted when absent. M0.2-owned fields use explicit `null` only where the legal combination below permits it. Repository-relative policy paths are canonical values; absolute host and environment-derived paths are excluded.

## Policy aggregation

Each `policyContributions` entry records one M0.1 evaluation. A repository contribution is `projectPath`, `sourcePath`, `policyExpectation`, and `matchedRuleId`. A pathless generated contribution replaces `sourcePath` with `generatedOutput(producerKind, producerId, outputId)`. Paths use M0.1 lexical repository-relative rules. Repository `(projectPath, sourcePath)` and generated `(projectPath, producerKind, producerId, outputId)` keys are independently unique. `matchedRuleId` is `null` for default fallback.

An empty contribution array derives `policyResolution: unavailable`. One contribution derives `single`; multiple contributions with one expectation derive `all-declarations-agree`; different expectations derive `conflict` and `policyExpectation: null`. `matchedRuleId` provenance is preserved per contribution rather than reduced to an unordered set.

## Observation, outcome, and reasons

The v1 observation vocabulary is `documentation.present`, `documentation.absent`, and `documentation.unavailable`. Observation is direct on the selected target or exact component subject; inheritance and `<inheritdoc/>` do not synthesize presence. A target is present when its authoritative direct block has substantive payload, even when that payload is malformed XML. A component is present only when a complete well-formed applicable block has a substantive exact-name match. No authoritative block or component match is absent only when the complete authoritative declaration set proves it; whitespace-only content is absence. A readable malformed component block that prevents exact matching is unavailable with `audit.reason.documentation-unavailable.malformed-xml`; it never degrades to component absence.

For a supported classification with usable policy and documentation:

| Policy expectation | Observation | Outcome | Reason |
| --- | --- | --- | --- |
| required | present | compliant | `audit.reason.required-present` |
| required | absent | violation | `audit.reason.required-absent` |
| optional | present | compliant | `audit.reason.optional-present` |
| optional | absent | compliant | `audit.reason.optional-absent` |
| forbidden | present | violation | `audit.reason.forbidden-present` |
| forbidden | absent | compliant | `audit.reason.forbidden-absent` |

`reasonCode` is always audit-owned. A classification skip uses `audit.reason.classification-skipped`; its precise taxonomy `skipReason` remains in the embedded M0.3 record and is not copied into the audit registry. Other skip reasons are `audit.reason.policy-conflict`, `audit.reason.policy-unavailable`, `audit.reason.documentation-unavailable`, `audit.reason.documentation-unavailable.malformed-xml`, and `audit.reason.evidence-incomplete`.

Primary-reason precedence is classification skip, policy conflict/unavailable, generic documentation unavailable, malformed XML, evidence incomplete, then the matrix reason. The selected row fixes all fields. A classification skip may retain contributions but forces `policyExpectation: null`, `policyResolution: unavailable`, `documentationObservation: null`, `evidenceIds: []`, and an unavailable bundle with `evidence.omission.not-provided`. Policy conflict retains contributions, forces `policyExpectation: null`, keeps `policyResolution: conflict`, and uses the same unavailable bundle. An empty contribution array with no higher-precedence reason is policy-unavailable. Generic documentation unavailable retains valid policy fields, uses `documentation.unavailable`, and an unavailable bundle with `evidence.omission.source-unavailable`. Malformed XML retains a complete bundle and declaration-authority evidence but produces a skipped/unavailable observation. Evidence incomplete retains valid policy fields, uses `documentation.unavailable`, and a partial bundle with `evidence.omission.budget-exhausted`.

Policy contract errors are not per-symbol results. Every M0.3 non-supported classification status is represented as a skipped audit result when present; supported classifications have no taxonomy skip.

## Evidence binding

Each result has its own M0.3-conformant evidence bundle, preserving M0.3's 32-item and 32,768-byte limits and its availability semantics. A present compliant/violation result requires an untruncated item for the same subject with `kind: evidence.source.xml-documentation` and `relation: evidence.documents`. An absent compliant/violation result requires an untruncated same-subject declaration item with `kind: evidence.source.declaration` and `relation: evidence.declares`, and rejects contradictory same-subject XML-documentation evidence.

Target evidence subjects match the target `SymbolRef`. Component evidence subjects match the exact parent/kind/identity triple. Unresolved results have no evidence references. All references resolve within the result bundle, are exact, unique, ordinally sorted, and target untruncated items. Cross-context, cross-component, mismatched, dangling, duplicated, or truncated references fail closed.

Ordinary present/absent results and malformed-XML results must carry `evidenceAuthority`, and their bundles must carry the paired `observationSubject`. Classification skips, Policy skips, generic source-unavailable skips, and evidence-incomplete skips forbid both fields. Authority has `dset.<sha256>`, completeness `complete` or `positive-only`, and unique declaration rows. Each row has `decl.<sha256>`, an authority role, block state, and exact evidence ID. Parameter/type-parameter rows also require `componentLocalName`; well-formed/no-block component rows require `componentMatch`, while malformed rows forbid it because malformed XML proves neither match nor absence. Other component kinds forbid the local name, and target rows forbid all component fields. `positive-only` is legal only for presence; absence and malformed XML require `complete`.

The rows form exactly one authority mode: one `ordinary` row; one or more `partial-type-part` rows and no other role; exactly one `partial-member-implementing` row; or exactly one `partial-member-defining-fallback` row. Target observation is derived from those rows: any authoritative `well-formed` or `malformed` substantive block is present; otherwise a complete set containing only `no-block` or `whitespace-only` rows is absent; every other state is unavailable. Component observation is also derived: any well-formed present match wins, including over a separate malformed block; otherwise any malformed row is unavailable; otherwise a complete set of no-block, whitespace-only, or well-formed absent rows is absent; every other state is unavailable. The claimed observation, reason, and outcome must agree with that derived value.

Every malformed authority row references the exact bound evidence item. That item is untruncated `evidence.source.xml-documentation` with relation `evidence.documents`, is included in `evidenceIds`, and uses the exact target or component subject. A malformed row cannot borrow a sibling declaration item or a non-XML declaration excerpt.

The declaration rows serialize the authoritative ordinary/partial declaration selection, including implementing-versus-defining fallback and whitespace/no-block state. The rows are sorted ordinally by declaration ID and each row uses the normative property order before hashing or serialization. Their canonical-array SHA-256 and count must equal the independent Taxonomy EvidenceBundle `observationSubject` commitment. Input array and object-property enumeration order are not semantic. This prevents an Audit producer from removing a declaration and merely recomputing its own local hash. Wrong member names, declaration roles, contexts, components, counts, or evidence subjects fail closed.

`evidence.bundle.partial` is legal only for `audit.reason.evidence-incomplete`; it has `audit.outcome.skipped`, `documentation.unavailable`, empty `evidenceIds`, `evidence.omission.budget-exhausted`, and M0.3-required non-empty items and omission. A partial or unavailable bundle never supports a compliant or violation outcome.

## Canonical JSON

Canonical bytes are valid UTF-8 without BOM, one compact JSON document followed by exactly one LF, with no other insignificant whitespace. Duplicate properties are rejected. The top-level property order is versions, `targetProfile`, then `results`. Arrays are ordered by: result classification type (`TargetClassification`, `ComponentClassification`, `UnresolvedClassification`) then full subject key; component parent `SymbolRef`, `componentKind`, `identity`; unresolved M0.3 locator order; repository policy contribution key before generated contribution key; declaration/evidence IDs; and M0.3 evidence items.

Strings preserve Unicode scalar sequences without normalization and compare ordinally. Non-ASCII scalars are direct; quotation mark, reverse solidus, and control characters are escaped with the short JSON escapes for LF, CR, TAB, BACKSPACE, and FORM FEED, and lowercase `\\u00xx` for other controls. Solidus, U+2028, and U+2029 are not escaped. Unpaired UTF-16 surrogates are rejected. Numbers are signed integers in ordinary decimal notation; zero is `0`, negative zero, leading zero, fractional, and exponent forms are forbidden.

Canonical serialization sorts logically unordered input. A separate test-only validator rejects noncanonical bytes. Run metadata, timestamps, durations, environment, command lines, derived summaries, and migration fields are not canonical members. No automatic migration or unknown-field preservation is defined.

The contract-baseline or provenance identity required for a cross-revision draft workflow is carried by the surrounding run envelope, manifest, state record, or other explicitly defined transport boundary. It is not added to canonical Audit Result v1.

## Non-goals

No Roslyn/MSBuild loading, production XML observation implementation, policy discovery, filesystem access, production audit runtime/API, CLI command, M0.4 experimental JSON coupling, production serializer, evidence search/ranking/trust/excerpt generation, SARIF/severity/localization, summaries, baselines, incremental diff, streaming/persistence format, provider/proposal/prompt, patch generation/validation, GitHub adapter, migration tool, runtime error transport, or telemetry. Issues #36-#40 own production implementation; this coordinated amendment owns only the machine contract and public conformance baseline.
