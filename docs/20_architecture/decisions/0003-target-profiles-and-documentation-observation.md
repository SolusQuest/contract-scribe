# ADR 0003: Target profiles and direct documentation observation

Status: Accepted for M1; coordinated contract and production implementation pending

Date: 2026-07-26

Decision owner: Repository owner; this ADR becomes a repository decision through human-reviewed PR merge.

## Context and boundary

M1 needs one deterministic answer to two questions before Policy, Taxonomy, Audit Result, classification, observation, evidence, aggregation, validation, and CLI work can proceed: which C# declarations are audit targets, and what direct XML documentation means for each target and component.

This ADR decides those semantics. It does not amend the current v1 schemas, registries, conformance fixtures, oracles, production code, ADR 0001, or ADR 0002. The executable annex at `tests/fixtures/m1-target-observation/adr-0003-vectors.json` enumerates this decision against the registries pinned at commit `beada966b3c06e1b823e488472a9f515b87b0760`. ADR prose and the annex are one normative decision; disagreement blocks merge.

## Target-profile ownership

The closed profiles are `profile.external-api` and `profile.assembly-visible`. Policy owns one required run-level profile choice; it cannot vary by rule and has no default. Taxonomy owns the membership algorithm, but individual classification and relation records do not repeat the profile. Audit Result owns the effective profile as canonical result-set identity and requires it to equal the Policy choice. A standalone persisted taxonomy set requires a surrounding profile binding. The run envelope separately owns contract-baseline, repository, tool, and execution provenance; baseline identity never substitutes for profile identity.

An omitted, unknown, or mismatched profile is a run-level Policy failure. Selection occurs before per-declaration policy evaluation, so an excluded declaration produces no contribution, classification, component, or Audit Result.

## Accessibility predicates

Accessibility is evaluated as explicit predicates, never as a numeric ordering.

- `profile.external-api` selects public declarations and protected or protected-internal declarations only when every containing-type edge is externally reachable and the declaring class or interface is externally derivable.
- `profile.assembly-visible` selects public, internal, and protected-internal declarations, plus protected and private-protected declarations only when every containing edge is assembly-reachable and the declaring class or interface is derivable within the assembly.
- Private and file-local declarations are excluded.
- Nested declarations are evaluated one containing edge at a time. Sealed or static classes, structs, enums, and delegates do not provide a derivation path.
- Effective C# accessibility includes implicit defaults. The algorithm consumes semantic accessibility and containing-type facts, not modifier-token heuristics.

## Primary targets

Every current concrete `primaryKinds` entry uses the profile predicate. Classes, structs, interfaces, enums, delegates, constructors, ordinary methods, destructors, operators, conversions, properties, indexers, fields, enum members, and events are direct targets when selected. Enum members derive selection from their enum. Destructors use protected-member reachability and remain method targets.

Static constructors and other implicit constructors are not independent targets. Explicit interface implementations are relation-only. Inherited interface members do not become duplicate targets. An otherwise selected `symbol.unknown` candidate uses `support.unsupported` and `skip.unsupported.symbol-kind`.

## Components

Components exist only below a selected, supported parent. An excluded parent emits nothing. A selected parent that is unsupported, unavailable-context, partial-ambiguous, or mixed-origin-ambiguous emits only its own skipped record and no components.

| Family | Result and observation | Support and evidence |
| --- | --- | --- |
| parameter, type-parameter, return, value | Component Audit Result; observe the matching direct element on the authoritative parent block | `support.supported`; evidence subject is the parent `SymbolRef` plus exact `ComponentClassification` identity |
| accessor and backing-field | No documentation judgment | `support.not-applicable`, `skip.not-applicable.non-documentation-component` |
| named synthesized components | No documentation judgment | `origin.compiler-synthesized`, `support.not-applicable`, `skip.not-applicable.synthesized-non-target` |
| unknown | No documentation judgment | `support.unsupported`, `skip.unsupported.component-kind` |

Ordinary component origin derives from effective parent declarations. Component identity is ordinal, while XML name matching uses the declaration-local names belonging to the declaration whose documentation comment is authoritative. For a paired partial member, an implementing comment uses implementing names; a no-comment fallback uses defining names. Tags for the non-authoritative names are stale.

Return components exist only for non-void return-bearing targets. Value components apply to properties and indexers. Accessor/backing-field records remain descriptive non-documentation components; they are not silently omitted when their supported parent and component kind exist.

## Relations

All four current relation kinds remain `RelationObservation` only.

| Relation | Profile interaction | Target and evidence effect |
| --- | --- | --- |
| `relation.overrides` | Emit only for a selected source surface; endpoints are selected independently | No promotion, inherited documentation, Audit Result, support/skip state, or Audit evidence binding |
| `relation.implicit-interface-implementation` | Emit only for a selected source surface | Same |
| `relation.explicit-interface-implementation` | The implementation is relation-only inside a selected containing type for a selected interface surface | No source target/result, including when the endpoint cannot be formed |
| `relation.inherited-interface-member` | The selected derived interface is source and the original inherited member is target | No duplicate inherited-member target/result |

The semantic compilation is authoritative. A missing or ambiguous endpoint omits the relation and produces bounded diagnostics without changing direct source classification, observation, or evidence. Relation endpoints may be source or metadata `SymbolRef` values without becoming targets. Relations have no Audit Result evidence subject; source and target results bind their own evidence independently.

## Direct documentation observation

Observation is scoped to one explicit compilation context and to directly attached active-declaration trivia. It does not use symbol-expanded, inherited, metadata, or external documentation; it does not resolve `<include>` or `<inheritdoc/>`; it performs no retrieval.

For parent targets, precedence is:

1. classification skip;
2. any readable applicable declaration with directly attached substantive payload proves `documentation.present`;
3. when no positive exists, a complete readable declaration universe proving only missing or whitespace/comment/processing-instruction-only trivia yields `documentation.absent`;
4. otherwise `documentation.unavailable`.

Empty elements, `<inheritdoc/>`, unresolved `cref`, `<include>` markers, and malformed non-whitespace direct payload count as parent presence. Comments, processing instructions, and whitespace alone do not. `///` and `/** */` forms are equivalent after C# attachment and normalization; attributes do not break attachment.

Partial types aggregate all active parts. A paired partial member is special: an active implementing part with any attached comment is exclusive authority, including whitespace-only or malformed comments. Only a readable implementing part with no comment falls back to the defining part. An unreadable implementing leading-trivia region yields unavailable. A defining-only declaration removed from the compilation produces no target.

For observable components, precedence is:

1. classification skip;
2. a complete well-formed applicable block with a matching non-whitespace `<param>`, `<typeparam>`, `<returns>`, or `<value>` element proves present;
3. when no positive exists, a complete well-formed applicable universe with no matching substantive element proves absent;
4. otherwise unavailable.

Wrong or stale names do not match. Duplicate matching tags are present if any has non-whitespace content. Empty matching elements are absent when the complete universe is well formed. `<inheritdoc/>` alone does not document a component. A malformed block cannot prove any component present or absent, even when parser recovery exposes an apparent matching subtree. A positive match in another complete block still wins.

Conditional-compilation-inactive declarations are absent. No multi-target-framework merge occurs; equal documentation IDs in different compilation contexts remain distinct.

## Evidence

Record feasibility precedes skip precedence: a legal identity, authoritative locator, origin, and record shape must exist before a taxonomy skip can be emitted.

For target present, `evidenceIds` references one untruncated complete direct block proving substantive payload; another unreadable part does not negate that existential proof. Target absent requires one untruncated `evidence.source.declaration` / `evidence.declares` item for every active declaration, covering the complete leading documentation-trivia region and explicit no-block or whitespace-only state. Any substantive direct payload contradicts absence.

Component evidence keeps the parent `SymbolRef` subject and exact component identity. Component present references an untruncated complete block containing the exact match. Component absent requires the complete declaration/leading-trivia universe, every complete block, explicit no-block proof, and authoritative local-name mapping. Parent summary XML, wrong-name tags, and sibling-component tags are allowed and do not contradict component absence; only a substantive exact match does.

Every required item must be listed in `evidenceIds`; bundle membership alone does not bind it. Truncated, omitted, unreadable, unenumerable, or over-budget required evidence uses the existing evidence-incomplete or source-unavailable path and publishes no compliant/violation judgment.

A complete readable malformed block that makes a supported component semantically unavailable requires the coordinated reason `audit.reason.documentation-unavailable.malformed-xml`: skipped outcome, retained valid Policy expectation/resolution, `documentation.unavailable`, at least one contribution, `evidence.bundle.complete`, no omission reason, `requiresEvidence: true`, and the untruncated malformed XML item in `evidenceIds`. Its precedence is immediately after `audit.reason.documentation-unavailable` and before `audit.reason.evidence-incomplete`. This differs from source-unavailable and budget-exhausted evidence-incomplete.

## Partial, origin, and failure precedence

One original-definition symbol with consistently bound active parts is supported. Source/trivia unreadability after classification affects observation, not classification. A unique parent whose parts or complete component identities cannot be assigned deterministically is `support.ambiguous` with `skip.ambiguous.partial-declaration`; components are omitted. Consistently bound parts with mixed provenance use `origin.mixed` and `skip.ambiguous.mixed-origin`. When both partial-membership and mixed-origin ambiguity are established, partial-declaration precedence wins and #35 makes the `origin.mixed` combination representable.

Missing documentation ID with a legal candidate locator and proven origin produces one unresolved record per typed candidate key with `skip.unavailable.documentation-comment-id`. Missing generated provenance with formable target/component identity uses `origin.unknown` and `skip.unavailable.generated-provenance`. Missing semantic context preserves known origin and uses `skip.unavailable.semantic-context`; generated-provenance precedence wins when both are unavailable.

`origin.unknown` plus the documentation-ID skip is illegal and is not invented. If documentation ID and provenance cannot form a legal unresolved record, or no deterministic locator exists, classification fails at run level and publishes no classification or Audit Result artifact. Distinct unresolved candidates are deduplicated by typed key; same-key conflicting origins fail the run rather than choosing by enumeration order.

The responsibility chain is fixed: #34 decides no artifact; #35 adds contract identifiers/representation only when required; #37 detects normalized classification failure; #24 maps host terminal state and stale-artifact behavior; #25 defines CLI disposition/exit contract; #30 implements it; #26 validates the complete path.

## Generated identity and policy

Repository-backed tool-generated source uses existing repository project/source identity. Pathless generated declarations never invent repository paths. Any Policy rule declaring `sourcePaths` is wholly inapplicable to pathless generated output, including exclude-only rules; project-only/global rules and the default can apply.

The owning project identity is normalized repository-relative `projectPath`. A generated contribution identity is the tagged tuple (`projectPath`, producer kind, producer ID, output ID). Repository and generated identities are disjoint domains, ordered repository then generated and then by their ordinal typed keys. All declarations in one producer/output share one contribution.

Source-generator unresolved candidates retain the existing `generatedSource` locator. #35 adds a tool-only `toolGenerated` candidate variant (`producerId`, `outputId`, optional span) ordered repository, generatedSource, toolGenerated, synthetic. #35 separately adds a shared `generatedOutput` EvidenceItem locator (`producerKind`, `producerId`, `outputId`, `sourceSha256`, optional span) ordered after metadata and before synthetic. Candidate and evidence locators are never interchangeable. Spans are zero-based UTF-16, end-exclusive coordinates in the full generated text. `sourceSha256` binds the complete `SourceText` character stream encoded as UTF-8 without BOM; EvidenceItem `sha256` separately binds its original evidence-region text.

Canonical producer/output IDs use strict byte framing. For each identity, hash: the ASCII domain; `00`; then for each field, a four-byte unsigned big-endian UTF-8 byte length and the field bytes. Fields are independently NFC-normalized and encoded as strict UTF-8 without BOM. Source-generator fields are opaque authoritative Roslyn strings: any non-empty valid Unicode value up to 4096 UTF-8 bytes is accepted, hashed, and never published raw; no path, URI, username, or secret-like interpretation is applied. Tool-adapter namespace, producer name, and output name use the closed ASCII grammar `^[A-Za-z][A-Za-z0-9._-]{0,127}$`; slash, backslash, colon, whitespace, control characters, empty values, and non-ASCII values are rejected identically on every platform.

| ID | Domain | Fields |
| --- | --- | --- |
| `sgp.<hex>` | `contract-scribe/sgp/v1` | generator full metadata type name; Roslyn `AssemblyIdentity.GetDisplayName(fullKey: false)` |
| `sgo.<hex>` | `contract-scribe/sgo/v1` | exact generator hint name |
| `tgp.<hex>` | `contract-scribe/tgp/v1` | trusted in-process adapter producer namespace; producer name |
| `tgo.<hex>` | `contract-scribe/tgo/v1` | trusted in-process adapter output name |

The tool adapter facts are a non-serialized #36 internal seam, not a new machine contract. #36 constructs and validates authoritative facts, collision handling, generated text, project/context association, and public safety; later stages consume the same facts. Case differences remain distinct; NFC-equivalent source-generator spellings share identity. A hash collision between distinct normalized payloads, conflicting authority for one key, missing identity, or tool field outside the closed grammar fails closed before publication.

## Compatibility

All affected contracts remain unreleased pre-release version `1` drafts. #35 establishes a new commit-pinned M1 baseline and reruns conformance; no incompatible released consumer must coexist. M0 version-1 policies without a profile remain historical artifacts only at their pinned M0 baseline and are invalid at the amended M1 baseline. There is no silent default or automatic migration. Missing or mismatched cross-revision baseline identity is rejected.

## Coordinated impact map

| Issue | Responsibility |
| --- | --- |
| #35 | Policy/Taxonomy/Audit schemas, registries, fixtures, oracles, generated candidate/evidence locators, target profile, generated contributions, malformed-XML reason, canonical ordering, cross-contract conformance |
| #36 | Production loading seam for authoritative project/context/generated provenance, normalized producer/output identities, generated source text, fail-closed/public-safe facts |
| #37 | Profile classification, component/relation emission, unresolved and run-level classification failures |
| #38 | Direct parent/component observation, partial authority, malformed/unreadable handling |
| #39 | Policy contributions and bounded target/component/generated evidence |
| #40 | Aggregation, canonical profile/provenance, uniqueness, ordering, amended result validation |
| #24 | Host terminal mapping, cancellation, artifact invalidation/publication |
| #25 / #30 | CLI contract and implementation, terminology, diagnostics, exit behavior |
| #26 | End-to-end validation, cross-platform determinism, failure and public-safety vectors |

The executable vectors use this closed amendment-surface vocabulary for #35:

- `audit-result.component-evidence`
- `audit-result.documentation-observation`
- `audit-result.generated-contribution`
- `audit-result.generated-evidence-locator`
- `audit-result.malformed-xml`
- `audit-result.profile`
- `audit-result.target-evidence`
- `policy.generated-contribution`
- `policy.input-error`
- `policy.target-profile`
- `taxonomy.failure-contract`
- `taxonomy.generated-candidate-locator`
- `taxonomy.generated-evidence-locator`
- `taxonomy.profile-membership`
- `taxonomy.profile-vocabulary`

This PR changes none of those surfaces.

## Post-MVP exclusions

Prose quality, tag completeness beyond represented components, inherited-content quality, rendered documentation, proposals, and automated fixes are outside M1 deterministic audit semantics. Relation-derived context may be used by later proposal work but never changes this direct-observation decision.

## Consequences and risks

The decision is deterministic and fail-closed, but the coordinated v1 amendment is intentionally incompatible with the pinned M0 draft baseline. Generated identity is canonical without publishing raw paths or names. Direct-presence existential evidence avoids turning one unreadable partial part into a false negative; absence remains more expensive because it requires a complete declaration universe. Malformed parent payload counts as direct documentation while component parsing remains fail-closed.

## References

- [Issue #34](https://github.com/SolusQuest/contract-scribe/issues/34)
- [M1 implementation plan](../../90_roadmap/m1-plan.md)
- [Policy configuration v1](../contracts/policy-configuration-v1.md)
- [Symbol/evidence taxonomy v1](../contracts/symbol-evidence-taxonomy-v1.md)
- [Audit result v1](../contracts/audit-result-v1.md)
- [ADR 0002](0002-process-topology.md)
