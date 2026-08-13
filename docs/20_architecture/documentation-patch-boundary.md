# Documentation patch boundary

## Goal

The patch engine turns one or more validated structured documentation proposals into a deterministic candidate source change while proving that no forbidden code or repository change occurred.

This boundary separates model-assisted writing from source mutation. The Documentation Scribe does not produce a diff and does not receive an edit tool.

The normative serialization and validation rules are defined by [Documentation Patch v1](contracts/documentation-patch-v1.md). This page describes how the later patch engine uses that contract.

The production implementation lives in `ContractScribe.Patching`. It may reuse read-only declaration resolution from `ContractScribe.Roslyn`, but neither the Roslyn audit project nor `ContractScribe.Agent` references the patching project. See [Project structure](project-structure.md).

## Input

A patch request binds:

- an opaque `repositoryContextRef`, the canonical repository-relative `inputIdentity`, and the exact `TargetProfile`;
- each selected `SymbolRef` and its compilation-context reference;
- a closed repository, source-generator, or tool-generated source locator;
- exact source identity: repository locators commit to file bytes, encoding, BOM form, and a UTF-16 declaration span, while generated locators commit to exact UTF-8 source text and a UTF-16 declaration span;
- applicable documentation components and complete-block structured content or `<inheritdoc/>` intent;
- bounded provenance references; and
- canonical ordinal target ordering.

`repositoryContextRef` is a process-local opaque value shaped as `repoctx-` plus 32 lowercase hexadecimal characters. A successful production repository load samples it from 16 cryptographically secure random bytes. It deliberately reveals no local path, Git remote, commit, GitHub repository identity, or filesystem metadata. It is a substitution guard, not a durable repository identity: values are not persisted, derived, retried for uniqueness, or promised never to collide.

Repository declaration resolution retains the request-facing repository path separately from a session-local, repository-relative physical source identity. Locator correlation and later rebinding continue to use the request-facing path; documentation-owner reverse lookup and cross-block exclusivity use the physical identity plus the owner span so contained symbolic-link or junction aliases cannot represent one physical owner as multiple writable targets. The handoff exposes no absolute filesystem path. The multiple-declaration authority exception applies only to ordinary partial methods; partial constructors, properties, indexers, events, and every other non-type multi-declaration shape fail closed as ambiguous.

The engine rejects stale source, an unresolved or ambiguous declaration, mismatched parameters or type parameters, unsupported XML structures, invalid Unicode, and proposals for unselected targets.

## Representation boundary

The structured proposal is an XML-documentation-specific intermediate representation between model-assisted writing and deterministic source rendering. The model owns proposed documentation meaning; the renderer owns XML and C# representation details.

This compiler-like separation is not a language-neutral abstraction promise. Supporting another documentation format or programming language may require coordinated changes to taxonomy, target identity, proposal shape, locators, rendering, and validation rather than only replacing the renderer. See [Semantic foundation](semantic-foundation.md).

## Rendering

The renderer owns:

- XML escaping;
- `///` trivia construction;
- tag ordering;
- indentation;
- preservation of logical-line boundaries without width-based reflow;
- placement on the canonical declaration;
- replacement rules for an allowed existing documentation block;
- file encoding, BOM, and newline preservation.

The initial product inserts or replaces complete documentation blocks. It does not apply arbitrary textual edits from the model.

## Safety invariants

A publishable patch must prove:

1. The pre/post non-documentation token sequence is identical.
2. Only documentation trivia attached to selected targets changed.
3. No new C# parse diagnostic is introduced.
4. Symbol identity, signatures, modifiers, attributes, generic constraints, and semantic relationships are unchanged.
5. Project files, build scripts, tests, and unselected source files are unchanged.
6. File encoding, BOM, newline, and indentation policies are preserved.
7. Applying an internally rebound accepted candidate again produces no second byte change; replaying the original public request against changed source instead reports stale input.
8. Every changed documentation block maps to one accepted proposal and evidence set.
9. Uncertainty, stale input, or an unsupported syntax shape fails closed.

Formatting changes are permitted only when they are inseparable from the selected documentation trivia and explicitly allowed by the renderer contract.

## Adversarial matrix

M2 must cover:

- comments containing code-like text;
- strings containing documentation markers;
- preprocessors and disabled text;
- partial types and partial methods;
- generated and source-generated declarations;
- records and primary constructors;
- operators, conversions, indexers, events, and delegates;
- explicit interface implementations;
- overrides and `<inheritdoc/>`;
- multiple targets in one file;
- mixed newline and indentation cases;
- UTF-8, BOM, invalid encoding, and unusual Unicode;
- source changes between proposal and apply;
- repeated application and partially applied batches.

## Candidate workspace

Patch rendering occurs in a candidate workspace or proposal branch, never directly on an unvalidated target branch. A patch validation result records:

- the request context and one correlated trace per selected target;
- changed repository files, exact original and candidate hashes, and documentation-block counts;
- exact documentation-region byte and physical `///` line observations;
- all nine invariant results;
- bounded diagnostics selected deterministically; and
- an `accepted`, `rejected`, or `stale` outcome.

Malformed, oversized, incorrectly encoded, duplicate-property, unsupported-version, or otherwise intrinsically invalid request artifacts produce `PatchRequestValidationFailure`; they never produce a Patch Validation Result. A result exists only after an intrinsically valid request enters live execution, and it must be validated by explicit correlation against that request.

Only an accepted result may be handed to the GitHub adapter.

## Relationship to later stages

M2 is fully deterministic and provider-independent. M3 consumes it as the only path from a structured proposal to source changes. M4 applies campaign budgets before and after rendering. M5 publishes only accepted patch results.
