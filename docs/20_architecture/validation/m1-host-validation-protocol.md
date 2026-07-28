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

1. A cross-cell source/configuration manifest binds the exact Issue #24 source revision and a closed, generated inventory from the frozen roots `ContractScribe.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, and `src`. It also binds the Issue #24-owned machine-readable failure registry at `src/ContractScribe.Core/Hosting/host-failure-registry-v1.json`, calibrated-bound manifest at `src/ContractScribe.Core/Hosting/host-calibrated-bounds-v1.json`, build recipe, command/control contract, contract baseline, environment policy, and validation workflow paths frozen in `protocol.json`. The two host-owned files must conform to `m1-host-failure-registry-v1.schema.json` and `m1-host-calibrated-bounds-v1.schema.json`; the latter contains exactly the eight frozen protocol bound names. The harness resolves the complete root inventory from the host revision's Git tree, verifies that the host revision is an ancestor of the validation-execution revision, and hashes every Git blob before a production run. Current-checkout enumeration and bytes must exactly match those commit-bound identities, so an added or deleted materialized source file is drift rather than an omitted input. Every named recipe or policy is a path-plus-content identity, and `sourceConfigurationId` is the canonical digest of the complete ordered record. `declaredOperationInventoryId` is independently derived by the bundle-owned evaluator version and the exact project/package, structured configuration, command contract, environment policy, and workflow identities; the subject does not supply a claimed empty operation list. `execution-subject.template.json` is deliberately non-executable: Issue #24 or the execution issue must create the two named host-owned files, expand every frozen root to the exact ordered Git-tree inventory, and replace all template identities.
2. A per-cell materialization manifest binds the concrete runner image, RID, architecture, independently observed selected SDK/runtime/MSBuild, built production subject and harness artifacts, platform fixture realization, and bounded process observations. Each managed response repeats the source-configuration, host revision, contract baseline, failure-registry, calibrated-bounds, and selected-toolchain identities; the harness compares those response facts with the protected source and cell records. It also compares normalized failure facts, output-commit status and digest, and the temporary-disk vector's measured value, unit, threshold, and enforcement class with independent observations and the protected bound manifest.
3. A pre-execution validation-attempt identity binds workflow identity and revision, workflow run ID, run attempt, validation execution SHA, and exact host revision. Per-cell job identities remain materialization facts. Matrix result and `evidencePublicationBaseRevision` are finalization identities and appear only in aggregate evidence; the base revision equals the validation execution SHA and is not a claim that the aggregate already exists in that commit. After the exact cell and aggregate bytes are committed, a separate publication record binds their repository-relative paths and hashes to the later evidence-record revision and proves both the host and validation revisions are ancestors of it.

Ubuntu and Windows evidence from different workflow runs or run attempts cannot be combined. In GitHub Actions, the harness compares the claimed run, attempt, job, execution SHA, operating system, and architecture with the running environment. Reruns create immutable new evidence instances. They may supersede, but never overwrite or splice, earlier records. A changed source, build input, package, toolchain, runner image, environment policy, workflow, protocol, or materialized artifact invalidates evidence bound to the prior identity.

## Required matrix and run expansion

The required cells are:

| Cell | Runner policy | Architecture | RID |
| --- | --- | --- | --- |
| `ubuntu-x64` | GitHub-hosted `ubuntu-latest`, recording the concrete image | X64 | `linux-x64` |
| `windows-x64` | GitHub-hosted `windows-latest`, recording the concrete image | X64 | `win-x64` |

The requested SDK policy from `global.json` and the actually selected SDK, runtime, and MSBuild identities are separate evidence fields. Runner labels are not concrete runner-image identities.

Each vector freezes applicable cells, exact invocation count, stable run IDs, fresh-process requirements, compared fields, cross-cell equality applicability, fixture/provisioning recipe, expected observation, expected enforcement class, support disposition, independent observers, and protected-input classes. `FrozenFixtureRegistry` additionally fixes the per-cell/per-vector repository root, complete pre-run repository identity, result path and prestate, exact design-time allowlist, observation mode, external cause, provisioning paths, process-identity slots, and each run's working-directory mode. Ordinary result-producing runs begin with an absent target; the stale-invalidation vector begins with a protected invalid sentinel so a pre-existing valid file cannot be mistaken for current output. The working-directory vector runs once at the repository root and once from its audit-unique system-temporary directory. Platform applicability is a `(cellId, vectorId)` relation. Execution completeness is exact set equality over `(cellId, vectorId, runId)`.

Determinism vectors use exactly two distinct fresh operating-system processes. Other managed-entry vectors require the fresh root PID frozen by their vector. The three pre-entry failure vectors deliberately set `freshProcessPerInvocation` to false because launch, runtime-load, or permission failure can occur before a managed subject PID exists; their independent process-start and termination observations remain mandatory. No workspace, MSBuild registration, or process-global state is reused. Unrecorded retry is forbidden. A retry belongs to a new validation-execution attempt and retains the earlier attempt as immutable non-success evidence.

## Production subject contract

The production subject descriptor is a contract that Issue #24 must implement before exact-revision execution:

- It is a prebuilt test-only entry point over the real in-process production composition.
- It accepts one canonical request path and one response path through argument-list entries, never a shell command.
- Its production invocation is exactly the prebuilt entry point followed by `--request <path> --response <path>`; `argumentPrefix` is frozen empty and cannot adapt the production command.
- It invokes no second ContractScribe runtime and performs no post-hoc translation into the expected response.
- It supports protocol-owned named synchronization gates and the closed control actions used for cancellation, kill-window, and late-completion vectors.
- It emits one bounded canonical response or no managed response when managed entry/commit is impossible.
- It does not write raw stdout/stderr as a result channel.

The self-test subject is structurally separate and has `allowedForProductionEvidence: false`. Self-test observations can prove harness behavior only; they can never satisfy a production cell or aggregate schema.

`executorKind` is executable, not documentary. `production-host` invokes only the Issue #24 entry point. `external-process` and `platform-fixture` resolve their command from the bundle-locked `FrozenExecutorCommandRegistry`; the execution subject cannot define a new command. Each vector has one exact executable kind, ordered argument sequence, and ordered provisioning-input path sequence. `subject-entrypoint` is a protocol token that resolves to the same fixed Issue #24 production invocation as `production-host`; the other closed kinds are the `missing-executable` sentinel, the system-allowlisted `dotnet` launcher, or one exact `repository:` executable. Repository helper references and provisioning records use exact paths below the fixture repository and every such path must appear in the frozen arrangement sequence. Any substituted executable, added, omitted, or reordered argument, or added, omitted, or reordered provisioning input is a protocol mismatch. Repository executables additionally carry their own SHA-256. `harness-static` invokes a separate closed code registry and cannot manufacture a pass from the vector's expected value. The fixture realization must exactly match `FrozenFixtureRegistry`; it is not a second source of fixture semantics.

The named-gate mechanism uses an audit-unique system-temporary `controlRoot`. For one frozen gate name, the subject creates the zero-byte `<gate>.reached` marker only after entering the named state and while any process or restore-attempt fact relevant to a negative claim remains observable. For `observe`, the harness waits for a successful process-tree sample taken after the reached marker before creating `<gate>.release`; it creates `cancel.requested` and release for cooperative cancellation/late release, or externally terminates the subject process tree for `external-kill`. Neither side infers ordering from elapsed sleeps. A missing marker, failed post-gate sample, or missing exact control action at the 30-second protocol timeout is infrastructure-incomplete, and all control files are non-public temporary state deleted by the harness.

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

Audit and execution fields are absent when no managed terminal record can exist. The harness independently enforces the closed legality combinations across process start/termination, handled execution outcome, audit outcome, terminal state, artifact state, result presence, and control gate; for example, a handled failure is a normal termination with one committed non-success terminal, no audit outcome, no published artifact, and no canonical result. When a canonical Audit Result exists, the harness independently validates it against the pinned v1 schema and rejects an empty result set. It revalidates each complete classification against its taxonomy definition and every applicable registry constraint, including allowed support statuses, required origin and skip, skip/status compatibility, origin-specific combinations, unknown-kind equivalence, and component parent-kind compatibility. Parent compatibility joins an exact target classification from the same result set when present and otherwise uses the exact documentation-ID member form, including constructors and conversion/operator methods, rather than treating every `M:` parent as interchangeable. It also validates unique and canonically ordered results, policy contributions, evidence IDs/items, and authority declarations; policy resolution; evidence bounds and hashes; evidence-authority canonical identities; observation-subject binding; the complete protected reason legality matrix and omission rules; and required complete evidence for compliant or violating rows. It then derives the Audit Result version, policy/configuration version, taxonomy-registry version, target profile, ordered audit outcomes, and canonical commitment from those bytes. Vector observations and equality checks use those independently derived facts rather than trusting the subject response. Every handled failure binds the exact protected Issue #24 failure-registry identity and must match one exact registry row for code, normalized failing stage, execution outcome, and committed-non-success terminal mapping. This protocol does not invent Issue #24 codes.

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
| Multi-targeting | Whole-input `load-failure`; the committed handled response uses one exact Issue #24-owned `host.*` registry row while a separate bounded loader fact remains exactly `loader.unsupported.multi-targeting`; no audit outcome, canonical result, partial compilation/result, or ambient/default TFM selection |
| Missing assets | Input/environment failure; caller prepares assets |
| Restore/runtime download | ContractScribe must not initiate it |

Directory auto-discovery is not tested or claimed. Missing, invalid, and nested `global.json` follow SDK resolution rules while the selected identities are recorded.

## Frozen fixture recipes

Every non-static `(cellId, vectorId)` fixture is owned by the bundle through `FixtureRecipeRegistry`. The recipe emits an exact logical inventory and exact UTF-8 bytes for the vector descriptor, project/source inputs, entry-format files, external-process arrangement inputs, and a platform-property manifest. The latter freezes Unix execute-mode denial, Windows execute-deny ACL behavior, reparse kind and target, and same-directory or distinct-volume publication topology as applicable. `provision-fixtures` materializes the bytes and platform properties at the one frozen runtime root, reads back the permission and reparse disposition on the applicable runner, and binds reparse targets into the repository identity before requiring exact equality with the recipe-derived identity. The execution subject cannot choose a repository inventory and then attest its own matching hash. Candidate-specific files, additional files, changed bytes or platform properties, changed allowed roots, or a different recipe identity are fixture drift.

## Independent observers

The harness snapshots a closed repository inventory before subject execution and compares existence, SHA-256, and observed byte counts afterward. It records protected source/project creation, deletion, and content change; other unexpected writes; and fixture-specific allowlisted design-time output. A directory merely named `bin` or `obj` is not globally trusted. Reparse entries are recorded without traversal. Allowed-output deletion and every non-allowlisted creation, deletion, or change remain observable. Protected mutation is rejected on every ordinary vector; only the dedicated protected-write detection vector expects that mutation. Each run also receives a harness-owned audit-unique temporary-work root through the request, working directory, and `TMP`, `TEMP`, and `TMPDIR`.

The temporary-disk vector freezes peak concurrent logical file bytes over the exact temporary-work and output-staging roots named by the protected fixture. Both governed roots must be empty immediately before observer activation; a stale or otherwise nonempty prestate invalidates the arrangement. The observer tracks current lengths and the peak concurrent temporary/staging composition from activation through the gate. At the gate it creates an exact harness-owned sentinel in each root, waits for both watcher callbacks, reconciles a fresh current snapshot, and only then closes the interval. This drains earlier notifications instead of discarding callbacks that race with gate capture. The subject reaches `temporary-disk-high-water` only after every applicable allocation and publication-staging entry is materialized and before any applicable delete, truncate, replacement, rename-out, or cleanup; every entry remains stable until the harness releases the gate. Any observed delete, shrink, or rename-out is unconditionally a retention breach, including an entry removed before its create callback can sample a length, and makes that run subject-nonconforming. A watcher error, missing sentinel acknowledgement, or inaccessible reconciliation makes the observer infrastructure-incomplete; a defective checked-in observer is protocol failure. Harness sentinel files, control files, and separately classified toolchain design-time outputs are excluded. A later low or empty final snapshot cannot replace the peak evidence. Diagnostic count and UTF-8 byte measurements come from the canonical normalized diagnostic-fact envelope, never stderr, and the toolchain-subprocess measurement comes from the independently classified descendant set. An unavailable observation or subject-supplied lower value cannot satisfy a protected bound.

The harness records the root subject synchronously before polling descendants, then distinguishes additional ContractScribe workers, protected toolchain-owned processes, protected restore/runtime-download processes, and unknown descendants. Each available fixture carries at most 32 protected process-identity fingerprints. A fingerprint binds the sanitized image name, the SHA-256 of the actual executable or `dotnet` DLL command entry point, and the exact ordered command arguments; an image name or entry-point binary alone, including `dotnet` or `msbuild`, is never a trust decision. Every rule also names only the artifact provenance class and entry-point digest. Before execution the harness independently requires that digest to equal a built production artifact, a protected fixture helper, or an actually observed selected-toolchain artifact. The execution subject never supplies a semantic role. After an exact fingerprint and provenance match, a closed bundle-owned classifier gives restore/runtime-download commands precedence, recognizes `ContractScribe*` production/helper entry points as workers, recognizes only selected-toolchain compiler/MSBuild entry points as toolchain-owned, and rejects renamed or copied non-toolchain content as unknown. Paths and arguments remain inside the fingerprint preimage and are never emitted. Windows combines one Toolhelp process snapshot with the process command line; Linux reads the NUL-delimited `/proc/<pid>/cmdline`. Inspection failure is fail-closed. Claims about zero workers, no restore initiation, or other short-lived descendants require the protected synchronized-tree arrangement, the exact named gate and action, and a successful post-gate sample; bounded polling alone yields incomplete evidence. An unmatched child `dotnet` or `ContractScribe*` process is a ContractScribe worker, and every other unmatched child is unknown.

Fatal-process fixtures record the externally arranged cause (`out-of-memory`, `stack-overflow`, or `abort`) in the per-cell fixture realization. The harness never infers an OOM from an ambiguous Unix signal alone; the arrangement, materialized helper, runner policy, and observed termination are protected together. If the cell cannot establish that cause, the capability is unavailable and the vector is incomplete rather than passing under a guessed classification.

The no-ContractScribe-initiated-network-operation row uses exactly the four methods in the protected `network-evidence-profile.json`: independently derived declared dependency/operation inventory; a bounded source and managed-metadata regression scan; the Issue #24 test-only product-operation recorder; and synchronized restore/runtime-download process and artifact observation. The first method parses the exact protected project/package XML, structured configuration, command schema, environment policy, and workflow inputs with a closed evaluator version. Its package capability registry, product-owned build `Exec`/`UsingTask` rule, affirmative configuration keys, production command flags, policy statements, and workflow-owned ContractScribe flags are bundle-owned; ordinary runner provisioning such as `dotnet restore`, `setup-dotnet`, and artifact download is not reclassified as a ContractScribe product operation. Every result binds the method/version, exact protected input or closure identity, coverage-limitation identity, `complete|finding|incomplete` status, bounded observation, and non-clean cause class. Semantic evidence validation independently reruns the frozen methods from the protected source/materialization identity, recorder state, process observation, and repository delta and requires byte-exact equality with the recorded method results. The recorder path begins absent; the subject must create it with the profile's exact activation record before any zero-or-more operation entries. An absent or invalid activation record is missing subject-owned capability, not a clean empty recording. All four methods must be complete and clean for the vector to match.

The source/metadata method strips comments and literals, and it constructs the managed closure only from the exact declared built artifacts. Each non-framework assembly reference must resolve to exactly one declared artifact by managed simple name; framework references are allowed only by the trusted-platform-assembly set of the independently selected runtime. Sibling directories and probing paths are never searched to infer an undeclared dependency. The scan covers the exact direct and indirection routes enumerated by the profile, including `Assembly.GetType`, `Type.GetMethods`, `Type.GetConstructors`, delegate creation, and expression compilation. A clean result is regression evidence for that manifest and exact revision; it is not a theorem about arbitrary or adversarial .NET code. Positive findings, an omitted or malformed unchanged production dependency, and missing subject capabilities are subject nonconformance. A missing or hash-changed protected input invalidates protected input. Actual read/permission or runner/observer unavailability is infrastructure-incomplete. An otherwise current input that triggers an unrecognized checked-in scanner failure, or a defective checked-in schema, validator, oracle, or harness, is protocol failure. The existing aggregate precedence applies.

The forced-termination row uses the production subject entry point, waits for the exact `forced-termination` gate, and performs the harness-owned `external-kill`. On Windows the harness terminates descendants first, calls `TerminateProcess` on the exact root handle with frozen sentinel `0xE02600F1`, waits, and requires `GetExitCodeProcess` to equal that sentinel. On Linux the native path exclusively owns root-child status observation and reaping from process creation through raw `waitpid` capture; before capture it never calls `HasExited`, `WaitForExit*`, `ExitCode`, exit events, or another managed status consumer. Gate/race liveness uses a non-reaping native probe. It kills descendants first and the root last with `SIGKILL`, then requires `WIFSIGNALED` and `WTERMSIG == 9`. Normal `exit(137)` is an exited status and never causal kill evidence. Only the exact native cause plus a harness-issued request and no valid result derives `issued-and-observed`. Already-exited, permission-failure, unsupported, indeterminate, ambiguous, or normal-exit races cannot be relabeled as external termination.

Cancellation and publication transition rows receive a harness-selected bounded transition-recorder path. The harness preserves physical append order and requires contiguous physical sequence numbers. Each vector has one closed event vocabulary and one exact legal trace: no additional, duplicate, contradictory, reordered, or out-of-state event is accepted, and terminal rows contain exactly one terminal commit. Those traces prove invalidation before the first failure-prone stage, same-destination staging before atomic rename, the first committed terminal outcome, and rejection of competing or late terminal attempts. Final state and the response observation code alone cannot satisfy those rows. Once a handled non-success or canonical result is committed, that terminal outcome remains authoritative even if the process later crashes, aborts, or is externally killed.

These observers claim bounded detection only. The temporary-disk retain-until-release contract makes its authoritative gate snapshot the defined high-water quantity; no broader claim is made that unrelated transient writes are exhaustively captured. The observers do not prevent or roll back writes, isolate processes, enforce memory/network limits, or create an operating-system sandbox. Observer failure produces the exact non-passing class frozen above.

## Stream and public-safety boundary

Every subject process has stdout and stderr redirected and drained concurrently. Each stream is limited to 65,536 bytes. Raw stream bytes are never persisted or echoed into CI. Invalid UTF-8, overflow, unsafe markers, broken-pipe behavior, and output on both streams have closed harness classifications. Ordinary harness failures print only a stable bounded diagnostic code, never raw exception text, stack traces, environment dumps, or machine paths.

Public validation evidence is canonical JSON encoded as UTF-8 without BOM, compact, with object keys in ordinal order and exactly one trailing LF. Audit Result v1 is different: the harness parses strict UTF-8 and duplicate-free JSON without applying the generic alphabetical canonicalizer, validates schema and semantics, reserializes through its own protected and independently compiled Audit Result v1 canonicalizer with the normative property and typed array orders, and compares those exact bytes. The harness project has zero ContractScribe project references; the separately protected baseline probe remains only a test-time cross-check of canonical bytes. The per-cell limit is 4 MiB and subject-response limit is 256 KiB.

The only public execution artifacts are:

- `cell-evidence.json`
- `aggregate-evidence.json`
- `incomplete-evidence.json`
- `publication-record.json`

The same credential-marker, machine-path, raw-log, and bounded-field scans apply to successful and non-success artifacts and to captured CI-visible harness output.

Machine-path rejection covers drive-rooted paths, UNC paths, and Unix runner/temp/toolchain roots while permitting HTTPS identities and repository-relative paths. Credential markers include bearer authorization headers. Public network claims are a structured, indivisible claim set rather than caller-supplied prose. Evidence carries only the composite claim-set identity, and a renderer derives the three exact statements below.

The scanner rejects other affirmative connectivity, disconnection, isolation, prevention, air-gap, offline-enforcement, sandbox, capability-security, or repository-build secret-isolation claims.

### Frozen public network claim set

Claim set: `m1.network-behavior-and-bounded-evidence-claims.v1`

1. `m1.no-declared-network-operation.no-contractscribe-initiation.v1`: The deterministic audit declares no network-dependent operation, and ContractScribe initiates no provider, GitHub, update, telemetry, restore, runtime-download, or other declared network-dependent operation.
2. `m1.exact-revision-bounded-network-conformance.v1`: The recorded evidence is bounded to one exact reviewed production revision and the frozen checks.
3. `m1.no-egress-sandbox-or-adversarial-completeness.v1`: The recorded evidence does not establish egress isolation, sandboxing, capability security, secret isolation from repository-controlled build logic, whole-program non-reachability, or the absence of every possible network path in arbitrary or adversarial .NET code. Repository-controlled MSBuild targets, analyzers, generators, and SDK logic execute as trusted input with caller privileges and are outside this ContractScribe behavior claim.

## Evidence acceptance

Before subject execution, the harness validates:

- strict JSON without comments, trailing commas, duplicate properties, BOM, invalid UTF-8, unknown versions, or unknown schema fields;
- the complete oracle artifact lock and protected-input manifest;
- protocol, vector, subject, evidence, review, and aggregate schemas;
- bidirectional requirements-to-vector/validator coverage;
- project/package/reference boundaries;
- review binding when production evidence is being accepted;
- the closed cross-cell source/configuration identity, exact execution-subject manifest content identity, and current per-cell materialization.

Per-cell evidence is accepted only when its exact run triples equal the expanded oracle set for that cell. Validation re-derives subject/process legality, failure-registry binding, observation, enforcement class, vector verdict, and cell outcome from independently observed process, repository, and canonical-result facts. Missing, duplicate, unexpected, substituted, contradictory, or wrong-cell runs fail closed. Equality uses a closed field registry; canonical result commitments are recomputed from the exact published canonical bytes, fresh-process rows require distinct observed root process IDs, and designated equality is enforced within and across cells.

Aggregation accepts the exact referenced cell files, validates them, recomputes their hashes, enforces one bundle/review/source/subject/attempt identity, evaluates cross-cell equality, and derives the aggregate outcome. A standalone aggregate assertion cannot be accepted or published. `validate-publication-record` additionally proves that the exact accepted cell and aggregate bytes are present in the later evidence-record Git revision; this closes publication without asking the aggregate to predict the commit that will contain it.

All command inputs and protected bundle/source paths are validated before stale-output invalidation. Output files must be disjoint from every input and protected artifact, must not overlap input directories or traverse links, and, when repository-local, must be under `TestResults/m1-host-validation/`. Every existing ancestor up to the filesystem root is checked for symbolic-link or reparse aliases, so an external alias cannot redirect an output into the repository.

Failed, cancelled, timed-out, invalidated, or infrastructure-incomplete attempts produce immutable incomplete evidence when the harness can safely do so. A later success does not erase them. `supersedes` accepts only canonical incomplete-evidence v1 records with the same bundle, review, source configuration, exact host revision, workflow path, workflow content identity, immutable flag, closed classification, and an earlier distinct workflow run or attempt. The host revision must also be an ancestor of the candidate validation-execution revision. Arbitrary JSON, a different host or workflow lineage, duplicate/self/current/future attempts, successful cell evidence, and aggregate evidence cannot enter the supersession set. When live materialization drifts during failure handling, incomplete evidence retains the commit-bound source and reviewed bundle identities while recording the incomplete classification instead of suppressing the record.

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
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- provision-fixtures --root . --subject-manifest <execution-subject>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- self-test --root .
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- run-cell --root . --subject-manifest <execution-subject> --review <review> --cell <cell> --output <cell-evidence> --incomplete-output <incomplete-evidence>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- validate-cell --root . --review <review> --subject-manifest <execution-subject> --evidence <cell-evidence>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- validate-incomplete --root . --review <review> --subject-manifest <execution-subject> --evidence <incomplete-evidence>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- aggregate --root . --review <review> --subject-manifest <execution-subject> --evidence <ubuntu>;<windows> --matrix-result <passed|failed|incomplete> --publication-base-revision <validation-execution-commit> --output <aggregate>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- validate-aggregate --root . --review <review> --subject-manifest <execution-subject> --evidence <aggregate> --cell-evidence <ubuntu>;<windows>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- validate-publication-record --root . --review <review> --subject-manifest <execution-subject> --record <publication-record> --aggregate-evidence <aggregate> --cell-evidence <ubuntu>;<windows>
dotnet run --project tests/ContractScribe.HostValidation/ContractScribe.HostValidation.csproj --configuration Release -- prepare-public --root . --review <review> --subject-manifest <execution-subject> --kind <cell|aggregate|incomplete|publication-record> --source <evidence> --aggregate-evidence <aggregate-when-publication-record> --cell-evidence <ubuntu>;<windows> --output <allowlisted-name>
```

`lock-protected-inputs` and `lock-bundle` are maintainer commands. Any regenerated identity requires a new exact review. `provision-fixtures` writes only the frozen deterministic runtime fixture inventory and rejects any remaining extra or changed file through the recipe identity. The CI `validate-bundle`, `dry-run`, and `self-test` executions validate the oracle and harness on both operating systems; they are not production-host pass evidence.

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
- treat the 25-row mutation corpus as documentary metadata instead of executing every frozen mutation and expected diagnostic;
- emit or preserve raw streams, machine paths, credentials, logs, or exception text;
- describe trusted MSBuild as sandboxed or normal CI as network-isolated.

If the host contradicts a frozen contract, fix the owning implementation. If authoritative contracts genuinely change, regenerate and independently review a new bundle rather than weakening the old oracle.
