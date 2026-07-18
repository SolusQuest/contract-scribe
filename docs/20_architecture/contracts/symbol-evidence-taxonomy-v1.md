# Symbol and Evidence Taxonomy v1

## Authority and status

This provisional M0 contract defines taxonomy semantics and compatibility. The [registry](../../../schemas/symbol-evidence-taxonomy/v1.registry.json) owns the closed V1 identifiers; the [schema](../../../schemas/symbol-evidence-taxonomy/v1.schema.json) owns JSON shape; fixtures own executable conformance vectors. A disagreement is an implementation failure. V1 has no extension point; unknown properties, identifiers, or versions fail validation.

## Classification boundary

`TargetClassification` contains `SymbolRef`, one primary kind, ordered traits, origin, support status, and an optional primary skip reason. `ComponentClassification` contains its parent `SymbolRef`, component kind and identity, origin, support status, and optional primary skip reason. `RelationObservation` alone represents relations. `UnresolvedClassification` has a compilation context and candidate locator when no `SymbolRef` can be formed.

`SymbolRef` is an ordinal pair of opaque `compilationContextRef` (`^[a-z0-9][a-z0-9._-]{0,127}$`) and an exact, non-empty, original-definition XML documentation comment ID. The latter is never normalized. Its ordering is context then documentation ID. Every relation and evidence subject uses the full pair.

A top-level type is reachable only when public. A nested type needs every containing type reachable. Public members are reachable; protected and protected-internal members additionally need an externally derivable containing type. Private-protected members, protected members of structs/sealed classes, and static constructors are not reachable. Explicit interface implementations are relation-only; the reachable interface member is the target. Inherited interface members do not create duplicate targets.

Compiler-synthesized symbols never create targets. Source-generator and tool-generated declarations may. Origins aggregate to source, source-generator, tool-generated, mixed, compiler-synthesized, or unknown. Unknown origin is allowed only with unavailable context. Only synthesized forms named by the V1 registry create components; all other synthesized forms create no record.

## Components and relations

Parameters belong to methods, constructors, operators, conversions, indexers, or delegates; type parameters to named types, delegates, or methods; returns to methods/operators/conversions/delegates; values/getters/setters/init to properties/indexers; add/remove to events; backing fields to properties/events. Component identities are `parameter/N`, `type-parameter/N`, `return`, `value`, and `accessor/<name>`, where `N` is a zero-based ordinal. Explicit source record-copy constructors are ordinary constructor targets; synthesized copy/default constructors and registered delegate/record members are non-target components.

An override points from overriding member to original-definition base member. Interface relations point from implementation to interface member; a derived interface points to its inherited original member. Multiple observations sort by relation ID then full target `SymbolRef`.

All valid interface member forms keep their normal primary kind. Default body-bearing instance members add `trait.virtual`; static abstract members add `trait.static` and `trait.abstract`; static virtual/default members add `trait.static` and `trait.virtual`. A reachable source destructor is a supported method target; one in a non-derivable container is not reachable.

## Status, skips, and compatibility

Supported records have no skip. Every other record has exactly one skip. Precedence is documentation-comment-id unavailable, generated provenance unavailable, semantic context unavailable, applicable unknown kind, partial ambiguity, mixed-origin ambiguity, synthesized non-target, then non-documentation component. The registry is authoritative for the exact values. An unrecognized primary/component uses its reserved unknown kind and the matching unsupported skip. Unknown required IDs, schema versions, and malformed bundles are contract failures rather than skips.

Registry identifiers are lowercase ASCII dotted identifiers, compare ordinally, are never reused, and are opaque to consumers. Every registry entry declares an ID, normative definition, permitted record/status combinations, and `deprecated`/`replacementId` metadata. V1 entries have both metadata values null. A future deprecation retains a readable ID and supplies a replacement in the same registry section. Adding an ID, changing a definition, or changing a required document member requires a new integer artifact version; editorial changes that do not change behavior do not.

When several conditions apply, choose exactly one skip in this order: missing documentation comment ID; unavailable generated provenance; unavailable semantic context; unknown primary/component kind; ambiguous partial declaration; mixed origin; synthesized non-target; non-documentation component. `origin.unknown` is valid only for unavailable-context with a generated-provenance or semantic-context skip; `origin.mixed` with ambiguity uses the mixed-origin skip.

An unresolved candidate has its compilation context, origin, status, skip, and exactly one candidate locator. Candidate locators are repository, generated-source, or synthetic; their order is repository, generated-source, synthetic. A generated-source locator contains manifest-supplied generator and hint-name IDs (both `^[a-z0-9][a-z0-9._-]{0,127}$`) plus an optional span. Metadata locators never represent an unresolved candidate.

## Evidence bundle

An evidence item has an ID, subject `SymbolRef`, one kind and relation, exactly one repository/metadata/synthetic locator, an excerpt, lowercase SHA-256 of complete original UTF-8 bytes, and original/included/omitted byte counts. Spans are zero-based end-exclusive UTF-16 offsets in decoded UTF-8 source and are repository-only. Items sort ordinally by ID and IDs are unique.

V1 limits a bundle to 32 items, each excerpt to 4,096 UTF-8 bytes, and all excerpts to 32,768 UTF-8 bytes. Counts are exact; truncation occurs only on a Unicode scalar boundary. A truncated item makes the bundle `partial` with `budget-exhausted`, overriding other omissions. Complete bundles have at least one untruncated item and no omission; partial bundles have at least one item and one omission; unavailable bundles have no items and one omission. This taxonomy does not decide an audit outcome.

`originalUtf8ByteCount` equals `includedUtf8ByteCount + omittedUtf8ByteCount`; included count equals the UTF-8 byte length of `excerpt`; and `isTruncated` is true exactly when omitted count is non-zero. Empty original content has all counts zero and is not truncated; non-empty content has a non-empty excerpt. `sha256` is lowercase hexadecimal SHA-256 of complete original UTF-8 bytes. Evidence IDs are unique and ordinally sorted. Without truncation, omission precedence is access-not-permitted, source-unavailable, binary-content, budget-exhausted, then not-provided. A complete bundle cannot express a missing-evidence outcome.

Repository paths use the M0.1 lexical repository-relative path rules: no rooted/drive/UNC path, NUL, traversal, filesystem lookup, realpath, or host casing behavior. Repository spans are zero-based, UTF-16 code-unit, end-exclusive offsets into UTF-8-decoded text. Metadata locators use opaque lowercase assembly identity and an exact documentation ID; synthetic locators use an opaque lowercase fixture ID. Exactly one locator variant is required.
