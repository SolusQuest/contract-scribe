# Policy/Configuration v1

Policy/Configuration v1 is a provisional, repository-neutral contract for selecting a documentation expectation from a caller-supplied project/source-path pair. It defines policy expectations, not observed audit results, and makes no filesystem, Roslyn, provider, or platform-adapter commitment.

## Authority and status

This document owns the semantic behavior, validation-stage order, and error-code meanings. [The JSON Schema](../../../schemas/policy-configuration/v1.schema.json) owns document-shape validation only. The schema has no remote `$ref` dependency.

The contract is provisional until the M0 contract freeze. Before then, an exact semantic reference must pin a repository commit. After freeze, a breaking change requires a new schema version and identifier.

## Document and input boundary

V1 accepts one supplied JSON policy document encoded as UTF-8 without a BOM. It does not define a file name, discovery, parent search, environment override, multi-file merge, inheritance, remote include, filesystem access, realpath, symlink behavior, or automatic migration.

The caller supplies `projectPath` and `sourcePath`, both required non-empty lexical paths. Establishing a repository root and relativizing filesystem paths are host responsibilities. Evaluation is pure lexical behavior.

## Policy model

`schemaVersion` is the required integer `1`. `defaultDecision` is required and is one of:

- `required`: absent XML documentation is a future audit violation.
- `optional`: neither presence nor absence is a documentation-requirement violation.
- `forbidden`: present XML documentation is a future audit violation.

These values are policy expectations, not M0.2 per-symbol audit-result reasons.

`rules` is optional; omitted and `[]` are equivalent. A rule has a unique ID, a unique priority, a decision, and optional `projectPaths` and `sourcePaths` selectors. A rule with no selectors is global.

A selector accepts when at least one `include` pattern matches, if `include` is present, and no `exclude` pattern matches. Exclude wins when both lists match. A rule applies only when every declared selector accepts. The applicable rule with the greatest priority wins; otherwise `defaultDecision` applies. Global priority uniqueness makes precedence statically decidable before evaluation.

V1 intentionally has no symbol-category selector. M0.3 owns taxonomy vocabulary, and any future selector requires a separately versioned contract change.

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
5. JSON Schema shape validation: `policy.schema.invalid-document`.
6. Semantic validation: `policy.semantic.duplicate-rule-id`, `policy.semantic.duplicate-priority`, or `policy.semantic.invalid-pattern`.
7. Evaluation input validation: `policy.input.invalid-path`.
8. Rule resolution: an effective decision and matched rule ID, or a null matched rule ID for default fallback.

Error outcomes contain `code`, an RFC 6901 `pointer` when an instance location exists, and `schemaKeyword` only for stage 5. Human-readable messages are non-normative.

Any duplicate property, including `schemaVersion`, fails at stage 3. Stage 4 applies only to an object with exactly one integer `schemaVersion` other than `1`. Missing, null, string, Boolean, non-integral, and non-object schema-version cases fail at stage 5.

Within a failing stage, the oracle selects a canonical outcome: a leading BOM wins over later encoding defects; lexical parsing uses the earliest byte-offset violation; schema leaf failures sort by ordinal instance pointer and then schema keyword; semantic checks scan duplicate IDs, then duplicate priorities, then patterns; and input validation checks `projectPath` before `sourcePath`.

Policy errors are not M0.2 audit-result reasons and do not produce ordinary symbol audit results.
