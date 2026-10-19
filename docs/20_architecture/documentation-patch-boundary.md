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

Repository declaration resolution retains the request-facing repository path separately from a session-local, repository-relative physical source identity. A successful loader session seals a narrow immutable repository baseline authority; C2 resolution and E1 rendering consume the same captured bytes rather than independently authorizing live reads. Locator correlation and final original-root rebinding continue to use the request-facing path and fail closed when observable repository identity, topology, or protected commitments drift. Documentation-owner reverse lookup uses baseline-confined physical identities, and cross-block exclusivity uses the validated physical identity plus the owner span, so contained symbolic-link or junction aliases cannot represent one physical owner as multiple writable targets. The handoff exposes no absolute original filesystem path. The multiple-declaration authority exception applies only to ordinary partial methods; partial constructors, properties, indexers, events, and every other non-type multi-declaration shape fail closed as ambiguous.

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

Every requested block must produce an intrinsic byte change in its own documentation region. A replacement that would reproduce that region is rejected as `patch.rejected.no-effective-change` even when another block in the request would change; other unsafe rendering shapes remain `patch.rejected.unsafe-change`.

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

E1 renders into a fresh request-local candidate workspace outside and physically distinct from the original checkout. Before creating any entry, the loader-owned baseline authority validates the proposed root location and binds the staging parent identity; the candidate builder then creates every directory and regular file relative to an identity-checked parent without following links. Every governed file is created anew: selected source files receive deterministic rendered bytes and all other governed files retain their baseline bytes. Candidate entries are sealed only after complete readback and a final original-baseline rebind. Construction failure or cancellation returns no usable handle; disposal invalidates the opaque handle and performs identity-bound best-effort cleanup without following changed topology.

Candidate mutation uses Linux handle-relative filesystem operations in the required GitHub-hosted Ubuntu x64 lane. Qualified native macOS arm64 also provides local developer feedback under [ADR 0004](decisions/0004-initial-runner-platform-support.md): Darwin lookup/open effects and possible blocking are accepted only for identity inspection, while retained root/ancestor and actual-descriptor authority must be established before content reads, child enumeration, or existing-object mutation. Private staging requires ownership-enforced APFS, exact private modes, and no extended ACL. Physical volume UUID/object-ID ancestry rejects a staging parent equal to or below the source root, including APFS namespace aliases, before directory creation. An ancestor staging parent may create an exclusively new empty root only when the actual candidate root is physically disjoint in both directions. Both capture passes use the retained authorized hierarchy and actual file descriptors. Native Windows still fails closed before candidate-root creation; its read-only loader and renderer feedback does not grant candidate mutation.

Consumption transfers cleanup ownership exactly once. The internal #93 handoff can perform one identity-bound disk capture that rebinds the staging parent, root, every directory, and every regular file, rejects extra, missing, linked, or replaced entries, and returns the current bounded bytes. The capture retains handles for the first-pass root, nested directories, and regular files, opening each nested directory relative to its retained parent, then finishes with a second complete-tree pass bound to the exact paths, kinds, identities, and bytes. Original filesystem objects therefore remain pinned so a released identity cannot be recycled between the passes to hide a same-name file, root, or subtree replacement. A second capture operation, a disposed consumer, or any identity/topology mismatch returns no candidate capture.

The E1 result is complete but unaccepted. Its public handle exposes no staging path or arbitrary writer, and no E1 status is a Patch Validation Result outcome. Issue #93 consumes the internal correlated candidate, revalidates it, and constructs the Patch Validation Result, which records:

- the request context and one correlated trace per selected target;
- changed repository files, exact original and candidate hashes, and documentation-block counts;
- exact documentation-region byte and physical `///` line observations;
- all nine invariant results;
- bounded diagnostics selected deterministically; and
- an `accepted`, `rejected`, or `stale` outcome.

Malformed, oversized, incorrectly encoded, duplicate-property, unsupported-version, or otherwise intrinsically invalid request artifacts produce `PatchRequestValidationFailure`; they never produce a Patch Validation Result. A validation result exists only after an intrinsically valid request enters the #93 validation stage, and it must be validated by explicit correlation against that request and the sealed E1 candidate.

Only an accepted result may be handed to the GitHub adapter.

## Campaign composition and recovery

The campaign Host accepts independently validated proposal/skip members incrementally under one shared Scribe attempt. A durable proposal is generated content awaiting M2; only `accepted` work belongs to a validated candidate. Publication and main completion remain separate GitHub facts. The Agent receives immutable receipt/progress acknowledgements and has no checkpoint-store, source writer or candidate capability.

The cumulative composer reconstructs one complete M2 request from all accepted and proposal-complete blocks in the original batch, rederiving current source, component and evidence authority. It uses the existing isolated candidate engine for same-file declarations and canonical partial-method owners. Per-field claim/evidence metadata accompanies the sole generated body in each patch block; no source bytes, candidate bytes or conversation is stored. A compatible fresh process can reconstruct the whole candidate without calling the provider. The original checkout remains unchanged.

A proven single independently invalid block may close through the existing narrow reduction and leave its valid siblings reconstructible. Multiple invalid blocks, shared-owner conflicts and global cumulative failures refuse aggregate publication; they cannot choose an arbitrary subset. Projection-capacity stops close only the over-bound completed result by commitment, preserve earlier durable siblings, and pass those siblings through independent cumulative M2 validation. A proven accepted subset can use the existing publication handoff and fresh accepted-only reconstruction while its structural budget terminal continues to prohibit provider work; ordinary exhaustion and unaccepted/global-failed M2 outcomes cannot use that exception. Cancellation and crash retain unpublished progress for the original fixed manifest.

## Relationship to later stages

M2 is fully deterministic and provider-independent. M3 consumes it as the only path from a structured proposal to source changes. M4 applies campaign budgets before and after rendering. M5 publishes only accepted patch results.
