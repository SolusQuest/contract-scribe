# Audit Result v1

## Authority and boundary

Audit Result v1 is a provisional, repository-neutral artifact for one deterministic documentation-audit result set. This contract composes the M0.1 policy/configuration and M0.3 symbol/evidence taxonomy contracts; it does not redefine their identifiers or implement their evaluators.

The normative rules in this document own audit outcomes, observations, policy aggregation, result-level evidence binding, reason precedence, and canonical property order. `schemas/audit-result/v1.schema.json` owns the result envelope shape. `schemas/audit-result/v1.registry.json` owns audit identifiers. The test-only oracle owns cross-contract and semantic validation, including full validation of embedded M0.3 records and evidence bundles against their checked-in schema and registry.

## Document shape

The top-level object has `auditResultVersion`, `policyConfigurationVersion`, and `taxonomyRegistryVersion`, all integer `1`, followed by `results`. Every result contains one embedded M0.3 `TargetClassification`, `ComponentClassification`, or `UnresolvedClassification`; `RelationObservation` is not an audit result in v1.

The result field order is `classification`, `policyContributions`, `policyExpectation`, `policyResolution`, `documentationObservation`, `auditOutcome`, `reasonCode`, `evidenceIds`, `evidenceBundle`. M0.2 owns the canonical property order of embedded M0.3 objects while M0.3 owns their shape and semantics:

- `TargetClassification`: `recordType`, `symbolRef`, `primaryKind`, `traits`, `origin`, `supportStatus`, `skipReason`.
- `ComponentClassification`: `recordType`, `parentSymbolRef`, `componentKind`, `identity`, `origin`, `supportStatus`, `skipReason`.
- `UnresolvedClassification`: `recordType`, `compilationContextRef`, `origin`, `supportStatus`, `skipReason`, `candidateLocator`.
- `SymbolRef`: `compilationContextRef`, `documentationCommentId`.
- Candidate locators: `repository(path, span)`, `generatedSource(generatorId, hintNameId, span)`, `synthetic(fixtureId)`.
- Evidence bundle: `evidenceBundleVersion`, `availabilityStatus`, `omissionReason`, `items`.
- Evidence item: `evidenceId`, `subject`, `kind`, `relation`, `excerpt`, `sha256`, `originalUtf8ByteCount`, `includedUtf8ByteCount`, `omittedUtf8ByteCount`, `isTruncated`, `locator`.
- Evidence locators: repository `path, span`; metadata `assemblyIdentity, documentationCommentId`; synthetic `fixtureId`; span `start, end`.

M0.3 optional properties are omitted when absent. M0.2-owned fields use explicit `null` only where the legal combination below permits it. Repository-relative policy paths are canonical values; absolute host and environment-derived paths are excluded.

## Policy aggregation

Each `policyContributions` entry records one M0.1 evaluation: `projectPath`, `sourcePath`, `policyExpectation`, and `matchedRuleId`. Paths use M0.1 lexical repository-relative rules. The `(projectPath, sourcePath)` pair is unique; a duplicate pair is invalid. `matchedRuleId` is `null` for default fallback.

An empty contribution array derives `policyResolution: unavailable`. One contribution derives `single`; multiple contributions with one expectation derive `all-declarations-agree`; different expectations derive `conflict` and `policyExpectation: null`. `matchedRuleId` provenance is preserved per contribution rather than reduced to an unordered set.

## Observation, outcome, and reasons

The v1 observation vocabulary is `documentation.present`, `documentation.absent`, and `documentation.unavailable`. `present` means a bounded documentation payload exists. XML parsing quality, whitespace policy, `<inheritdoc/>`, malformed XML, and detection implementation are outside this contract.

For a supported classification with usable policy and documentation:

| Policy expectation | Observation | Outcome | Reason |
| --- | --- | --- | --- |
| required | present | compliant | `audit.reason.required-present` |
| required | absent | violation | `audit.reason.required-absent` |
| optional | present | compliant | `audit.reason.optional-present` |
| optional | absent | compliant | `audit.reason.optional-absent` |
| forbidden | present | violation | `audit.reason.forbidden-present` |
| forbidden | absent | compliant | `audit.reason.forbidden-absent` |

`reasonCode` is always audit-owned. A classification skip uses `audit.reason.classification-skipped`; its precise taxonomy `skipReason` remains in the embedded M0.3 record and is not copied into the audit registry. Other skip reasons are `audit.reason.policy-conflict`, `audit.reason.policy-unavailable`, `audit.reason.documentation-unavailable`, and `audit.reason.evidence-incomplete`.

Primary-reason precedence is classification skip, policy conflict/unavailable, documentation unavailable, evidence incomplete, then the matrix reason. The selected row fixes all fields. A classification skip may retain contributions but forces `policyExpectation: null`, `policyResolution: unavailable`, `documentationObservation: null`, `evidenceIds: []`, and an unavailable bundle with `evidence.omission.not-provided`. Policy conflict retains contributions, forces `policyExpectation: null`, keeps `policyResolution: conflict`, and uses the same unavailable bundle. An empty contribution array with no higher-precedence reason is policy-unavailable. Documentation unavailable retains valid policy fields, uses `documentation.unavailable`, and an unavailable bundle with `evidence.omission.source-unavailable`. Evidence incomplete retains valid policy fields, uses `documentation.unavailable`, and a partial bundle with `evidence.omission.budget-exhausted`.

Policy contract errors are not per-symbol results. Every M0.3 non-supported classification status is represented as a skipped audit result when present; supported classifications have no taxonomy skip.

## Evidence binding

Each result has its own M0.3-conformant evidence bundle, preserving M0.3's 32-item and 32,768-byte limits and its availability semantics. A present compliant/violation result requires an untruncated item for the same subject with `kind: evidence.source.xml-documentation` and `relation: evidence.documents`. An absent compliant/violation result requires an untruncated same-subject declaration item with `kind: evidence.source.declaration` and `relation: evidence.declares`, and rejects contradictory same-subject XML-documentation evidence.

Target evidence subjects match the target `SymbolRef`. Component evidence subjects match the parent `SymbolRef`, because M0.3 v1 evidence subjects do not identify components. Unresolved results have no evidence references. All references resolve within the result bundle, are unique and ordinally sorted, and target untruncated items. Cross-context, mismatched, dangling, or duplicate references fail closed.

`evidence.bundle.partial` is legal only for `audit.reason.evidence-incomplete`; it has `audit.outcome.skipped`, `documentation.unavailable`, empty `evidenceIds`, `evidence.omission.budget-exhausted`, and M0.3-required non-empty items and omission. A partial or unavailable bundle never supports a compliant or violation outcome.

## Canonical JSON

Canonical bytes are valid UTF-8 without BOM, one compact JSON document followed by exactly one LF, with no other insignificant whitespace. Duplicate properties are rejected. Arrays are ordered by: result classification type (`TargetClassification`, `ComponentClassification`, `UnresolvedClassification`) then full subject key; component parent `SymbolRef`, `componentKind`, `identity`; unresolved M0.3 locator order; policy contribution `projectPath` then `sourcePath`; and M0.3 evidence IDs/items.

Strings preserve Unicode scalar sequences without normalization and compare ordinally. Non-ASCII scalars are direct; quotation mark, reverse solidus, and control characters are escaped with the short JSON escapes for LF, CR, TAB, BACKSPACE, and FORM FEED, and lowercase `\\u00xx` for other controls. Solidus, U+2028, and U+2029 are not escaped. Unpaired UTF-16 surrogates are rejected. Numbers are signed integers in ordinary decimal notation; zero is `0`, negative zero, leading zero, fractional, and exponent forms are forbidden.

Canonical serialization sorts logically unordered input. A separate test-only validator rejects noncanonical bytes. Run metadata, timestamps, durations, environment, command lines, derived summaries, and migration fields are not canonical members. No automatic migration or unknown-field preservation is defined.

## Non-goals

No Roslyn/MSBuild loading, XML documentation detection/parsing, policy discovery, filesystem access, production audit runtime/API, CLI command, M0.4 experimental JSON coupling, production serializer, evidence search/ranking/trust/excerpt generation, SARIF/severity/localization, summaries, baselines, incremental diff, streaming/persistence format, provider/proposal/prompt, patch generation/validation, GitHub adapter, migration tool, runtime error transport, telemetry, or amendment to M0.1/M0.3.
