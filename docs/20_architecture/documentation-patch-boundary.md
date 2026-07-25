# Documentation patch boundary

## Goal

The patch engine turns one or more validated structured documentation proposals into a deterministic candidate source change while proving that no forbidden code or repository change occurred.

This boundary separates model-assisted writing from source mutation. The Documentation Scribe does not produce a diff and does not receive an edit tool.

The production implementation lives in `ContractScribe.Patching`. It may reuse read-only declaration resolution from `ContractScribe.Roslyn`, but neither the Roslyn audit project nor `ContractScribe.Agent` references the patching project. See [Project structure](project-structure.md).

## Input

A patch request binds:

- repository and input snapshot identity;
- selected `SymbolRef`;
- canonical source declaration and expected source hash;
- structured documentation fields;
- style-profile identity;
- evidence references;
- proposal-contract identity;
- deterministic target ordering when several proposals share a file.

The engine rejects stale source, an unresolved or ambiguous declaration, mismatched parameters or type parameters, unsupported XML structures, invalid Unicode, and proposals for unselected targets.

## Rendering

The renderer owns:

- XML escaping;
- `///` trivia construction;
- tag ordering;
- indentation;
- line wrapping allowed by the style profile;
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
7. Applying the same accepted patch again produces no diff.
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

- input and output identities;
- selected targets;
- changed files and documentation-block count;
- patch bytes and line counts;
- invariant results;
- diagnostics;
- accepted, rejected, or stale outcome;
- bounded failure codes.

Only an accepted result may be handed to the GitHub adapter.

## Relationship to later stages

M2 is fully deterministic and provider-independent. M3 consumes it as the only path from a structured proposal to source changes. M4 applies campaign budgets before and after rendering. M5 publishes only accepted patch results.
