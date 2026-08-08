# Audit CLI v1 (M1)

Status: Pre-release draft; current behavior is defined and validated at the checked-out repository revision.

Decision owner: Repository owner; this contract becomes a repository decision through human-reviewed PR merge.

## Authority and boundary

This document is the normative contract for the `contract-scribe` M1 command-line surface: grammar, help and diagnostic bytes, path rules, the execution envelope, run classification, exit codes, and process outcomes. It owns CLI-level decisions only. It does not amend ADR 0002, ADR 0003, or the Policy/Configuration, Symbol and Evidence Taxonomy, or Audit Result contracts, and it does not implement anything.

The executable annex at `tests/fixtures/m1-audit-cli/cli-contract-v1.json` and the test-only checker `tests/ContractScribe.Tests/M1AuditCliContractTests.cs` provide behavioral conformance coverage for this contract. Annex, affected fixtures, tests, implementation, and this document change coherently when the behavior changes; the annex is not a protected-input manifest or release-authorization boundary.

Under [Contract lifecycle](../00_project/contract-lifecycle.md), this pre-release draft may change incompatibly in place. The current repository revision defines its exact semantics, and affected tests plus exact-head review and CI validate a coherent change. Historical milestone commits remain evidence for their own revisions; they do not create an active protected-input map, successor-baseline requirement, or separate promotion gate for later work.

The checker directly uses the shared test-only Audit Result oracle `tests/ContractScribe.Tests/AuditResultConformance.cs`, the canonical byte implementation it delegates to at `tests/ContractScribe.ContractBaselineProbe/AuditResultCanonicalizer.cs`, and the independent Taxonomy classification oracle `tests/ContractScribe.ContractBaselineProbe/ClassificationConformanceOracle.cs`. It also exercises the current matrices `tests/fixtures/symbol-evidence-taxonomy/v1/classification-origin-skip-vectors.json` and `tests/fixtures/audit-result/v1/repository-candidate-locator-vectors.json`. These sources and fixtures are updated only when an affected behavior changes; they are not duplicated into a hash closure. Run classification depends only on `auditOutcome` values.

## Grammar and retained surface

The audit invocation grammar is:

```text
contract-scribe audit --repository-root <path> --input <path> --policy <path> --output <path>
```

- All four options are required, take exactly one value each, and may appear in any order.
- Both `--option value` and `--option=value` forms are accepted.
- Duplicate options are rejected. Option names are case-sensitive; no abbreviations and no short aliases exist. There are no positional operands; `--` is unsupported; response files are unsupported; the CLI performs no environment-variable or glob expansion.
- `--input` extensions are matched ASCII case-insensitively against the closed set `.sln`, `.slnx`, `.csproj`.
- Option values containing control characters are rejected at the usage layer.
- An empty `--option=` value reports `missing-option-value`. In the space form, a token beginning with `--` is never consumed as an option value: the preceding option reports `missing-option-value`, and that token is then classified on its own merits.

Retained bootstrap surface: no arguments prints the top-level help (stdout, exit 0); `--help`/`-h`, `--version`/`-v`, and `doctor` keep their command meaning, target stream, and exit code. The top-level help is extended to list `audit`. `audit --help` and `audit -h` print the complete fixed audit help (stdout, exit 0). `contract-scribe --help audit`, `contract-scribe --version audit`, `audit --version`, and `doctor` with any trailing argument are invalid usage (stderr, exit 2). An unknown command or option produces a bounded, stable stderr diagnostic with exit 2 and never echoes raw argument text.

Intentional presentation-contract change from the bootstrap: every byte the CLI produces is UTF-8 without BOM with LF newlines, no ANSI sequences, no terminal-width dependence, and no localization. This replaces the bootstrap's `Environment.NewLine`; command meaning, target streams, and exit codes are retained.

## Help texts

The following two texts are the complete, fixed help surfaces. The raw bytes (UTF-8 without BOM, LF newlines) are pinned as `tests/fixtures/m1-audit-cli/help-top-level.txt` and `tests/fixtures/m1-audit-cli/help-audit.txt` and are reproduced here verbatim:

```text
ContractScribe CLI

Usage:
  contract-scribe [--help | --version | doctor]
  contract-scribe audit --repository-root <path> --input <path> --policy <path> --output <path>

Commands:
  audit       Run the deterministic XML documentation audit.
  doctor      Print an allowlisted local runtime diagnostic without network or credential access.

Options:
  -h, --help      Print this help.
  -v, --version   Print the tool version.
```

```text
ContractScribe audit

Usage:
  contract-scribe audit --repository-root <path> --input <path> --policy <path> --output <path>

Options:
  --repository-root <path>  Repository root directory. Must exist.
  --input <path>            Audit input (.sln, .slnx, or .csproj). Must resolve inside the repository root.
  --policy <path>           Policy configuration file. Must resolve inside the repository root.
  --output <path>           Audit result file. Must resolve outside the repository root.
  -h, --help                Print this help.

All four path options are required, take exactly one value, and may appear in any order. Both "--option value" and "--option=value" forms are accepted.

Exit codes:
  0  No violations (also help, version, and doctor).
  1  Documentation violations found.
  2  Invalid command-line usage.
  3  No audit judgments (no results, or every result skipped).
  4  Invalid input or unavailable environment.
  5  Load, audit, or publication failure.
  6  Cancelled.
  7  Timeout.
```

`--version` prints exactly `ContractScribe ${TOOL_VERSION}` followed by one LF. `doctor` prints exactly the keys `application_version`, `runtime_description`, `process_architecture`, `runtime_identifier`, `network_access`, `credential_access` in that order, one `key: value` line each; `network_access` and `credential_access` always carry the value `not performed`.

## Diagnostic-code ownership

Four disjoint namespaces carry diagnostic identity:

1. `cli.*` codes, owned by this contract. The closed set is: `cli.usage.unknown-command`, `cli.usage.unknown-option`, `cli.usage.missing-required-option`, `cli.usage.duplicate-option`, `cli.usage.missing-option-value`, `cli.usage.invalid-option-value`, `cli.usage.unexpected-operand`, `cli.usage.forbidden-combination`, `cli.preflight.repository-root`, `cli.preflight.input`, `cli.preflight.input-escape`, `cli.preflight.policy`, `cli.preflight.policy-escape`, `cli.preflight.output-parent`, `cli.preflight.output-inside-root`, `cli.preflight.output-reparse`, `cli.audit.skipped-summary`, `cli.cancel.requested`, `cli.host.unknown-terminal`.
2. Host execution-failure codes, owned by the #24 production host contract. The CLI passes them through verbatim and opaquely; it maps only the host's normalized terminal class. An unknown or unmapped host terminal class fails closed (see Streams, envelope, and encoding).
3. Audit Result `reasonCode` values, owned by the current coordinated Audit Result contract baseline. The CLI never redefines them, never uses them for classification, and carries them only as verbatim data inside the `cli.audit.skipped-summary` record.
4. Platform process observations (OS exit statuses and signals). These carry no ContractScribe code.

## Diagnostic records and stderr

Every stderr line is exactly one diagnostic record, LF-terminated, in the form `<code>: <message>`. `<code>` is either a `cli.*` code or a verbatim #24 host code. `<message>` is a fixed per-code template whose only variable data slots are the closed placeholders (`<repository-root>`, `<input>`, `<policy>`, `<output>`) or repository-relative paths rendered after safe confinement; the `cli.audit.skipped-summary` record additionally carries the `<skipped-reason-breakdown>` slot defined below. Control characters in data are escaped as `\r`, `\n`, `\t`, or `\u00xx`. Messages never echo raw argument text. Records are bounded and deterministically ordered and deduplicated.

The fixed message template for each `cli.*` code is:

| Code | Message template |
| --- | --- |
| `cli.usage.unknown-command` | the command is not recognized; run 'contract-scribe --help' for usage |
| `cli.usage.unknown-option` | the option is not recognized for this command |
| `cli.usage.missing-required-option` | a required option is missing |
| `cli.usage.duplicate-option` | an option was specified more than once |
| `cli.usage.missing-option-value` | an option is missing its required value |
| `cli.usage.invalid-option-value` | an option value is not permitted |
| `cli.usage.unexpected-operand` | positional operands are not supported |
| `cli.usage.forbidden-combination` | the argument combination is not permitted |
| `cli.preflight.repository-root` | `<repository-root>` does not exist or is not a directory |
| `cli.preflight.input` | `<input>` does not exist, is not a regular file, or has an unsupported extension |
| `cli.preflight.input-escape` | `<input>` resolves outside `<repository-root>` |
| `cli.preflight.policy` | `<policy>` does not exist or is not a regular file |
| `cli.preflight.policy-escape` | `<policy>` resolves outside `<repository-root>` |
| `cli.preflight.output-parent` | the parent directory of `<output>` does not exist |
| `cli.preflight.output-inside-root` | `<output>` does not resolve outside `<repository-root>` |
| `cli.preflight.output-reparse` | `<output>` is a symbolic link, junction, or reparse point |
| `cli.audit.skipped-summary` | skipped results by reason: `<skipped-reason-breakdown>` |
| `cli.cancel.requested` | a cancellation signal was received; cancelling |
| `cli.host.unknown-terminal` | the host reported an unknown or unmapped terminal class |

The skipped breakdown is a single `cli.audit.skipped-summary` record; the verbatim canonical `reasonCode` values and their counts are message data of that record, not diagnostic identifiers. The `<skipped-reason-breakdown>` slot is rendered as comma-joined `<reasonCode>=<count>` pairs in ordinal `reasonCode` order, using only verbatim closed-registry identifiers and decimal counts. Host codes pass through in the form `<verbatim-host-code>: <bounded host message>` with the same escaping rules; in annex templates, `<verbatim-host-code>` and `<bounded host message>` are the closed slots for that pass-through form. The envelope `diagnosticCodes` array lists each emitted record's code in emission order and corresponds 1:1 to the emitted stderr records; duplicate codes are permitted when multiple emitted records share one code.

## Phase state graph and terminal commits

P0 validation runs in a fixed order; the first failure wins, and each check commits to exactly one terminal row. Within a stage, simultaneous faults resolve by the stage's sub-precedence order, so every invocation selects exactly one fault and one diagnostic code:

1. Grammar and usage (unknown, duplicate, or missing options or values; operands; forbidden combinations) → CLI usage failure. Sub-precedence: `forbidden-combination` → `unknown-option` → `duplicate-option` → `missing-option-value` → `invalid-option-value` → `unexpected-operand` → `missing-required-option`. Whole-argv structural faults outrank token-identity faults, which outrank value faults; `missing-required-option` ranks last because it can only be judged once the full argument list is known.
2. Repository root exists and is a directory → CLI preflight input failure (`cli.preflight.repository-root`). Single check; no sub-precedence.
3. Input exists, is a regular file, has an accepted extension, and resolves confined → CLI preflight input failure (`cli.preflight.input`, `cli.preflight.input-escape`). Sub-precedence: `input-escape` → `input-nonexistence` → `input-not-regular-file` → `input-unsupported-extension`. Confinement is judged on the resolved path before filesystem probes, so an escape always outranks existence and shape faults.
4. Policy exists, is a regular file, and resolves confined → CLI preflight input failure (`cli.preflight.policy`, `cli.preflight.policy-escape`). Sub-precedence: `policy-escape` → `policy-nonexistence`.
5. Output parent exists, the resolved parent is outside the resolved root, and the final-component rules hold → CLI preflight input failure (`cli.preflight.output-parent`, `cli.preflight.output-inside-root`, `cli.preflight.output-reparse`). Sub-precedence: `output-inside-root` → `output-missing-parent` → `output-final-reparse`.

The four terminal rows are:

- **CLI usage failure**: `usage` layer; exit 2; output untouched; no host record. Two controlled classes share this row: `top-level-usage-failure` (top-level grammar faults — unknown command, `doctor` with operands, `--help`/`--version` combined with anything — no envelope, bounded stderr records only) and `audit-usage-failure` (audit-grammar faults; CLI usage envelope variant). The deterministic envelope emission is the single observable CLI terminal disposition.
- **CLI preflight input failure**: `preflight` layer; `invalid-input` class; exit 4; CLI envelope variant; output untouched; no host record. A pre-existing output artifact is not evidence of the failed invocation.
- **Host execution non-success**: after the P1 run-start invalidation (ADR 0002 §9, before the first failure-prone stage), a handled non-success commits the host's normalized non-success terminal record (P4a); `execution` layer; exits 4/5/6/7 per class; no canonical result exists. A failure of the run-start invalidation itself — a prior output locked or unremovable while the process remains alive — commits the `publication-failure` record; cancellation accepted during invalidation commits the `cancelled` record.
- **Host audit success**: the atomic canonical-result replacement is the terminal commit (P4b: staging in the resolved destination directory, full write, rename); `audit` layer; exits 0/1/3. The success terminal record is derived from the committed bytes, not from a second commit.

Envelope emission (P5) is a later, non-authoritative presentation step for host outcomes, and is the disposition itself for CLI-only outcomes. The lifecycle matrix is closed: a non-success commit followed by a crash before envelope emission, or by an stdout failure, leaves the committed outcome authoritative; a success commit followed by a crash before envelope emission leaves the committed result authoritative and the envelope may never exist (external observation per the host contract); a success commit followed by an stdout failure leaves the result standing; a crash before either applicable commit leaves only a platform status. The matrix also covers the invalidation window: pre-entry failure before invalidation completes (platform status only; a prior artifact may remain and is never evidence of the invocation); invalidation failure while the process remains alive (the `publication-failure` commit); cancellation during invalidation (the `cancelled` commit); and abrupt termination during invalidation (invalidation may be partial; the prior artifact may be removed or remain and is never evidence; nothing is readable as the current result).

## Paths

- `--repository-root`: absolute, or relative resolved against the process working directory as argument preprocessing; all post-resolution behavior is working-directory independent. The resolved root must exist and be a directory.
- `--input`, `--policy`: absolute or repository-relative. Every existing ancestor is reparse-point resolved; the final resolved path must be lexically under the resolved root; escapes are rejected; the target must be a regular file.
- `--output`: absolute, or working-directory-relative as argument preprocessing. The deterministic safety rule: resolve every existing ancestor and the destination parent; the resolved parent must be outside the resolved repository root — a lexically-outside path that resolves inside is rejected. Staging is created in the resolved destination parent (the same filesystem by construction). The final target may be absent or an existing regular file; a final symlink, junction, or reparse point is rejected in preflight and is never followed or replaced. In-repository output is rejected in P0 before invalidation: in M1 the output must resolve outside the repository root, and a future reserved in-repository results directory is the documented relaxation path. The parent must pre-exist; the CLI creates none. A publication failure commits the host `publication-failure` record; staging cleanup is attempted while the process is alive, and orphan staging after an uncatchable termination is never authoritative. The output can never alias a confined input or policy path.
- Diagnostics never echo absolute paths: repository-relative rendering (canonical `/` separators, M0.1 lexical rules) only after safe confinement; otherwise the closed placeholders `<repository-root>`, `<input>`, `<policy>`, `<output>`.
- Concurrent invocations sharing one output path are unsupported; such use voids result-attribution guarantees.

## Streams, envelope, and encoding

Canonical Audit Result bytes appear only at `--output`, atomically published; they never appear on any stream.

`audit` stdout carries exactly one execution envelope per controlled return of a recognized `audit` invocation. Usage failures split into two controlled classes: `audit-usage-failure` (a grammar fault inside a recognized `audit` invocation) emits the usage envelope, while `top-level-usage-failure` (unknown command, `doctor` with operands, `--help` or `--version` combined with anything) emits no envelope — only bounded stderr records with exit 2.

The envelope is a draft machine-readable CLI contract: `envelopeVersion: 1` is its artifact compatibility-family identifier and `cliContractBaseline` identifies its exact draft semantics (the exact source commit of the CLI/contract revision). Consumers reject an unsupported `envelopeVersion`; no cross-revision compatibility is promised without matching baseline identity. The version remains 1 while no incompatible consumer requires coexistence. The annex and checker exercise this behavior.

Two explicit variants share the common fields, in fixed order: `envelopeVersion`, `terminalLayer`, `cliContractBaseline`, `toolVersion` (assembly informational version, for example `0.1.0-dev+<sha>`), `diagnosticCodes`.

- **CLI variant** (`terminalLayer`: `usage` or `preflight`): adds `usageClass` (usage only; closed set `unknown-option`, `missing-required-option`, `duplicate-option`, `missing-option-value`, `invalid-option-value`, `unexpected-operand`, `forbidden-combination`) or `executionClass: invalid-input` (preflight only). Forbidden: `terminalState`, `sourceRevision`, `toolchain`, `disposition`, `counts`, `resultDigest`, `outputCommit`.
- **Host variant** (`terminalLayer`: `execution` or `audit`): the CLI-owned serialization of the host's normalized terminal record. Adds `terminalState: committed`, `sourceRevision` (exact source revision from host/tool provenance), `executionClass` (execution only: `invalid-input`, `environment-unavailable`, `load-failure`, `audit-error`, `publication-failure`, `cancelled`, `timeout`), `disposition` (audit only), `counts` (audit only: `compliant`, `violation`, `skipped`), `toolchain` (selected normalized SDK/MSBuild identity; present exactly in the `selected` forms of the toolchain-state matrix below), and `resultDigest` plus `outputCommit` (audit only). `resultDigest` is the SHA-256 of the exact committed canonical Audit Result bytes — the bytes standing at `--output` after the atomic rename (P4b) — rendered as 64 lowercase ASCII hexadecimal characters. The digest binds the envelope to those canonical bytes, never to any re-serialization of the result; the CLI must not substitute another algorithm, input byte stream, or text encoding. The annex `resultDigestCases` section carries the conformance vector. `outputCommit` is the host contract's closed structure — commit status plus an opaque public-safe commit identity — never a path or placeholder.

Execution stream forms are keyed by a closed `toolchainState` dimension (`selected` or `not-selected`) in the annex; a `not-selected` form omits `toolchain`. The closed matrix is:

| execution class | permitted toolchainState forms |
| --- | --- |
| `invalid-input` | `not-selected` only — policy and configuration validation precede SDK resolution (ADR 0002) |
| `environment-unavailable` | `not-selected` only — no toolchain could be selected |
| `load-failure` | `selected` only |
| `audit-error` | `selected` only |
| `publication-failure` | `not-selected` (run-start invalidation failure) or `selected` (final publication failure) |
| `cancelled` | `not-selected` (cancellation during invalidation) or `selected` (cancellation after selection) |
| `timeout` | `not-selected` (SDK-discovery timeout — SDK/global.json resolution precedes toolchain selection) or `selected` (load, total-audit, and applicable graceful-shutdown timeouts) |

The envelope encoding is compact JSON, UTF-8 without BOM, exactly one trailing LF, fixed property order, fields omitted when not applicable (no explicit nulls), and no timestamps, durations, process IDs, or absolute paths.

### Substitution grammar

Dynamic envelope fields use a closed substitution grammar in annex templates. The closed token set is `${CLI_CONTRACT_BASELINE}`, `${TOOL_VERSION}`, `${SOURCE_REVISION}`, `${TOOLCHAIN_IDENTITY}`, `${RESULT_DIGEST}`, `${OUTPUT_COMMIT_IDENTITY}`.

- CLI-variant envelopes (`usage`, `preflight`) and the `host-contract-error` envelope permit only `${CLI_CONTRACT_BASELINE}` and `${TOOL_VERSION}`.
- The `execution` envelope additionally permits `${SOURCE_REVISION}` and `${TOOLCHAIN_IDENTITY}`.
- The `audit` envelope permits all six tokens.
- Token values are JSON strings only — never raw fragments, numbers, or objects — and are escaped per the canonical string rules (quotation mark, reverse solidus, and control characters escaped; all other scalars literal).
- Bindings are platform-independent: a token's value is identical on every supported platform for one build and one run.
- An unknown, missing, or duplicated substitution fails the checker.

### Unknown host terminal class

The closed envelope model includes a CLI-owned adapter-failure representation for an unknown or unmapped host terminal class. It is not a host class and never masquerades as one:

- `terminalLayer: host-contract-error`; exit 5; exactly one diagnostic record with code `cli.host.unknown-terminal`.
- Required fields: only the common fields `envelopeVersion`, `terminalLayer`, `cliContractBaseline`, `toolVersion`, `diagnosticCodes`.
- Forbidden fields: all host provenance fields — `terminalState`, `sourceRevision`, `toolchain`, `executionClass`, `disposition`, `counts`, `resultDigest`, `outputCommit` — and `usageClass`.

### Representative envelope templates

The annex pins one exact expected stdout template per controlled stream form (every `exitCodeCases` row with a controlled return maps to exactly one envelope or stream form, with execution forms keyed by the closed `toolchainState` dimension — including the toolchain-omitted `invalid-input`, `environment-unavailable`, invalidation-window `publication-failure`/`cancelled`, and SDK-discovery `timeout` forms — and empty-`diagnosticCodes` audit forms). The following representative templates (each followed by exactly one LF) are reproduced verbatim from the annex:

```text
{"envelopeVersion":1,"terminalLayer":"usage","cliContractBaseline":"${CLI_CONTRACT_BASELINE}","toolVersion":"${TOOL_VERSION}","diagnosticCodes":["cli.usage.unknown-option"],"usageClass":"unknown-option"}
```

```text
{"envelopeVersion":1,"terminalLayer":"preflight","cliContractBaseline":"${CLI_CONTRACT_BASELINE}","toolVersion":"${TOOL_VERSION}","diagnosticCodes":["cli.preflight.input"],"executionClass":"invalid-input"}
```

```text
{"envelopeVersion":1,"terminalLayer":"execution","cliContractBaseline":"${CLI_CONTRACT_BASELINE}","toolVersion":"${TOOL_VERSION}","diagnosticCodes":["cli.cancel.requested"],"terminalState":"committed","sourceRevision":"${SOURCE_REVISION}","toolchain":"${TOOLCHAIN_IDENTITY}","executionClass":"cancelled"}
```

```text
{"envelopeVersion":1,"terminalLayer":"audit","cliContractBaseline":"${CLI_CONTRACT_BASELINE}","toolVersion":"${TOOL_VERSION}","diagnosticCodes":["cli.audit.skipped-summary"],"terminalState":"committed","sourceRevision":"${SOURCE_REVISION}","toolchain":"${TOOLCHAIN_IDENTITY}","disposition":"violations-with-skipped","counts":{"compliant":1,"violation":1,"skipped":1},"resultDigest":"${RESULT_DIGEST}","outputCommit":{"status":"committed","identity":"${OUTPUT_COMMIT_IDENTITY}"}}
```

```text
{"envelopeVersion":1,"terminalLayer":"host-contract-error","cliContractBaseline":"${CLI_CONTRACT_BASELINE}","toolVersion":"${TOOL_VERSION}","diagnosticCodes":["cli.host.unknown-terminal"]}
```

There is no stable cross-stream byte ordering. A broken pipe or stream-write failure after a commit does not invalidate the committed outcome or result; abnormal platform status is an external observation.

## Run classification

Classification is non-canonical CLI behavior layered on a committed canonical result. Three semantic layers are never conflated: `auditOutcome` (outcome), `documentationObservation` (observation), and `reasonCode` (canonical reasons). `documentation.unavailable` is never a run disposition; `policy-unavailable` is never an execution failure.

Precondition: successful host execution and a produced canonical result passing validation — UTF-8 without BOM, a single trailing LF, and schema plus semantic validity against the pinned baseline with a complete `results` array. A produced result failing validation is the host execution class `audit-error`, never a disposition. Artifact-version rejection (an unsupported `auditResultVersion` integer) and contract-baseline/provenance identity mismatch are distinct fail-closed paths, both `audit-error`; the baseline identity lives outside the canonical Audit Result bytes, in the host terminal record. Classification reads only `auditOutcome` values. Writing V, C, S for the presence of at least one `audit.outcome.violation`, `audit.outcome.compliant`, and `audit.outcome.skipped` result:

| V | C | S | Disposition | Exit |
| --- | --- | --- | --- | --- |
| yes | any | no | `violations` | 1 |
| yes | any | yes | `violations-with-skipped` | 1 |
| no | yes | no | `compliant` | 0 |
| no | yes | yes | `compliant-with-skipped` | 0 |
| no | no | yes | `skipped-only` | 3 |
| no | no | no (empty) | `no-results` | 3 |

Every (V, C, S) mixture maps to exactly one disposition; the skipped-reason vocabulary never changes the disposition or exit code.

## Exit codes

Exit codes are selected by ContractScribe only on controlled return paths. They are small positive integers; 126, 127, and values at or above 128 are never ContractScribe-selected.

| Exit | Controlled classes |
| --- | --- |
| 0 | `compliant`, `compliant-with-skipped`; retained `--help`, `--version`, `doctor` |
| 1 | `violations`, `violations-with-skipped` (successful execution carrying violation outcomes; stated in help) |
| 2 | `top-level-usage-failure` (no envelope) and `audit-usage-failure` (usage envelope); any usage class |
| 3 | `no-results`, `skipped-only` |
| 4 | `invalid-input` (CLI preflight or host) and `environment-unavailable` (host); distinguished by layer and host codes |
| 5 | `load-failure`, `audit-error` (including an invalid produced result), `publication-failure`, and the CLI adapter failure for an unknown host terminal class (`host-contract-error`) |
| 6 | `cancelled` |
| 7 | host-committed `timeout` |

Sharing is exactly as documented per row; no other sharing is permitted.

## Process outcomes

Process outcomes carry no application-selected code.

- **Pre-entry failure** (OS launch, runtime load, permission, managed bootstrap before run-start invalidation completes): platform status only; no committed record; no envelope; no new result. A pre-existing artifact cannot be guaranteed removed and must not be consumed. Startup and pre-entry timeouts are caller- or OS-enforced and codeless.
- **Invalidation-window outcomes**: a run-start invalidation failure while the process remains alive (prior output locked or unremovable) maps to the controlled `publication-failure` commit (exit 5); cancellation accepted during invalidation maps to the controlled `cancelled` commit (exit 6); an abrupt termination during invalidation is a platform observation only — invalidation may be partial, the prior artifact may be removed or remain and is never evidence of the invocation, and nothing is readable as the current result.
- **Abrupt crash, abort, or kill after invalidation but before the applicable commit**: platform status or signal; no committed outcome; no artifact readable as the current result; orphan staging is never authoritative; no forced cleanup is claimed.
- **Post-commit precedence**: the committed P4a/P4b outcome is authoritative; any later abnormal platform status, including a signal or a graceful-shutdown timeout after commit, is an external observation.

There is no supervising launcher and no `Environment.Exit` requirement. Platform-scoped signal handling: on Windows, Ctrl+C and Ctrl+Break map to cooperative cancellation with identical handling, and `TerminateProcess` is unhandleable; on Unix, SIGINT and SIGTERM map to cooperative cancellation through registered handlers, and SIGKILL is unhandleable. A signal arriving before handler registration is a platform observation. The first handled signal produces one `cli.cancel.requested` record and enters the cancellation path; repeated signals do not escalate and never change the selected class. Help, version, and doctor invocations have no cancellation behavior.

## Unsupported inputs and normative absences

`.slnf` solution filters, non-C# extensions, and all other input forms are preflight `invalid-input` (exit 4). There is no auto-discovery and no auto-restore. Solution content questions (mixed-language or unloadable projects) are host-owned and map through the standard host execution classes.

The following are normative absences, proven by the absence of any accepting parser fixture: provider or model selection, GitHub integration, auto-discovery, auto-restore, update checks, telemetry, disk logs, distribution or RID options, worker or child-process control, and batch modes.

## Executable annex and checker

The annex `tests/fixtures/m1-audit-cli/cli-contract-v1.json` carries: `meta` (envelope version and live shared-oracle bindings); `commands` and `options` (closed grammar tables); `helpCases` (static help byte fixtures, the version template, the doctor key order and value grammar); `parseCases`; `precedenceCases` (multi-fault collision rows recording each stage's sub-precedence — the selected first-failure code and stream form); `pathCases` with platform scoping and the closed path templates `${REPOSITORY_ROOT}`, `${OUTSIDE_ROOT}`, `${INPUT}`, `${POLICY}`, `${OUTPUT}`; `resultValidationCases`; `classificationCases` including `skippedReasonCases`; `resultDigestCases` (the `resultDigest` conformance vector over exact committed canonical bytes); `streamCases`; `terminalLifecycleCases`; `exitCodeCases`; `cancellationCases`; and `publicSafetyConstraints`.

The checker `tests/ContractScribe.Tests/M1AuditCliContractTests.cs` enforces: closed vocabularies; stage-internal validation precedence via the collision rows; the closed `toolchainState` dimension on execution stream forms; every (V, C, S) mixture mapping to exactly one disposition; every controlled class mapping to exactly one exit code with no undeclared sharing; both envelope variants' required and forbidden fields including the `host-contract-error` adapter failure; annex-to-document consistency for the option table, exit-code table, help bytes, envelope templates, and live shared-oracle bindings; referenced raw-fixture integrity; validity of reused and synthetic payloads through the shared `AuditResultConformance` oracle; the `resultDigest` vector recomputed over canonical bytes; and public safety (closed `${...}` path templates only, no machine-local absolute paths, no credentials, bounded strings). Annex metadata stays limited to fields required by these current behavioral checks rather than maintaining hashes or promotion state for unrelated current-tree files.

## References

- [ADR 0002: Production process topology](decisions/0002-process-topology.md)
- [ADR 0003: Target profiles and direct documentation observation](decisions/0003-target-profiles-and-documentation-observation.md)
- [Policy/Configuration v1](contracts/policy-configuration-v1.md)
- [Symbol and Evidence Taxonomy v1](contracts/symbol-evidence-taxonomy-v1.md)
- [Audit Result v1](contracts/audit-result-v1.md)
- [Contract lifecycle](../00_project/contract-lifecycle.md)
- [Issue #25](https://github.com/SolusQuest/contract-scribe/issues/25)
