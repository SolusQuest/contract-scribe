# Policy/Configuration v1

Policy/Configuration v1 is a provisional, repository-neutral contract for selecting one target profile and a documentation expectation from caller-supplied project plus repository-source or generated-output identity. It defines policy expectations, not observed audit results, and makes no filesystem, Roslyn, provider, or platform-adapter commitment.

## Authority and status

This document owns the semantic behavior, validation-stage order, and error-code meanings. [The JSON Schema](../../../schemas/policy-configuration/v1.schema.json) owns document-shape validation only. The schema has no remote `$ref` dependency.

The contract is a pre-release draft governed by [Contract lifecycle](../../00_project/contract-lifecycle.md). M0 established a commit-pinned, cross-consistent baseline; it did not create an external compatibility promise. Before the first downstream-consumable release, a coordinated breaking amendment may retain version `1` when incompatible revisions do not need to coexist. Exact draft semantics must pin a repository commit. After release, or when coexistence is required, a breaking change requires a new schema version and identifier.

A consumer rejects an unsupported `schemaVersion`. A workflow that persists, transfers, or consumes this draft policy across repository revisions must separately bind and validate the contract-baseline or provenance identity; `schemaVersion: 1` alone cannot distinguish incompatible draft revisions. A verified same-revision workflow may rely on its pinned source and tool baseline. The repository revision is not a member of the canonical policy document.

## Document and input boundary

V1 accepts one supplied JSON policy document encoded as UTF-8 without a BOM. It does not define a file name, discovery, parent search, environment override, multi-file merge, inheritance, remote include, filesystem access, realpath, symlink behavior, or automatic migration.

The caller supplies a required non-empty lexical `projectPath` and exactly one of:

- repository input: required non-empty lexical `sourcePath`;
- generated input: `generatedOutput` with `producerKind`, opaque `producerId`, and opaque `outputId`.

`producerKind: source-generator` requires `sgp.<sha256>` and `sgo.<sha256>` IDs. `producerKind: tool-generated` requires `tgp.<sha256>` and `tgo.<sha256>` IDs. Establishing a repository root, relativizing filesystem paths, and validating the authoritative generated-fact set are host responsibilities. Evaluation is pure lexical and opaque-ID behavior.

The public, normative conformance corpus is [`tests/fixtures/policy-configuration/v1/cases.json`](../../../tests/fixtures/policy-configuration/v1/cases.json). Every case has a unique non-empty `caseId`, one declared stage, one input pair, and exactly one expected outcome: either a permitted decision (with an optional matched rule ID) or an error. A non-missing case names exactly one payload beneath `policies/`: `policyFile` is read as raw bytes, while `payloadFile` may specify `payloadEncoding: "base64"` to preserve intentionally invalid byte sequences. Missing-document cases name no payload. The test-only oracle rejects malformed manifests, payloads outside `policies/`, unknown stages/error codes, and a declared stage that does not match the structured outcome.

## Policy model

`schemaVersion` is the required integer `1`. `targetProfile` is required and is exactly one of:

- `profile.external-api`: the externally reachable surface defined by ADR 0003;
- `profile.assembly-visible`: every declaration visible within the analyzed assembly as defined by ADR 0003.

There is no default profile. The selected value is a run-level choice and must be copied unchanged to the Audit Result v1 envelope. Classification records do not repeat it.

`defaultDecision` is required and is one of:

- `required`: absent XML documentation is a future audit violation.
- `optional`: neither presence nor absence is a documentation-requirement violation.
- `forbidden`: present XML documentation is a future audit violation.

These values are policy expectations, not M0.2 per-symbol audit-result reasons.

`rules` is optional; omitted and `[]` are equivalent. A rule has a unique ID, a unique priority, a decision, and optional `projectPaths` and `sourcePaths` selectors. A rule with no selectors is global.

A selector accepts when at least one `include` pattern matches, if `include` is present, and no `exclude` pattern matches. Exclude wins when both lists match. A rule applies only when every declared selector accepts. The applicable rule with the greatest priority wins; otherwise `defaultDecision` applies. Global priority uniqueness makes precedence statically decidable before evaluation.

A `sourcePaths` selector is wholly inapplicable to a pathless generated input, including an exclude-only selector. A rule that declares `sourcePaths` therefore never matches a generated input. Project-only and global rules remain eligible.

Duplicate patterns are allowed and do not change selector behavior. Shape constraints belong to the schema: selectors must contain at least one of non-empty `include` or `exclude` arrays. Semantic validation runs only after shape validation. It scans all rule IDs first, then all priorities, then patterns. A duplicate ID or priority reports the pointer of the current (second) rule member. Pattern validation scans rule index ascending, `projectPaths` before `sourcePaths`, `include` before `exclude`, then pattern index ascending; its pointer identifies that exact pattern array element, including its index. This ordering is part of the structured error contract.

The current M0 baseline intentionally has no symbol-category selector. M1 owns the target-surface and documentation-observation completion gate. A future selector requires a coordinated Policy/Taxonomy/Audit amendment. Before release that amendment may complete v1 in place under the contract lifecycle; after release it requires a new compatible artifact version.

## Lexical paths and globs

For each supplied path, the evaluator rejects NUL; rejects rooted, drive-letter, and UNC forms from the original input; splits `/` and `\`; rejects every `..` segment; discards empty and `.` segments; rejects zero remaining segments; then joins segments with `/`.

For example, `./a//b/` normalizes to `a/b`; `/a`, `\a`, `C:foo`, `C:/foo`, `//server/share`, and traversal input are invalid. Comparisons are ordinal and case-sensitive, with no Unicode normalization or host-filesystem behavior.

Patterns are canonical repository-relative paths using `/` only and match a complete normalized path. `*` matches zero or more non-separator characters within a segment. `**` is valid only as a complete segment and matches zero or more complete segments: `**` matches every valid path, `a/**/b` matches `a/b`, `a/**` matches `a`, and `**/a` matches root-level `a`.

V1 rejects `?`, character classes, brace expansion, negation, directory-only patterns, regex, and escape syntax. Normative vectors are in `tests/fixtures/policy-configuration/v1/cases.json`.

## Validation pipeline

The test-only conformance oracle returns one structured outcome and stops at the first failure:

1. Document presence: `policy.input.missing-document`.
2. Raw bytes: `policy.document.invalid-encoding` or `policy.document.bom-not-allowed`.
3. JSON lexical parse: `policy.document.invalid-json` or `policy.document.duplicate-property`.
4. Schema-version gate: `policy.schema.unsupported-version`.
5. Target-profile gate: `policy.target-profile.required` or `policy.target-profile.invalid`, both at `/targetProfile`.
6. JSON Schema shape validation: `policy.schema.invalid-document`.
7. Semantic validation: `policy.semantic.duplicate-rule-id`, `policy.semantic.duplicate-priority`, or `policy.semantic.invalid-pattern`.
8. Evaluation input validation: `policy.input.invalid-path`, or a run-level generated-fact failure owned by the taxonomy registry.
9. Rule resolution: an effective decision and matched rule ID, or a null matched rule ID for default fallback.

Error outcomes contain `code`, an RFC 6901 `pointer` when an instance location exists, and `schemaKeyword` only for stage 6. Human-readable messages are non-normative.

Any duplicate property, including `schemaVersion` or `targetProfile`, fails at stage 3. Stage 4 applies only to an object with exactly one integer `schemaVersion` other than `1`. Stage 5 applies only to an object whose unique integer `schemaVersion` is `1`; it distinguishes a missing profile from any present non-enumerated value. Missing, null, string, Boolean, non-integral, and non-object schema-version cases fail at stage 6.

Within a failing stage, the oracle selects a canonical outcome: a leading BOM wins over later encoding defects; lexical parsing uses the earliest byte-offset violation; duplicate-property errors identify the current duplicate member with an RFC 6901 pointer; schema leaf failures sort by ordinal instance pointer and then schema keyword; semantic checks use the scan order above; and input validation checks `projectPath` before `sourcePath`, with pointer `/projectPath` or `/sourcePath`. `schemaKeyword` appears only for stage-6 errors.

Policy errors are not M0.2 audit-result reasons and do not produce ordinary symbol audit results.
