# Documentation Patch v1

## Authority and lifecycle

Documentation Patch v1 is the provisional M2 machine contract between a validated structured documentation input and the deterministic patch engine. It defines Patch Request parsing, source and session commitments, complete-block content, target order, Patch Validation Result semantics, and stable failure vocabularies. It does not render or mutate source.

This is a pre-release draft governed by [Contract lifecycle](../../00_project/contract-lifecycle.md). Consumers reject every request or result version other than integer `1`. Version `1` identifies the current compatibility family; the repository revision identifies the exact draft semantics. No compatibility reader, migration, alias, persisted replay promise, or cross-revision identity is defined.

The JSON Schemas own fail-closed shape and simple lexical bounds. `schemas/documentation-patch/v1.registry.json` owns closed vocabularies and numeric limits. `DocumentationPatchValidator` owns cross-field semantics, ordering, reference closure, live-context correlation, exact source-byte/text validation, and request/result correlation.

## Patch Request

The top-level property order is `patchRequestVersion`, `context`, `provenanceCatalog`, then `blocks`. Its maximum encoded size is 1,048,576 UTF-8 bytes. JSON is UTF-8 without BOM; comments, trailing commas, duplicate properties at any depth, malformed scalar sequences, unknown fields, and unsupported versions fail closed.

`context` has exactly:

- `repositoryContextRef`: `repoctx-` plus exactly 32 lowercase hexadecimal characters;
- `inputIdentity`: a non-empty canonical repository-relative path naming the explicitly loaded project or solution input;
- `targetProfile`: `profile.external-api` or `profile.assembly-visible`.

For every successful production load, the trusted load boundary obtains an independently generated 16-byte sample from a cryptographically secure random source and encodes it as the context reference. Consumers compare the request value to the bound loaded session with ordinal equality before target lookup. This value has a negligible but non-zero collision probability. It is not collision-free, globally unique, persistent, authenticated, caller-selected, recoverable, or valid across process/restart boundaries. It is not derived from or replaceable by an absolute path, path hash, Git remote, GitHub repository, branch, commit, tree, source content, repository-relative locator, `SymbolRef`, compilation context, or exact source bytes. Issued-value tracking and collision retry state are not defined.

`provenanceCatalog` contains zero to 4,096 unique opaque IDs in ordinal order. Each ID is one to 128 Unicode scalar values and contains only ASCII letters, digits, period, underscore, colon, and hyphen. M2 validates only order, uniqueness, bounds, and reference closure. The catalog does not embed or interpret M1 evidence and does not judge evidentiary sufficiency.

`blocks` contains one to 512 complete documentation-block requests in the total order defined below. Each block has exactly:

1. `blockId`, unique within the request;
2. `symbolRef`, retaining the existing lowercase `compilationContextRef` (`^[a-z0-9][a-z0-9._-]{0,127}$`) and exact `documentationCommentId`;
3. one closed `locator` variant;
4. `editKind`, exactly `insert` or `replace`;
5. `applicableComponents`;
6. one closed `content` variant;
7. `provenanceRefs`, unique and ordinally ordered, with every value present in `provenanceCatalog`.

`insert` authorizes insertion only when the resolved authoritative declaration has no direct documentation block. `replace` authorizes replacement only when a replaceable direct complete block exists. A later engine rejects rather than guesses or broadens an operation.

## Source locator union

A locator has a `kind` discriminator and exactly the fields of that variant.

### Repository

The repository locator contains canonical repository-relative `path`, lowercase `originalFileSha256` over the exact original file bytes including any BOM and newline bytes, `encoding`, and non-empty half-open `declarationSpan` in the strictly decoded text after removal of the encoding BOM.

Supported encodings are:

| ID | Required byte representation |
| --- | --- |
| `utf-8` | strict UTF-8 with no BOM |
| `utf-8-bom` | EF BB BF followed by strict UTF-8 |
| `utf-16le-bom` | FF FE followed by strict little-endian UTF-16 |
| `utf-16be-bom` | FE FF followed by strict big-endian UTF-16 |

UTF-16 without BOM, legacy code pages, unknown encodings, odd UTF-16 byte counts, malformed sequences, and a BOM/encoding mismatch fail closed. The encoding identity remains part of the commitment even when decoded text is equal. `declarationSpan.start` and `.end` are UTF-16 code-unit offsets with `0 <= start < end <= decodedText.Length`.

Intrinsic validation rejects an unsupported encoding ID as an invalid request. Once the ID is valid, a mismatch between the locator and authoritative live bytes is a stale source condition.

### Source generator and tool generated

`sourceGenerator` contains `producerId` (`sgp.` plus 64 lowercase hexadecimal characters), `outputId` (`sgo.` plus 64 lowercase hexadecimal characters), `sourceSha256`, and `declarationSpan`. `toolGenerated` uses `tgp.` and `tgo.` identifiers with the same digest/span fields.

`sourceSha256` is SHA-256 over strict UTF-8-without-BOM encoding of the exact Unicode-scalar source text. Generated locators never carry a repository path or file encoding and are non-writable inputs in this version. They remain valid request inputs so the engine can reject them deterministically as non-writable rather than confuse them with repository files.

## Components and content

`applicableComponents` contains zero to 512 entries in the following kind order: `typeParameter`, `parameter`, `return`, `value`. It reuses the current M1 component identity domain without aliases: type parameters are `type-parameter/N`, parameters are `parameter/N`, and the two unnamed identities are exactly `return` and `value`, where `N` is a canonical zero-based decimal ordinal without leading zeroes. Type-parameter and parameter entries also require the exact non-empty source `name`; return and value omit `name`. Names are XML 1.0-valid scalars. Values compare ordinally. Entries are unique, each kind group is ordered by identity, duplicate names within one named kind fail, and return/value are mutually exclusive.

Content is one of:

- `inheritDoc`: exactly `{ "kind": "inheritDoc" }`, representing standalone attribute-free `<inheritdoc/>`;
- `structured`: complete plain-text content for the applicable components.

Structured content contains:

- non-empty `summaryLines`;
- `typeParameters` and `parameters`, each bound to an applicable component identity and exact name, with non-empty `lines`;
- nullable `return`, present exactly when the applicable set contains return;
- nullable `value`, present exactly when the applicable set contains value;
- zero to 256 `exceptions`, unique and ordinally ordered by `typeDocumentationId`;
- nullable `remarksLines`, which is either null or a non-empty line array.

The structured type-parameter, parameter, return, and value entries match the applicable set one-for-one. Duplicate, missing, mismatched, or unexpected entries fail. `return` and `value` cannot both be non-null.

A logical-line array has one to 256 strings. A line may be empty and is limited to 2,048 Unicode scalar values; one block is limited to 32,768 logical-text scalar values in total. A logical line contains no CR, LF, U+0085, U+2028, or U+2029. After JSON decoding, every scalar must be a valid XML 1.0 character: TAB, U+0020 through U+D7FF, U+E000 through U+FFFD, or U+10000 through U+10FFFF. Unpaired surrogates and controls such as U+0001 fail closed. Limits count Unicode scalar values, not UTF-16 code units.

An exception `typeDocumentationId` is an exact type documentation-comment ID: it starts with `T:`, has a non-empty body, is XML 1.0-valid, and contains no whitespace, control character, line separator, `<`, `>`, `&`, quotation mark, or apostrophe. It is not C# type syntax, a qualified-name alias, or an arbitrary XML `cref`.

The request never contains raw XML nodes, pre-rendered `///` trivia, source replacement text, a diff, formatting instructions, provider/prompt identity, Style Profile, or proposal-run identity. The later renderer escapes text exactly once, chooses the source newline, and preserves line-array order.

## Deterministic request order

Blocks use the total ordinal order:

1. locator rank: `repository`, `sourceGenerator`, `toolGenerated`;
2. variant identity: repository path, or producer ID then output ID;
3. numeric declaration-span start, then end;
4. `symbolRef.compilationContextRef`;
5. `symbolRef.documentationCommentId`;
6. `blockId`.

Duplicate symbol refs and duplicate locator/span bindings fail. Multiple repository targets in one file therefore order by path and numeric declaration start. Public conformance data includes same-file starts `21` and `100` so numeric ordering, reversed order, duplicate binding, and partial accepted-result accounting are executable rather than inferred. Ordering, uniqueness, and reference validation are linear after bounded input materialization.

## Intrinsic request validation versus execution

Malformed or unsupported artifacts do not produce a Patch Validation Result. `PatchRequestValidationFailure` carries one stable bounded code and optional bounded JSON Pointer. Deterministic precedence is:

1. document byte limit, BOM, strict UTF-8;
2. JSON syntax and duplicate property;
3. unsupported version;
4. required/unknown-field shape;
5. vocabulary and lexical values;
6. content/component semantics;
7. ordering and duplication;
8. reference closure.

Only an intrinsically valid request enters live execution. The nonserialized `DocumentationPatchValidationContext` carries the exact live `repositoryContextRef`, input identity, expected `TargetProfile`, and allowed compilation-context refs. Root checks run in repository-context, input-identity, then target-profile order before target lookup. Every block compilation-context ref must resolve in the live set. Repository and generated source identities are then checked through the pure Core seams using authoritative exact bytes or exact text supplied by the host.

Environment failure and cancellation are host outcomes outside this artifact contract.

## Patch Validation Result

The result top-level property order is `patchValidationResultVersion`, `patchRequestSha256`, `context`, `outcome`, `targets`, `changedFiles`, `changedDocumentationBlockCount`, `invariants`, then `diagnostics`. It is parsed under the same UTF-8-without-BOM, size, duplicate-property, version, unknown-field, and bounded-diagnostic rules as the request.

`patchRequestSha256` is lowercase SHA-256 over the exact accepted UTF-8 Patch Request artifact bytes, including their JSON representation. `ParseRequest` retains this digest on the validated in-memory request; a result producer copies it unchanged, and `ValidateResult` compares it ordinally before target correlation. It binds the result to the complete request payload, including edit authorization, applicable components, content, and provenance catalog. It is not canonical JSON, a persistent identity, release identity, or cross-revision compatibility promise.

`targets` contains exactly one trace for every request block in request order. A trace copies `blockId`, `symbolRef`, `locator`, and `provenanceRefs`, and adds status `valid`, `invalid`, `stale`, or `not-evaluated`. Result validation is an explicit correlation operation against the already validated request; a result cannot validate these copied commitments in isolation.

`changedFiles` is ordinally ordered by canonical request-owned repository path and limited to 512. Each observation contains:

- unequal lowercase original and candidate full-file byte SHA-256 values;
- `changedDocumentationBlockCount`;
- `originalDocumentationByteCount` and `candidateDocumentationByteCount`;
- `originalDocumentationLineCount` and `candidateDocumentationLineCount`.

Counts are non-negative signed 32-bit integers. The root block count equals the sum of file block counts without overflow.

Byte/line observations use exact complete documentation-trivia regions authorized by the request, never LCS, Myers, or a general diff. The original byte count sums exact encoded slices replaced by requested blocks; insertion contributes zero. The candidate byte count sums exact encoded inserted or substituted documentation slices. A line count is the number of physical `///` records in those slices. One final record counts even when it has no terminating newline. Indentation, `///`, content, and in-region CRLF/LF bytes contribute to byte counts; surrounding trivia and the following declaration do not. Multiple regions aggregate by addition after overlapping/shared-owner regions are rejected. Public accepted-result vectors pin original and complete candidate file bytes in base64, documentation-region offsets and lengths, physical-line counts, and full-file SHA-256 values; conformance tests recompute the result observations and remove the pinned regions to recover the exact original bytes.

`invariants` contains exactly once and in registry order every closed M2 safety invariant. Status is `passed`, `failed`, or `not-run`.

`diagnostics` contains at most 128 bounded error entries. Every currently defined stale/rejected code has severity `error`; unused informational and warning severities are not part of v1. Non-null block and path attribution must resolve to the correlated request. Root `patch.stale.repository-state` has first precedence, followed by repository-context, input-identity, then target-profile failures. Otherwise stale failures precede rejected failures, request block order chooses the first affected block, and the registry's closed within-block code order chooses the primary. Root context codes require every target to be `not-evaluated`; per-block stale codes require that target to be `stale`; target rejection codes require it to be `invalid`; `patch.rejected.no-effective-change` alone may retain all-valid targets. Root execution codes `patch.stale.repository-state` and `patch.rejected.candidate-state` carry null `blockId`, `path`, and `pointer`; the former requires every target `not-evaluated`, while the latter is available only after successful E1 resolution and requires every target `valid`. After the primary, unique secondary diagnostics are ordered ordinally by code, block ID, path, and pointer. When more than 128 diagnostics exist, retain the primary then the earliest secondaries under that order. Raw exceptions and unbounded logs are never serialized.

## Outcome semantics and precedence

`accepted` requires all of the following:

- every target status is `valid`;
- every required invariant is `passed`;
- no error diagnostic exists;
- every selected block is a repository block and has an effective documentation-byte change;
- every changed-file block count equals the selected block count for that path and the root count equals the request block count;
- every candidate documentation region has positive byte and physical-line observations; insertion has zero original region bytes/lines, while replacement has positive original region bytes/lines;
- every changed path belongs to a repository locator in the request;
- every changed-file original/candidate full-file hash is unequal;
- no generated target is treated as writable;
- all counts, trace commitments, and observations correlate.

`stale` means at least one live context, source commitment, protected input, or governed original-root commitment no longer matches. It requires a `patch.stale.*` primary code and empty/zero changed observations. Root repository-context/input/profile mismatch and `patch.stale.repository-state` make every target `not-evaluated`. Per-block compilation-context or authoritative source mismatch marks that target stale. For repository-state every invariant is `not-run` except `patch.invariant.fail-closed`, which is `passed`. If repository-state and candidate-state are both observable, repository-state wins.

`rejected` means the valid request is current but a target is unsupported, ambiguous, non-writable, in the wrong edit state, unsafe, one or more requested blocks have no effective byte change, or a completed E1 candidate cannot be proven identical to its terminal disk capture. No target is stale, a `patch.rejected.*` code is required, and changed observations are empty/zero. Any complete transformation that cannot account for an effective change for every requested block uses `patch.rejected.no-effective-change`. Root `patch.rejected.candidate-state` keeps every successfully resolved target `valid` and, like repository-state, sets every invariant `not-run` except fail-closed `passed`.

No malformed, rejected, stale, unsafe, wholly no-op, or partially processed request has an accepted result or handoff. Public replay of an original accepted request against its candidate is stale because the committed original file digest changed. Idempotency is a separate internal transformation rebound to candidate bytes; it produces no second byte change and no second public accepted result.

## Ownership and non-goals

M2-C2 / #91 owns trusted production generation/comparison of repository context refs, authoritative source acquisition, target resolution, writability/edit-state classification, and use of the Core byte/text seams. M2-E1 / #92 owns rendering and candidate-workspace application. M2-E2 / #93 owns full safety validation, engine composition, candidate acceptance, and Patch Validation Result construction. This contract does not implement Roslyn/MSBuild loading, filesystem access, source rendering, mutation, candidate workspace management, Git/GitHub operations, release identity, publication authority, M3 provider/proposal contracts, M4 snapshots/campaigns, or M5 workflow state.
