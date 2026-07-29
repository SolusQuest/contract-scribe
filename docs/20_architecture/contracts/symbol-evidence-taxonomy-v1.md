# Symbol and Evidence Taxonomy v1

## Authority and status

This pre-release contract defines taxonomy semantics and compatibility for an exact repository revision. The [registry](../../../schemas/symbol-evidence-taxonomy/v1.registry.json) owns the closed V1 identifiers; the [schema](../../../schemas/symbol-evidence-taxonomy/v1.schema.json) owns JSON shape; fixtures own executable conformance vectors. A disagreement is an implementation failure. A conforming artifact has no extension point; unknown properties, identifiers, or versions fail validation.

The artifact follows [Contract lifecycle](../../00_project/contract-lifecycle.md). M0 established a commit-pinned v1 baseline, not an external compatibility promise. Before the first downstream-consumable release, a coordinated contract amendment may add or change v1 identifiers and shape when incompatible revisions do not need to coexist, provided the registry, schema, fixtures, dependent audit contract, and cross-contract conformance are updated together. Released or coexisting incompatible semantics require a new integer artifact version.

## Classification boundary

`TargetClassification` contains `SymbolRef`, one primary kind, ordered traits, origin, support status, and an optional primary skip reason. `ComponentClassification` contains its parent `SymbolRef`, component kind and identity, origin, support status, and optional primary skip reason. `RelationObservation` alone represents relations. `UnresolvedClassification` has a compilation context and candidate locator when no `SymbolRef` can be formed. The required run-level Policy `targetProfile` selects membership and is not repeated on any classification record.

`SymbolRef` is an ordinal pair of opaque `compilationContextRef` (`^[a-z0-9][a-z0-9._-]{0,127}$`) and an exact, non-empty, original-definition XML documentation comment ID. The latter is never normalized. Its ordering is context then documentation ID. Every relation and evidence subject uses the full pair.

`profile.external-api` selects a public top-level type. At each nested containing edge it selects public, or protected/protected-internal only when the containing declaration is an externally derivable class or interface. A member below an externally reachable type is selected when public, or when protected/protected-internal and its declaring class or interface is externally derivable.

`profile.assembly-visible` selects public or internal top-level types. At each nested containing edge and for members it selects public, internal, or protected-internal; it also selects protected/private-protected only when the declaring class or interface is derivable within the assembly. Private and file-local declarations are excluded in both profiles. Effective semantic accessibility includes implicit defaults. Sealed/static classes, structs, enums, and delegates do not provide a derivation path, so protected/private-protected declarations requiring such a path are excluded. Every containing edge must independently satisfy the selected profile.

Static constructors are not reachable. Explicit interface implementations are relation-only; the selected interface member is the target. Inherited interface members do not create duplicate targets.

Compiler-synthesized symbols never create targets. Source-generator and tool-generated declarations may. Origins aggregate to source, source-generator, tool-generated, mixed, compiler-synthesized, or unknown. Unknown origin is allowed only with unavailable context. Only synthesized forms named by the V1 registry create components; all other synthesized forms create no record.

| Declaration provenance | Aggregated origin |
| --- | --- |
| all handwritten source | `origin.source` |
| all manifest-marked source-generator source | `origin.source-generator` |
| all manifest-marked tool-generated source | `origin.tool-generated` |
| more than one source provenance | `origin.mixed` |
| no source declaration | `origin.compiler-synthesized` |
| required provenance unavailable | `origin.unknown` with unavailable-context |

The manifest supplies fixture provenance; the test-only classifier never infers it from names, headers, or filesystem paths.

## Components and relations

Parameters belong to methods, constructors, operators, conversions, indexers, or delegates; type parameters to named types, delegates, or methods; returns to ordinary methods/operators/conversions/delegates; values/getters/setters/init to properties/indexers; add/remove to events; backing fields to properties/events. Destructors are method targets but have no return component. Component identities are `parameter/N`, `type-parameter/N`, `return`, `value`, and `accessor/<name>`, where `N` is a zero-based ordinal. Explicit source record-copy constructors are ordinary constructor targets; synthesized copy/default constructors and registered delegate/record members are non-target components.

An override points from overriding member to original-definition base member. Interface relations point from implementation to interface member; a derived interface points to its inherited original member. A relation target may be a source or metadata symbol when its full `SymbolRef` is formable. Multiple observations sort by relation ID then full target `SymbolRef`.

| Component kind | Parent primary kind | Identity |
| --- | --- | --- |
| `component.parameter` | method, constructor, operator, conversion, indexer, delegate | `parameter/N` |
| `component.type-parameter` | class, struct, interface, delegate, method | `type-parameter/N` |
| `component.return` | ordinary method, operator, conversion, delegate | `return` |
| `component.value` | property, indexer | `value` |
| `component.accessor.get`, `set`, `init` | property, indexer | `accessor/get`, `accessor/set`, `accessor/init` |
| `component.accessor.add`, `remove` | event | `accessor/add`, `accessor/remove` |
| `component.backing-field` | property, event | `backing-field` |
| record positional property | owning record | `synthesized/record-positional-property/N` |
| implicit constructor | owning type | `synthesized/implicit-constructor` |
| record copy constructor | owning record | `synthesized/record-copy-constructor` |
| delegate Invoke | owning delegate | `synthesized/delegate-invoke` |
| delegate BeginInvoke | owning delegate | `synthesized/delegate-begin-invoke` |
| delegate EndInvoke | owning delegate | `synthesized/delegate-end-invoke` |

`N` is the zero-based ordinal within the parent. Unknown components use `unknown/N`, ordered by candidate locator. A source-declared primary constructor is a constructor target; its parameters are ordinary parameter components. Positional record properties, implicit constructors, compiler-generated copy constructors, and `Invoke`/`BeginInvoke`/`EndInvoke` use only their named synthesized component kinds with compiler-synthesized origin and not-applicable status. All other compiler-generated symbols are outside V1 and produce no record.

| Relation kind | Source | Target |
| --- | --- | --- |
| `relation.overrides` | overriding member | overridden original-definition member |
| `relation.implicit-interface-implementation` | implementing member | implemented interface member |
| `relation.explicit-interface-implementation` | explicit implementation declaration | implemented reachable interface member |
| `relation.inherited-interface-member` | derived interface type | inherited original-definition interface member |

An explicit implementation never emits a target record. One source may have multiple observations, sorted ordinally by relation ID then target `SymbolRef`.

All valid interface member forms keep their normal primary kind. Default body-bearing instance members add `trait.virtual`; static abstract members add `trait.static` and `trait.abstract`; static virtual/default members add `trait.static` and `trait.virtual`. A reachable source destructor is a supported method target; one in a non-derivable container is not reachable.

## Status, skips, and compatibility

Supported records have no skip. Every other record has exactly one skip. Precedence is documentation-comment-id unavailable, generated provenance unavailable, semantic context unavailable, applicable unknown kind, partial ambiguity, mixed-origin ambiguity, synthesized non-target, then non-documentation component. The registry is authoritative for the exact values. An unrecognized primary/component uses its reserved unknown kind and the matching unsupported skip. Unknown required IDs, schema versions, and malformed bundles are contract failures rather than skips.

Registry identifiers are lowercase ASCII dotted identifiers, compare ordinally, and are opaque to consumers. Within a milestone-baselined or released revision they are never silently reused. `sectionEntryTypes` is a closed exact map over `sections`. Existing classification/evidence sections are `record-vocabulary` and retain `applicability`, a non-empty `recordTypes` set, and compatibility metadata. `targetProfiles` is `profile-vocabulary`; its entries define run-level reachability and are not legal classification/status/skip values. `runFailures` is `run-failure`; its entries define a producing stage, downstream implementation issue, and `serialized: false`, and are never serialized as classifications, skips, statuses, audit reasons, or ordinary results. Every entry has `deprecated`/`replacementId` metadata. Current entries have both values null.

The run failures are `run.classification.unrepresentable`, `run.generated.identity-collision`, `run.generated.authority-conflict`, and `run.generated.missing-identity`. They stop the run before ordinary Audit Result production. Issues #36 and #37 own production detection/transport; this contract owns the identifiers and fail-closed boundary.

Before release, adding an ID, changing a definition, or changing a required document member follows the coordinated amendment protocol and may retain version `1` when no incompatible revision must coexist. After release, the same changes require a new integer artifact version. Editorial changes that do not change behavior require neither a new artifact version nor compatibility handling.

When several conditions apply, choose exactly one skip in this order: missing documentation comment ID; unavailable generated provenance; unavailable semantic context; unknown primary/component kind; ambiguous partial declaration; mixed origin; synthesized non-target; non-documentation component. `skip.unavailable.generated-provenance` requires exactly `origin.unknown`. Missing documentation identity or semantic context preserves a known non-synthesized origin: `origin.source`, `origin.source-generator`, `origin.tool-generated`, or `origin.mixed`. Generated-provenance precedence wins when generated provenance and semantic context are both unavailable. `origin.mixed` normally uses `skip.ambiguous.mixed-origin`; when partial-membership ambiguity also applies, `skip.ambiguous.partial-declaration` wins while origin remains `origin.mixed`.

An unresolved candidate has its compilation context, origin, status, skip, and exactly one candidate locator. Candidate locators are repository, generated-source, tool-generated, or synthetic; their order is repository, generated-source, tool-generated, synthetic. Within one variant comparisons are ordinal: repository is normalized `path`, then an absent span before a present span, then `start`, then `end`; generated forms compare producer ID, output ID, absent span before present span, `start`, then `end`; synthetic is `fixtureId`. A generated-source locator requires `generatorId: sgp.<sha256>` and `hintNameId: sgo.<sha256>`. A tool-generated locator requires `producerId: tgp.<sha256>` and `outputId: tgo.<sha256>`. Metadata locators never represent an unresolved candidate.

Generated identities are SHA-256 over the strict framing fixed by ADR 0003 and the conformance vectors: ASCII domain, one NUL byte, then for each field a four-byte unsigned big-endian byte length and the field's NFC-normalized strict UTF-8 bytes. Source-generator fields are opaque non-empty values of at most 4,096 UTF-8 bytes and are not interpreted as paths, URIs, user names, or secrets. Tool fields additionally use `^[A-Za-z][A-Za-z0-9._-]{0,127}$`. Equal normalized inputs deduplicate. Distinct normalized inputs that resolve to one ID are a collision run failure, even when a test-only digest seam forced the collision. Producer/output prefix disagreement, cross-project/context/source/authority disagreement, or a missing required identity is a generated-fact run failure. The full generated source SHA-256 is independent of any bounded evidence-region SHA-256.

## Evidence bundle

An evidence item has an ID, a target `SymbolRef` or exact component subject, one kind and relation, exactly one repository/metadata/generated-output/synthetic locator, an excerpt, lowercase SHA-256 of complete original UTF-8 bytes, and original/included/omitted byte counts. A component subject is the closed triple `parentSymbolRef`, `componentKind`, and `identity`; parent-only component evidence is invalid after this coordinated amendment. Generated-output evidence contains `producerKind`, matching opaque producer/output IDs, full-source SHA-256, and an optional span. Spans are zero-based end-exclusive UTF-16 offsets in decoded UTF-8 source. Items sort ordinally by ID and IDs are unique.

An EvidenceBundle may carry `observationSubject`, an upstream observation commitment made before Audit assembly. It contains `obs.<sha256>`, the compilation context, exact target/component subject, authoritative declaration-set SHA-256, and authoritative declaration count. The observation reference hashes the other commitment members using the canonical fixture encoding. Ordinary present/absent and malformed-XML audit rows require this commitment. Audit-local declaration evidence must match its independently supplied digest and count; removing a declaration and recomputing only the local digest therefore fails closed.

V1 limits a bundle to 32 items, each excerpt to 4,096 UTF-8 bytes, and all excerpts to 32,768 UTF-8 bytes. Counts are exact; truncation occurs only on a Unicode scalar boundary. A truncated item makes the bundle `partial` with `budget-exhausted`, overriding other omissions. Complete bundles have at least one untruncated item and no omission; partial bundles have at least one item and one omission; unavailable bundles have no items and one omission. This taxonomy does not decide an audit outcome.

`originalUtf8ByteCount` equals `includedUtf8ByteCount + omittedUtf8ByteCount`; included count equals the UTF-8 byte length of `excerpt`; and `isTruncated` is true exactly when omitted count is non-zero. Empty original content has all counts zero and is not truncated; non-empty content has a non-empty excerpt. `sha256` is lowercase hexadecimal SHA-256 of complete original UTF-8 bytes. Evidence IDs are unique and ordinally sorted. Without truncation, omission precedence is access-not-permitted, source-unavailable, binary-content, budget-exhausted, then not-provided. A complete bundle cannot express a missing-evidence outcome.

Repository paths use the M0.1 lexical repository-relative path rules: no rooted/drive/UNC path, NUL, traversal, filesystem lookup, realpath, or host casing behavior. Repository spans are zero-based, UTF-16 code-unit, end-exclusive offsets into UTF-8-decoded text. Metadata locators use opaque lowercase assembly identity and an exact documentation ID; synthetic locators use an opaque lowercase fixture ID. Exactly one locator variant is required.
