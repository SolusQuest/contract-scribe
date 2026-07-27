# M1 host-validation protocol

## Status and ownership

This document and the machine-readable bundle under `tests/fixtures/m1-host-validation/v1` freeze version 1 of the M1 validation oracle for the production in-process audit host.

Issue #26 owns the protocol, schemas, vectors, harness, self-tests, and independent review record. It does not implement the production host, execute the final matrix against a claimed host revision, or publish a passing host aggregate. Issue #24 owns the real production composition and the prebuilt test-only production subject entry point. The later exact-revision host-validation execution issue selects that entry point and executes this bundle unchanged.

The project-boundary declaration for `ContractScribe.HostValidation` is recorded in Issue #26. The harness is a test-only, non-packable `net10.0` executable with no ContractScribe project reference. `JsonSchema.Net` is its only non-BCL runtime package. `ContractScribe.Tests` may reference the harness one way. No production project may reference the harness or an M0 experiment assembly.

## Frozen identities

The protocol pins the coordinated pre-release v1 baseline merged by Issue #35 at `bb4654edc180e2953dda6b89a29211b18778b78e`. The protected-input manifest records the byte identities of the baseline contracts, schemas, fixtures, conformance code, ADR 0002, the security boundary, and project-structure rules.

The oracle artifact lock contains the ordinal path and SHA-256 of every protocol, schema, manifest, harness source, test, build-policy, and workflow artifact that can change validation behavior. The bundle ID is:

```text
m1hvp1.<sha256>
```

Its digest input is each artifact-lock entry in ordinal path order:

```text
UTF8(path) || NUL || ASCII(lowercase-sha256) || NUL
```

The artifact lock and independent review record are not bundle members. No bundle member embeds the computed bundle ID. The lock entry set must equal `protocol.json.artifactInventory`; missing, duplicate, unexpected, stale, or hash-mismatched entries invalidate the bundle.

Execution uses three additional identity layers:

1. A cross-cell source/configuration manifest binds the exact Issue #24 source revision, production sources and projects, props/targets, package and lock inputs, failure registry, calibrated-bound manifest, build recipe, command/control contract, logical fixtures, contract baseline, workflow revision, runner policy, and environment policy.
2. A per-cell materialization manifest binds the concrete runner image, RID, architecture, selected SDK/runtime/MSBuild, built production subject and harness artifacts, platform fixture realization, and bounded process observations.
3. A validation-execution identity binds workflow identity and revision, workflow run ID, run attempt, validation execution SHA, exact host revision, per-cell job IDs and immutable URLs, upstream matrix result, and evidence-publication revision.

Ubuntu and Windows evidence from different workflow runs or run attempts cannot be combined. Reruns create immutable new evidence instances. They may supersede, but never overwrite or splice, earlier records. A changed source, build input, package, toolchain, runner image, environment policy, workflow, protocol, or materialized artifact invalidates evidence bound to the prior identity.

## Required matrix and run expansion

The required cells are:

| Cell | Runner policy | Architecture | RID |
| --- | --- | --- | --- |
| `ubuntu-x64` | GitHub-hosted `ubuntu-latest`, recording the concrete image | X64 | `linux-x64` |
| `windows-x64` | GitHub-hosted `windows-latest`, recording the concrete image | X64 | `win-x64` |

The requested SDK policy from `global.json` and the actually selected SDK, runtime, and MSBuild identities are separate evidence fields. Runner labels are not concrete runner-image identities.

Each vector freezes applicable cells, exact invocation count, stable run IDs, fresh-process requirements, compared fields, cross-cell equality applicability, fixture/provisioning recipe, expected observation, expected enforcement class, support disposition, independent observers, and protected-input classes. Platform applicability is a `(cellId, vectorId)` relation. Execution completeness is exact set equality over `(cellId, vectorId, runId)`.

Determinism vectors use exactly two distinct fresh operating-system processes. No workspace, MSBuild registration, or process-global state is reused. Unrecorded retry is forbidden. A retry belongs to a new validation-execution attempt and retains the earlier attempt as immutable non-success evidence.

## Production subject contract

The production subject descriptor is a contract that Issue #24 must implement before exact-revision execution:

- It is a prebuilt test-only entry point over the real in-process production composition.
- It accepts one canonical request path and one response path through argument-list entries, never a shell command.
- It invokes no second ContractScribe runtime and performs no post-hoc translation into the expected response.
- It supports protocol-owned named synchronization gates and the closed control actions used for cancellation, kill-window, and late-completion vectors.
- It emits one bounded canonical response or no managed response when managed entry/commit is impossible.
- It does not write raw stdout/stderr as a result channel.

The self-test subject is structurally separate and has `allowedForProductionEvidence: false`. Self-test observations can prove harness behavior only; they can never satisfy a production cell or aggregate schema.

The named-gate mechanism uses an audit-unique system-temporary `controlRoot`. For one frozen gate name, the subject creates the zero-byte `<gate>.reached` marker only after entering the named state. The harness then creates `cancel.requested` and `<gate>.release` for cooperative cancellation/late release, or externally terminates the subject process tree for `external-kill`. Neither side infers ordering from elapsed sleeps. A missing marker at the 30-second protocol timeout is infrastructure-incomplete, and all control files are non-public temporary state deleted by the harness.

## Observation and outcome taxonomy

The subject-observation plane has distinct optional audit and handled-execution outcomes, process start, process termination, terminal state, artifact state, and enforcement class.

Process start is one of:

- `started`
- `launch-failure`
- `runtime-load-failure`
- `permission-failure`
- `startup-timeout`

Process termination is one of:

- `normal`
- `crash`
- `abort`
- `external-kill`
- `out-of-memory`
- `stack-overflow`
- `fatal-runtime-termination`
- `not-started`

Audit outcome, when a managed audit result exists, is `compliant`, `violation`, or `skipped`. A violation is a successful execution carrying a violation result. Handled execution outcome, when a managed terminal record exists, is `invalid-input`, `environment-unavailable`, `load-failure`, `audit-error`, `cancelled`, `timeout`, or `succeeded`.

Audit and execution fields are absent when no managed terminal record can exist. Every handled failure binds the exact protected Issue #24 failure-registry identity, observed production failure code, normalized failing stage, terminal-commit state, and artifact state. This protocol does not invent Issue #24 codes.

Vector verdict is one of:

- `matched`
- `subject-nonconformance`
- `vector-environment-blocked`
- `vector-infrastructure-incomplete`
- `protocol-invalid-observation`

Cell and aggregate outcomes use the following highest-first precedence:

1. `protected-input-invalidated`
2. `protocol-failure`
3. `subject-nonconformance`
4. `environment-or-infrastructure-incomplete`
5. `harness-or-ci-cancelled`
6. `harness-or-ci-timed-out`
7. `passed`

Intentional subject cancellation or kill is a matched vector only when the bounded observation equals the oracle. Cancellation, timeout, or termination of the harness, CI job, or matrix is incomplete and cannot become a passing cell.

Enforcement class is an observation, not the verdict:

- `internally-enforceable`
- `caller-or-os-enforced`
- `observable-only`
- `not-enforceable-selected-topology`

An honest selected-topology limitation passes when it exactly matches the frozen expectation. A weaker observation or falsely stronger claim fails.

## Support dispositions

| Input or behavior | M1 protocol disposition |
| --- | --- |
| Explicit `.sln`, `.slnx`, `.csproj` | Required |
| `.slnf` | Unsupported, closed handled classification |
| Non-C# project | Unsupported, closed handled classification |
| Analyzer | Trusted input; execution and effects observed, no sandbox claim |
| Source generator | Trusted input; amended generated-fact behavior required and observed |
| Custom target | Trusted input; process and repository writes observed, no sandbox claim |
| Multi-targeting | Concrete aggregation and identity owned by Issue #24; exact protected disposition required before execution |
| Missing assets | Input/environment failure; caller prepares assets |
| Restore/runtime download | ContractScribe must not initiate it |

Directory auto-discovery is not tested or claimed. Missing, invalid, and nested `global.json` follow SDK resolution rules while the selected identities are recorded.

## Independent observers

The harness snapshots a closed repository inventory before subject execution and compares existence and SHA-256 afterward. It records protected source/project creation, deletion, and content change; other unexpected writes; and separately allowlisted `bin`/`obj` design-time output. It runs after success, handled failure, cancellation, and every case where the harness survives.

The harness samples the subject process tree and distinguishes the subject runtime, additional ContractScribe workers, toolchain-owned processes, and unknown descendants. Missing-assets/no-restore validation combines process observation with absence of assets or other restore outputs.

Fatal-process fixtures record the externally arranged cause (`out-of-memory`, `stack-overflow`, or `abort`) in the per-cell fixture realization. The harness never infers an OOM from an ambiguous Unix signal alone; the arrangement, materialized helper, runner policy, and observed termination are protected together. If the cell cannot establish that cause, the capability is unavailable and the vector is incomplete rather than passing under a guessed classification.

These observers claim detection only. They do not prevent or roll back writes, capture every transient write, isolate processes, enforce memory/network limits, or create an operating-system sandbox. Observer failure produces incomplete evidence.

## Stream and public-safety boundary

Every subject process has stdout and stderr redirected and drained concurrently. Each stream is limited to 65,536 bytes. Raw stream bytes are never persisted or echoed into CI. Invalid UTF-8, overflow, unsafe markers, broken-pipe behavior, and output on both streams have closed harness classifications. Ordinary harness failures print only a stable bounded diagnostic code, never raw exception text, stack traces, environment dumps, or machine paths.

Public evidence is canonical JSON encoded as UTF-8 without BOM, compact, with object keys in ordinal order and exactly one trailing LF. The per-cell limit is 4 MiB and subject-response limit is 256 KiB. Readers reserialize and compare bytes.

The only public execution artifacts are:

- `cell-evidence.json`
- `aggregate-evidence.json`
- `incomplete-evidence.json`

The same credential-marker, machine-path, raw-log, and bounded-field scans apply to successful and non-success artifacts and to captured CI-visible harness output.

The precise network statement is: the deterministic host has no declared network dependency and ContractScribe initiates no provider, GitHub, update, telemetry, restore, runtime-download, or other declared network-dependent operation. Ordinary CI is not an egress sandbox and this protocol does not claim network isolation.

## Evidence acceptance

Before subject execution, the harness validates:

- strict JSON without comments, trailing commas, duplicate properties, BOM, invalid UTF-8, unknown versions, or unknown schema fields;
- the complete oracle artifact lock and protected-input manifest;
- protocol, vector, subject, evidence, review, and aggregate schemas;
- bidirectional requirements-to-vector/validator coverage;
- project/package/reference boundaries;
- review binding when production evidence is being accepted;
- the cross-cell source/configuration manifest and current per-cell materialization.

Per-cell evidence is accepted only when its exact run triples equal the expanded oracle set for that cell. Missing, duplicate, unexpected, substituted, or wrong-cell runs fail closed. Aggregate evidence requires exactly both cells, one bundle and accepted review, one validation-execution identity, and no mixed run attempt.

Failed, cancelled, timed-out, invalidated, or infrastructure-incomplete attempts produce immutable incomplete evidence when the harness can safely do so. A later success does not erase them.

## Independent review

An accepted review record is outside the bundle to avoid self-reference. It binds:

- the exact bundle ID;
- the exact pushed repository head reviewed;
- independent reviewer kind;
- relay session and task identities;
- an accepted verdict;
- zero blocking finding IDs;
- a bounded UTC review timestamp.

Missing, pending, mismatched, stale, or blocking review records prevent production cell and aggregate acceptance. Dry-run and self-test commands do not require an accepted record because they are not host evidence.

## Commands

All commands run from the repository and use the pinned SDK:

```text
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- lock-protected-inputs --root .
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- lock-bundle --root .
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- validate-bundle --root .
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- dry-run --root . --cell ubuntu-x64
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- dry-run --root . --cell windows-x64
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- self-test --root .
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- run-cell --root . --subject-manifest <execution-subject> --review <review> --cell <cell> --output <cell-evidence> --incomplete-output <incomplete-evidence>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- validate-cell --root . --review <review> --evidence <cell-evidence>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- validate-incomplete --root . --review <review> --evidence <incomplete-evidence>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- aggregate --root . --review <review> --evidence <ubuntu>;<windows> --output <aggregate>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- prepare-public --root . --review <review> --kind <cell|aggregate|incomplete> --source <evidence> --output <allowlisted-name>
```

`lock-protected-inputs` and `lock-bundle` are maintainer commands. Any regenerated identity requires a new exact review. The CI `validate-bundle`, `dry-run`, and `self-test` executions validate the oracle and harness on both operating systems; they are not production-host pass evidence.

## Forbidden adaptations

The execution issue must not:

- change expected observations after seeing host results;
- add candidate-specific translation or adapter logic;
- use Issue #36 internal types, test helpers, or namespaces as the oracle;
- substitute the fake subject for production evidence;
- omit a required run, cell, observer, materialization, or failed attempt;
- reuse one process for a fresh-process vector;
- retry without a new execution-attempt identity;
- accept unreviewed bundle drift;
- emit or preserve raw streams, machine paths, credentials, logs, or exception text;
- describe trusted MSBuild as sandboxed or normal CI as network-isolated.

If the host contradicts a frozen contract, fix the owning implementation. If authoritative contracts genuinely change, regenerate and independently review a new bundle rather than weakening the old oracle.
