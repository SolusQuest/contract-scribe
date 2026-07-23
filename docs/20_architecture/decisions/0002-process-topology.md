# ADR 0002: Production process topology

Status: Proposed

Date: 2026-07-23

Decision owner: Repository owner; this ADR becomes a repository decision through human-reviewed PR merge.

## Context

ADR 0001 selected and validated the `framework-dependent semantic execution baseline` for M0 and was promoted to M0-validated status after the M0.7 independent validation succeeded. ADR 0001 explicitly did not select a production process topology: it recorded the M0.4 in-process boundary as a test-host observation only and deferred the loader child-process option as not evidenced, stating that selecting it would require defined ownership, transport/versioning, cancellation, exit and diagnostic propagation, cleanup, and public-safety behavior. Issue #17 owns that deferred decision.

The evidence boundary for this ADR is exact: M0.4 and M0.7 directly evidence only the in-process SDK/MSBuild discovery, solution-load, and semantic-projection segment, executed by a test-only host on the required Ubuntu and Windows X64 cells. They do not evidence production M0.1 policy evaluation, XML-documentation detection, production M0.3 classification, evidence generation, M0.2 audit-result aggregation, or production cancellation, failure, diagnostics, and file-write behavior.

This ADR decides the production process topology for the M1 deterministic-audit boundary, or defers it with a concrete revalidation gate. It does not implement anything, select a distribution channel (#18 owns that), reopen Native AOT, or modify the M0.1–M0.3 contracts.

## Fixed constraints

The following are inputs to this decision, fixed by existing contracts and validated boundaries. They are not candidate-specific trade-offs.

- Production audit inputs and outputs are defined by the versioned M0.1 policy/configuration and M0.2 audit-result contracts. The experimental `semantic-payload.json` remains a test-only comparison artifact and never becomes a production IPC, CLI, or backward-compatibility contract.
- The deterministic audit path declares no network-dependent operation and requires no provider/model secret or GitHub write token. No topology may introduce one.
- Restore/build preparation is the caller's responsibility, as in the M0.7 protocol. Missing restore assets are input/environment errors, never an automatic restore trigger, because an unexpected restore could violate the no-declared-network-dependency boundary.
- The M0-validated execution baseline is framework-dependent (`global.json` SDK `10.0.102` with `latestFeature` roll-forward and the current package baseline). Runtime/publication-profile alternatives, such as a Native AOT semantic core, are an independent dimension already deferred by ADR 0001 and are not topology candidates here.
- The M0.4/M0.5/M0.7 experiment hosts remain test-only. This ADR does not promote them or their failure registries into production code or contracts.
- Diagnostics and evidence are bounded and public-safe: no raw logs, absolute machine paths, credentials, environment dumps, or unbounded toolchain output in public artifacts.
- A child process, if ever selected, is fault isolation, not a security sandbox. No topology may claim containment of untrusted MSBuild content.
- Internal process boundaries are not external compatibility commitments before the release and license gate, per ADR 0001.

## Fixed M1 eligibility requirements

These requirements are fixed before candidate comparison. Each is classified as a hard gate (a candidate that fails it is ineligible) or a trade-off criterion, and justified from a committed document or labeled as an explicit stated assumption.

| ID | Requirement | Classification | Justification |
| --- | --- | --- | --- |
| R1 | Analyzed repositories are trusted input. No candidate may claim safe processing of untrusted MSBuild content. | Hard gate (conformance, not differentiation) | Stated assumption grounded in the security boundary (synthetic fixtures only, no sandbox claims) and in MSBuild semantics: loading a solution evaluates repository-controlled build logic. All candidates must conform, so it does not differentiate them. |
| R2 | The host must be able to terminate hung or runaway semantic work within bounded time. | Trade-off criterion | The evidenced path supports cooperative cancellation through `CancellationToken`. Under R1 (trusted input) and the M1 CLI shape, an unrecoverable hang is bounded by the user or OS killing the process. Demanding forced termination as a gate would eliminate the only evidenced candidate in favor of unevidenced ones, contradicting the evidence-first policy of ADR 0001. |
| R3 | One host lifetime must support multiple SDK selections. | Not required at M1 (stated assumption) | M1 audits run one repository per invocation, and each invocation is a fresh process. The evidenced MSBuildLocator registration is process-level and one-shot, which is compatible with per-invocation processes. A future multi-repository or long-lived host requirement reopens this; see decision reconsideration triggers. |
| R4 | A single audit's failure must not take down unrelated concurrent work. | Not required at M1 (stated assumption) | The M1 shape is a single-audit CLI invocation; process exit on fatal audit failure is acceptable CLI behavior. A future daemon or multi-tenant host reopens this; see decision reconsideration triggers. |
| R5 | Crash/fault containment of semantic execution is required. | Trade-off criterion | Under R1 and the M1 shape, process-level containment is defense-in-depth, not a correctness requirement. Candidates with a process boundary score higher on this criterion. |
| R6 | Concurrent audits in one host. | Not required at M1 (stated assumption) | The M1 shape is one audit per invocation. Repository-level concurrency is provided by running separate processes. |
| R7 | Startup/latency cost. | Trade-off criterion | A CLI audit should not add avoidable fixed overhead, but no latency budget is a gate at M1. |

## Decision dimensions and terminology

This ADR decides along one axis only — the process topology axis — and keeps the runtime/publication profile as a separate, already-deferred dimension.

- Process topology: whether SDK/MSBuild discovery, solution loading, and semantic execution share the user-facing process or cross a child-process boundary.
- Loader worker: a child process whose responsibility ends after bounded evidence generation (SDK/MSBuild discovery, solution loading, compilation, M0.3 classification, XML-documentation observation, evidence excerpt and hashing). Split points further downstream — a worker that also performs M0.1 policy evaluation or produces audit results, leaving the parent a pure orchestrator — are outside the candidate set.
- Ephemeral worker: spawned per audit and exits after it.
- Persistent worker: reused across multiple audits within a bounded host-session lifetime. System daemons or services with a lifetime beyond the invoking host session are outside the candidate set.
- Runtime/publication profile: framework-dependent vs Native AOT or split-runtime profiles. Deferred by ADR 0001; not decided here.

The disposition vocabulary is the closed ADR 0001 vocabulary: selected, rejected, deferred, not evidenced, not feasible under the tested profile, outside current scope. Live dispositions are selected and deferred; a deferred candidate remains live and re-enters through its stated revalidation gate. Rejected, not-feasible, and outside-scope dispositions are non-live and re-enter only through their stated re-entry conditions.

## Evidence classification

The ADR 0001 classifications apply: verified fact, decision inference, deferred risk, unknown.

The evidence inputs are:

| Input | What it establishes | What it does not establish |
| --- | --- | --- |
| M0.4 experiment record and transfer manifest | In-process SDK/MSBuild discovery, solution load, and deterministic semantic projection for the named synthetic fixture, on the current SDK/package baseline and required Ubuntu/Windows CI protocol, in a test host. | Production loader behavior, production audit-stage behavior (policy evaluation, audit-result aggregation), arbitrary project compatibility, any child-process behavior, or a distribution channel. |
| M0.7 independent validation (post-merge main run [30004931948](https://github.com/SolusQuest/contract-scribe/actions/runs/30004931948)) | The same in-process segment reproduces byte-identical canonical output against an independently authored fixture and oracle on both required cells. | Production topology behavior, production audit stages, child-process behavior, or arbitrary repository compatibility. |
| ADR 0001 and architecture/security/roadmap rules | The validated execution baseline, the deterministic/no-declared-network-dependency boundary, and the deferred status of child-process and split-runtime options. | A production process topology (that is this ADR's decision). |

No committed evidence exercises a child-process or split-runtime topology. Every statement below about C2/C3 runtime behavior is therefore an assumption or open question, never a verified fact.

## Candidates

- **C1 — In-process production loader.** One OS process (the future audit host/CLI) owns SDK/MSBuild discovery, solution loading, semantic extraction, policy evaluation, and audit-result production.
- **C2 — Ephemeral child-process loader worker.** The host spawns one worker per audit. The split point is fixed: the worker owns SDK/MSBuild discovery, solution loading, compilation, M0.3 classification, XML-documentation observation, and bounded evidence excerpt/hash generation, and returns a bounded serialized semantic-facts payload over a bounded, versioned IPC contract, then exits. The parent owns M0.1 policy evaluation, M0.2 audit-result aggregation and canonical serialization, final file writes, and user-facing diagnostics.
- **C3 — Persistent child-process loader worker.** Same fixed split point as C2, but the worker is reused across multiple audits within a bounded host-session lifetime.

Outside the candidate set: split points downstream of the fixed one (a worker that also performs M0.1 policy evaluation or produces audit results); system daemons or services; runtime/publication-profile variants. A materially distinct candidate requires an explicit #17 issue-body amendment and a new independent review before this ADR is revised.

## Comparison

All three candidates conform to R1. Under the fixed requirements above, no candidate fails a hard gate: R2 and R5 are trade-off criteria, and R3, R4, and R6 are not M1 requirements.

| Criterion | C1 in-process | C2 ephemeral worker | C3 persistent worker |
| --- | --- | --- | --- |
| Evidence strength | Direct for the loading/semantic segment (M0.4/M0.7); decision inference for the full audit host | None (not evidenced) | None (not evidenced) |
| Semantic-path fidelity | The evidenced path is preserved unchanged | Requires a serialization boundary that must reproduce the canonical semantics; unproven | Same as C2, plus reuse-state risks |
| Deterministic canonical output | Evidenced byte-identical behavior for the segment | Transport and framing add nondeterminism risks; unproven | Same as C2, plus cross-request state risks |
| SDK/MSBuild compatibility | Same as evidenced | Same per worker; orchestration unproven | Multi-SDK reuse conflicts with one-shot process-level MSBuildLocator registration unless one worker per SDK |
| Forced termination (R2) | Cooperative cancellation only; external kill as bound | Host may kill the worker process tree | Same as C2 |
| Fault containment (R5) | None beyond the OS process itself | Worker crash does not take down the host | Same as C2 |
| No-network/no-secret conformance | Equal (fixed constraint) | Equal | Equal |
| Operational complexity | Lowest: one process, no protocol | Worker lifecycle, IPC contract, version negotiation, orphan handling | Highest: adds reuse, cache invalidation, and state isolation to C2 |
| Failure diagnosability | Single-process diagnostics | Adds protocol and process-boundary failure modes | Same as C2, plus stale-state modes |
| Process-boundary cost (R7) | None | Spawn + IPC per audit | Amortized spawn, unchanged IPC |
| Distribution mechanics (for #18) | One binary; framework-dependent runtime | Two binaries or one binary with a worker mode; co-location and atomic-update questions | Same as C2 |
| M1 implementation implications | M1 can plan and implement now against the evidenced segment | Blocks M1 planning on a prototype experiment and an IPC contract | Blocks M1 planning on the same, plus reuse semantics |

## Decision

**Select C1 — in-process production loader — as the production process topology for the M1 deterministic-audit boundary.** This is a decision inference: it extends the directly evidenced in-process loading/semantic-projection segment to the full audit host while preserving the observed boundary. It is not executable evidence for the complete production audit topology; the production implementation (#24) and its executable validation (#26) are mandatory follow-ups (see Follow-up issues).

C1 wins on evidence strength, semantic-path fidelity, determinism, operational complexity, failure diagnosability, distribution simplicity, and M1 readiness. Its documented limitations follow from the fixed requirements: cooperative cancellation only (R2 is a trade-off), no fault containment beyond the process (R5 is a trade-off), one repository and one SDK selection per process lifetime (R3/R4/R6 assumptions). Each limitation has a corresponding decision reconsideration trigger.

**C2 and C3 are deferred (live).** They remain the only candidates that provide forced termination and fault containment, but they are not evidenced, and selecting them now would contradict the evidence sufficiency standard. Their eligibility path is the test-only prototype experiment (#27), which is also the revalidation gate. C2/C3 may not be marked selected while that experiment's open questions — including the serialized semantic-facts payload contract (§5) — remain unanswered.

**Runtime/publication profile** remains deferred under ADR 0001 and is unaffected by this decision: C1 ships as a framework-dependent host.

## Contract of the selected topology

Status legend: each **sub-decision row** below carries a status — **decided**, **not applicable**, or **deferred to** a linked follow-up issue. For the selected topology, every sub-decision that #17 owns and that determines topology eligibility or the process boundary is decided here; sub-decisions owned by other issues or requiring executable calibration are deferred with their required evidence stated, and they never change the selected topology. No unvalidated constant is invented to make a row look decided.

### 1. Responsibility and ownership matrix — decided

One process (the audit host) owns every stage, in order: repository-root determination; solution/project path resolution; policy/configuration read and validation; SDK/global.json resolution; restore/build prerequisite checking; MSBuildLocator registration; MSBuildWorkspace/Compilation creation; M0.3 classification; XML-documentation detection; evidence excerpt and hashing; M0.1 policy evaluation; M0.2 audit-result aggregation and canonical serialization; final file writes; diagnostic filtering and public-safety enforcement; cancellation, timeout, and cleanup. There is no inter-process ownership. For deferred C2/C3 the side assignment is fixed by the candidate definitions (worker: SDK/MSBuild discovery, solution loading, compilation, M0.3 classification, XML-documentation observation, evidence excerpt/hash; parent: M0.1 policy evaluation, M0.2 audit-result aggregation/serialization, final writes, user-facing diagnostics); no stage may move across the fixed split point. Stage-level error mapping is defined by the production implementation contract (#24).

### 2. Lifetime and concurrency — decided

One process lifetime handles exactly one audit of one repository with one SDK selection. The process exits after the terminal outcome is committed. There is no worker, no fallback between topologies, and no mixed topology. Concurrent audits are separate OS processes; the host must not rely on shared mutable state across processes.

### 3. Trust model and isolation — decided

Analyzed repositories are trusted input (R1). The host provides no sandbox and claims none. Environment inheritance is the OS default for a CLI process; the host must not read, persist, or emit credential-like environment values, and diagnostics remain bounded per §8. The repository is treated as read-only by the audit itself; build outputs exist only because the caller prepared them, and temporary files go to the system temporary directory with audit-unique names and deletion on exit. Paths are constrained lexically to the repository root; symlink, junction, or reparse-point escapes outside the root are rejected as invalid input. Worker executable location/integrity, shell-spawn rules, and process-tree termination are not applicable to C1.

### 4. Transport and versioning — not applicable to C1

C1 has no IPC boundary. For deferred C2/C3 this dimension is an open question set for the eligibility experiment (#27), consistent with the fixed split point: transport; framing; encoding; a request contract limited to protocol version, request ID, repository root, solution path, and timeout/cancellation metadata (no policy reference — the parent owns policy evaluation); a response contract of execution status, the serialized semantic-facts payload (§5), bounded diagnostics, toolchain identity, and protocol identity (no audit-result bytes — the parent owns audit-result production); version negotiation; version-skew policy; unknown-field handling; size limits; stdout/stderr ownership; atomic result semantics. Addressing distinguishes private runtime addressing from public/output data: the request envelope may carry execution-necessary paths (a normalized absolute repository root plus a repository-relative or validated absolute solution path) provided the parent lexically validates them before sending, the solution path resolves under the repository root, symlink/junction/reparse-point and root-escape behavior is defined, and the worker never depends on its current working directory; these paths serve only the current local execution and are neither canonical contract data nor an external compatibility promise. Public/output data remains path-free: absolute machine paths are forbidden in the semantic-facts payload, response diagnostics, the audit result, user-facing stdout/stderr, logs, evidence, and public artifacts, and the worker must convert paths to repository-relative locators or stable bounded codes without echoing input absolute paths. Data forbidden from crossing the boundary in either direction: Roslyn object graphs, raw exceptions, environment dumps, and `semantic-payload.json`. No final values are fixed here.

### 5. Serialized semantic-facts payload — not applicable to C1; open questions for the C2/C3 eligibility experiment

The payload the worker returns at the fixed split point is a distinct contract element, neither the experimental `semantic-payload.json` nor the M0.2 audit-result contract. The eligibility experiment (#27) must define: its semantic ownership (worker-produced, parent-consumed); schema and version; identity and ordering rules; size bounds; canonicalization; sufficiency for the parent's stages — because M0.3 classification, XML-documentation observation, and evidence excerpt/hash happen on the worker side, the payload must carry everything M0.1 policy evaluation and M0.2 audit-result production require; and breaking-change and compatibility rules. Elements only a prototype can determine (concrete schema, framing, bounds) remain open questions; C2/C3 may not be selected while they are open.

### 6. Failure taxonomy and outcome semantics — classes and mapping decided; CLI numeric codes deferred

Three outcome layers:

- Audit outcome: `compliant`, `violation`, `skipped` (per the M0.2 audit-result contract). An audit violation is a successful execution carrying a violation audit outcome.
- Execution outcome: invalid input, environment unavailable, load failure, audit error, cancelled, timeout.
- Process outcome: the host process itself failed before committing a terminal outcome (crash, abort, external kill). An abruptly terminated in-process host cannot classify itself; observers classify the absence of a committed terminal outcome as process failure.

Abstract terminal outcome classes and precedence: exactly one terminal outcome per run, committed atomically (§9). When causes coincide, precedence is: process outcome (externally observed) over execution outcome, and within execution outcomes environment/input classification precedes load/audit classification when both are detected; an audit outcome is produced only by a successful execution. The mapping from these classes to concrete CLI numeric exit codes is **deferred to #25**; it requires no topology decision and must not change this one. The production implementation (#24) defines a closed failure-code registry with explicit versioning; experiment failure registries are not reused.

### 7. Cancellation and timeout — semantics decided; concrete timeout values deferred

Cancellation follows a single-terminal-outcome state machine with an atomic result-commit point: cancellation accepted before the commit point yields the `cancelled` outcome; once the terminal result is atomically committed, the committed outcome stands. Every run produces exactly one terminal outcome; stale or late results are impossible within one process and discarded by construction. Cancellation in-process is cooperative (`CancellationToken`); stages must observe cancellation at stage boundaries. Timeout classes: SDK discovery, load, and total audit, each mapping to the corresponding execution outcome; graceful shutdown is bounded by the same commit rule. Concrete timeout values are **deferred to #24**; they require runtime calibration and must not change this topology. Forced termination of a hung stage is external only (user or OS kill) — this is the documented R2 limitation and carries a decision reconsideration trigger. Retries are whole-process reruns; a rerun of the same request is idempotent with respect to outputs because results are committed atomically and stale artifacts are removed (§9). Orphan workers, worker hangs, and response/exit-code conflicts are not applicable to C1.

### 8. Diagnostics and public safety — decided; numeric caps deferred

Diagnostics are allowlisted: stable codes, bounded messages, repository-relative paths (absolute machine paths forbidden), no source excerpts unless a future contract explicitly allowlists them, deterministic ordering and deduplication. Raw MSBuild/Roslyn diagnostics are classified, truncated, and sanitized before surfacing. Stack traces and machine-local detail are local-debug only and never enter public output; debug mode must not change the public output contract. Logs are not written to disk by default. Telemetry is a non-goal. Concrete maximum diagnostic counts and byte caps are **deferred to #24** as calibration values.

### 9. Determinism and state management — decided

The canonical audit result contains no process metadata (PID, timestamps, durations, temporary directories, machine identity); any execution envelope is separate and non-canonical. Culture, timezone, locale, and encoding must not affect output; ordering is ordinal and environment-independent, per the M0.2 contract. The host reads a load-time snapshot of the repository and does not watch for changes; a repository modified during an audit may yield a rerun decision by the caller (TOCTOU is accepted and documented for trusted input). Result commitment is atomic: write to a uniquely named temporary file, then rename; stale artifacts from earlier runs are deleted on success. MSBuildLocator registration is process-level and one-shot, consistent with the per-invocation process model. In-process caches are allowed within one audit only. OOM, stack overflow, and abort are process outcomes classified externally, since the terminated process cannot classify itself; concrete memory/runtime resource limits are **deferred to #24**. Working-directory independence: the host must not depend on the caller's current directory for correctness.

### 10. SDK/MSBuild input scope — boundaries decided; validation matrix deferred

The first version accepts an explicit path to a `.sln`, `.slnx`, or `.csproj`; directory auto-discovery is not performed. The CLI accepts absolute or repository-relative paths; diagnostics emit repository-relative paths only. Restore/build preparation is the caller's responsibility (fixed constraint); missing assets are input/environment errors. SDK selection follows `global.json` with `latestFeature` roll-forward; the actually selected SDK/MSBuild identity is recorded in the bounded execution envelope. Missing, invalid, or nested `global.json` follows .NET SDK resolution rules, with the selected identity recorded. The Ubuntu/Windows X64 matrix is the M0.7 evidence boundary, not a production support claim; the required implementation-validation matrix is **deferred to #26**, and no production support claim exists until it passes. Unsupported project features (analyzers, generators, multi-targeting, non-C# languages, custom targets, solution filters) are classified as expected-to-work-but-unvalidated unless the implementation contract states otherwise; M0 evidence covers only the tested synthetic shape.

### 11. Migration and compatibility — decided

C1 has no parent/worker versioning. The audit's external behavior binds to the M0.1–M0.3 contract versions only. No migration path exists from `semantic-payload.json` to any production contract. Internal process boundaries are not external compatibility commitments before the release and license gate, per ADR 0001. Version-skew, rollback, and stale-worker questions are deferred with C2/C3.

### 12. Revalidation triggers — decided

Evidence revalidation triggers (rerun validation; the topology decision stands): SDK, Roslyn, MSBuild, package, or runner-image drift; M0.1–M0.3 contract changes; evidence-contract changes; validation-matrix changes.

Decision reconsideration triggers (reopen the topology choice): a new requirement to isolate untrusted MSBuild content; forced termination becoming a hard requirement; a multi-repository or multi-SDK single-host-lifetime requirement (changes R3/R4/R6); a fault-containment requirement change (R5); M1 implementation evidence that an in-process limitation assumed above does not hold; or C2/C3 becoming eligible through the prototype experiment (#27).

### 13. Distribution constraints for #18 — decided (constraints only)

C1 imposes: a single-binary, framework-dependent host; no worker co-location, discovery, or atomic-update requirement; no RID-specific artifacts (framework-dependent deployment); no executable-permission requirements beyond a normal CLI; no version handshake. The channel decision itself remains with #18.

## Deferred candidates: assumptions and open questions

For C2/C3 (deferred, live), the prototype experiment (#27) must answer, at minimum: the serialized semantic-facts payload contract (§5); transport and framing; request/response contracts with forbidden boundary data; private runtime addressing versus path-free public outputs (§4); version negotiation and skew policy; cancellation propagation and process-tree termination on Ubuntu and Windows; orphan and parent-death behavior; deterministic output across the boundary; diagnostics bounding across the boundary; worker executable location and integrity; and for C3 additionally reuse-state isolation, cache invalidation, and one-worker-per-SDK behavior. The minimal experiment envelope is a test-only host pair that loads the M0.4 fixture shape through a bounded IPC boundary and compares canonical output, without promoting anything to production. No final contract values are fixed by this ADR.

## Consequences and risks

Positive: M1 can plan and implement against an evidenced boundary now; no speculative protocol is frozen; the child-process path remains live with an explicit eligibility gate; distribution constraints handed to #18 are minimal.

Costs and residual risks: C1 carries documented limitations on forced termination and fault containment; if M1 implementation evidence contradicts the R3/R4/R6 assumptions, this decision must be reconsidered; the decision inference beyond the evidenced segment must be validated by the executable validation follow-up (#26) before any production support claim.

## Follow-up issues

- [#24 — Implement the in-process production audit host](https://github.com/SolusQuest/contract-scribe/issues/24) (M1): implements the selected in-process topology and the production contracts, and owns the deferred calibration values (concrete timeout values, diagnostic caps, resource limits) and the closed failure-code registry.
- [#25 — Define the M1 CLI surface](https://github.com/SolusQuest/contract-scribe/issues/25) (M1): owns concrete CLI numeric exit codes within the abstract outcome classes decided here.
- [#26 — Validate the production in-process topology executably](https://github.com/SolusQuest/contract-scribe/issues/26) (M1): validates the implemented host on the required matrix, including determinism, cancellation, failure-taxonomy, and public-safety checks; defines the implementation-validation matrix.
- [#27 — Test-only child-process loader worker prototype experiment](https://github.com/SolusQuest/contract-scribe/issues/27) (kept live; scheduled after M1 or when a decision reconsideration trigger fires): establishes eligibility evidence for C2/C3, including the serialized semantic-facts payload contract.

## Public-safety and compatibility boundary

This ADR contains only public repository artifacts and sanitized records. It adds no private downstream identifiers, machine-local paths, raw logs, credentials, environment dumps, prompts, or unbounded toolchain output. Links to documentation, source, and evidence are pinned to full `main` commit SHAs or immutable CI runs; issue links are live tracking references by design.

## References

- [ADR 0001: Loader and distribution boundary](https://github.com/SolusQuest/contract-scribe/blob/f4f2f9de219f18c514d54d79f3ef343fff89fd21/docs/20_architecture/decisions/0001-loader-and-distribution-boundary.md)
- [M0.7 post-merge main validation run 30004931948](https://github.com/SolusQuest/contract-scribe/actions/runs/30004931948)
- [M0.4 experiment record](https://github.com/SolusQuest/contract-scribe/blob/92dc5fbc9c432ff410e48eed1ea6e79226838d8b/docs/20_architecture/experiments/m0.4-framework-dependent-loading.md)
- [Architecture](https://github.com/SolusQuest/contract-scribe/blob/f4f2f9de219f18c514d54d79f3ef343fff89fd21/docs/20_architecture/architecture.md)
- [Security boundary](https://github.com/SolusQuest/contract-scribe/blob/f4f2f9de219f18c514d54d79f3ef343fff89fd21/docs/20_architecture/security-boundary.md)
- [Roadmap](https://github.com/SolusQuest/contract-scribe/blob/f4f2f9de219f18c514d54d79f3ef343fff89fd21/docs/90_roadmap/roadmap.md)
- [Issue #17](https://github.com/SolusQuest/contract-scribe/issues/17)
- [Issue #18](https://github.com/SolusQuest/contract-scribe/issues/18)
