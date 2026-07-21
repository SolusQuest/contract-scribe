# ADR 0001: Loader and distribution boundary

Status: Provisional candidate baseline pending M0.7

Date: 2026-07-21

Decision owner: Repository owner; this ADR becomes a repository decision through human-reviewed PR merge.

## Context

ContractScribe's deterministic audit must remain separate from later proposal and platform-adapter concerns. The deterministic path has no network dependency, provider/model secret, or GitHub write token. Bootstrap intentionally contains only `ContractScribe.Core` and `ContractScribe.Cli`; it does not establish a production Roslyn loader, semantic runtime, or distribution channel.

M0.4 validated one bounded framework-dependent Roslyn/MSBuild semantic path on the public `roslyn-msbuild-v1` synthetic solution. The fixture contains `SampleApp` and `SampleLibrary`, with a handwritten expected-symbol oracle. The framework-dependent host discovers the SDK/MSBuild environment, loads the solution, projects a deterministic symbol set, and writes the standalone canonical `semantic-payload.json` comparison artifact. Two fresh local Windows runs produced identical payload bytes with SHA-256 `e5d3bc87a0448da6e953956aae897b33738a16efbedfeeefe1366fc5b8afbd29`.

M0.5 reused the frozen M0.4 runner, fixture, identity, ordering, and canonical payload comparison under `net10.0`, Release, `PublishAot=true`, `SelfContained=true`, `PublishTrimmed=true` on `linux-x64` and `win-x64`. Both closure cells reached a reproducible publish-time `publish.trimming-analysis-failed` result with cause `trimming-reflection`, producing the aggregate outcome `not-feasible`. Native execution and semantic payload comparison were not reached. This is evidence that the frozen path and exact tested Native AOT profile were not feasible on the required cells; it is not a claim that every Native AOT design or configuration is impossible.

This ADR records the next bounded execution baseline from those facts. It does not promote the experimental host or payload into a production API, and it does not select a package or release channel.

## Decision drivers

The baseline must:

- preserve the M0.4 semantic path, `(logicalProjectId, documentationCommentId)` identity, ordering, and deterministic comparison boundary;
- keep the audit path offline and independent of provider/model secrets and GitHub write tokens;
- be directly exercised by the committed M0 evidence rather than inferred from an untested runtime split;
- make SDK/MSBuild prerequisites, tested matrix, failure behavior, and evidence limits explicit;
- keep public source safe and avoid machine-local paths, raw logs, environment dumps, credentials, and private downstream material;
- provide a clear, bounded input to M0.7 and M1 without freezing an external compatibility or publication promise.

Evidence validity, semantic-path fidelity, deterministic output, offline operation, and public safety are eligibility gates. Operational complexity, installation prerequisites, process isolation, and packaging convenience are trade-off criteria only after an option is eligible.

## Decision dimensions and terminology

The candidate list contains different architectural layers. This ADR treats them separately:

- Execution baseline: the runtime/deployment model that executes the semantic path.
- Loader: the responsibility for resolving the SDK/MSBuild environment and opening a solution.
- Semantic runner: the frozen M0.4 test-only implementation that loads the solution and produces the canonical comparison artifact.
- Semantic core: the semantic analysis and canonicalization responsibility inside the runner. M0.6 does not define or select a separately deployable semantic-core component.
- Host: the process or test host that owns runner startup, SDK/MSBuild discovery, input selection, and result propagation. The M0.4 host is test-only.
- CLI: a user-facing command-line product surface. M0.6 does not create or select one.
- Process topology: whether loading and semantic execution share a process or cross a child-process boundary. M0.6 records the observed M0.4 test-host topology but does not select a production topology.
- Child process: a separately spawned process boundary used for loading or semantic execution. It is a topology option, not a distribution channel or an evidenced M0 baseline.
- Distribution baseline: the validated runtime and packaging assumptions that a future consumer-facing channel would carry. M0.6 records no distribution baseline.
- Distribution channel: how a future consumer obtains and updates a product. M0.6 does not select one.
- Framework-dependent: this M0 baseline requires the compatible repository SDK/runtime and MSBuild environment used by the M0.4 path; it is not a self-contained or Native AOT publication claim.
- Native AOT: the M0.5 tested publication profile and its bounded result, not an umbrella claim about future remediated designs.

The selected candidate name is `framework-dependent semantic execution baseline`. It describes the directly evidenced layer and must not be read as a verified claim about future production `ContractScribe.Cli` behavior.

The disposition vocabulary is closed for this ADR:

- Selected: the one provisional candidate chosen for the next validation gate from direct positive evidence.
- Rejected: evidence rules out the candidate for the stated profile or the candidate violates a decision boundary.
- Deferred: the candidate remains plausible but is intentionally left for a later issue or gate.
- Not evidenced: the committed experiments do not exercise the candidate sufficiently to make it eligible.
- Not feasible under the tested profile: the exact tested profile reaches a reproducible negative; this does not generalize beyond that profile.
- Outside current scope: the question is intentionally not decided by M0.6 and must not be inferred from this ADR.

Every candidate is compared using the same criteria: evidence strength, semantic-path fidelity, deterministic canonical output, SDK/MSBuild compatibility, offline and no-secret behavior, prerequisites, supported matrix, operational complexity, failure diagnosability, process-boundary cost, distribution mechanics, and M1 implementation implications. These criteria guide the decision without turning untested convenience or packaging preferences into evidence.

## Evidence classification

The following classifications apply throughout this ADR:

- Verified fact: directly demonstrated by a committed experiment record, manifest, evidence record, or repository rule.
- Decision inference: a bounded choice derived from verified facts and explicitly labeled as inference.
- Deferred risk: a material concern that does not invalidate this provisional selection but requires a later issue, experiment, or release gate.
- Unknown: not tested and not inferable from the committed evidence.

The evidence inputs are:

| Input | What it establishes | What it does not establish |
| --- | --- | --- |
| M0.4 experiment record and transfer manifest | Framework-dependent loading and deterministic semantic output for the named synthetic fixture, SDK/package baseline, and required Ubuntu/Windows CI protocol. | Production loader behavior, arbitrary project compatibility, Native AOT feasibility, or a distribution channel. |
| M0.5 experiment record and manifest | The frozen M0.4 path under the exact Native AOT profile, matrix, evidence schema, and closed failure registry. | General Native AOT impossibility, a remediated AOT design, or successful native execution. |
| M0.5 Ubuntu and Windows cell evidence plus aggregate | Both required cells have the same conclusive publish-time negative and the aggregate is `not-feasible`. | Semantic output comparison for Native AOT, because publish failed before execution. |
| Architecture, distribution, and roadmap rules | Deterministic/offline/security boundaries and the M0.6 → M0.7 lifecycle. | A production implementation or consumer-facing support promise. |

Toolchain, package, runner-image, M0.4 semantic-path, M0.5 profile, or evidence-contract changes invalidate this evidence basis and require a new evidence version or explicit revalidation issue. Historical M0.4/M0.5 evidence remains immutable.

## Alternatives

### Framework-dependent semantic execution baseline — selected provisionally

This is the only eligible candidate with positive framework-dependent execution evidence. The M0.4 test-only host and semantic runner execute the Roslyn/MSBuild path in one process, using SDK/MSBuild discovery and the pinned package baseline. The observed process topology is recorded as a test-host fact; no production process topology is selected. The baseline is limited to the tested synthetic fixture and the documented Ubuntu/Windows, X64, SDK/package, and semantic comparison scope.

The initial runtime prerequisite is the repository's `global.json` policy: base SDK `10.0.102` with `latestFeature` roll-forward, plus the exact M0.4 package baseline. Each run records the actually selected SDK/runtime/MSBuild identity under that policy. M0.7 must inherit the policy and record the observed runtime RID for each framework-dependent cell (`linux-x64` and `win-x64`) without treating RID as a Native AOT publish input.

### Native AOT CLI using the exact M0.5 profile — rejected for this profile

M0.5 gives this candidate a conclusive negative within its closed matrix and publish profile: both Ubuntu/linux-x64 and Windows/win-x64 cells failed at publish-time trimming/reflection analysis. It is not selected as the M0 baseline. This disposition does not rule out a future, separately refined and evidenced AOT design.

### Framework-dependent loader plus AOT semantic core — deferred and not evidenced

M0.5 did not test a split loader/core boundary. The failed unchanged AOT path cannot establish that an AOT semantic core would work after splitting the runtime, and this ADR introduces no speculative project or abstraction. A future experiment must define the boundary and prove it before it can become eligible for selection.

### Loader child process — deferred and not evidenced

Child-process isolation is a process-topology option, not a directly evidenced M0 runtime baseline. A future issue would need to define ownership, transport/versioning, cancellation, exit and diagnostic propagation, cleanup, and public-safety behavior before this option can be selected.

### .NET-tool-first — deferred packaging preference

This is a distribution-channel layer that may later wrap a selected execution baseline. M0.6 does not decide global/local tool packaging, package identity, installation, update, signing, or publication. No .NET tool, NuGet package, GitHub Action, release, or support channel is created by this ADR.

## Decision

Select the `framework-dependent semantic execution baseline` provisionally for M0.7 validation. This selection is a decision inference from the directly exercised M0.4 path and the bounded M0.5 negative; it is not a production CLI or general compatibility claim.

M0.6 does not select a production process topology. The M0.4 in-process boundary is retained as a test-host observation only. M0.6 does not select a user-facing distribution channel; all channels, including .NET-tool-first, remain deferred and non-contractual.

The experimental `semantic-payload.json` remains only the M0.4/M0.5 canonical comparison artifact. M0.7 may reuse that comparison boundary, but it does not replace the separately versioned M0.2 audit-result contract and does not become a production CLI, IPC, or backward-compatibility contract through this ADR.

## M0.7 validation handoff

M0.7 must validate the selected execution baseline without adapting it. The subject under test is the frozen M0.4 framework-dependent semantic runner, `(logicalProjectId, documentationCommentId)` identity, ordering, serializer, canonical comparison contract, and pinned source revision/hashes. The independent validation repository supplies newly authored project source and a precommitted expected-output oracle; those inputs must not be copied from or generated using the M0.4 fixture or runner output. Independence applies to the source contents and oracle, not to the frozen runner's structural preconditions: the validation fixture must still contain exactly two SDK-style C# projects named `SampleApp` and `SampleLibrary`, with exactly one `SampleApp` → `SampleLibrary` project reference. A different project count, project name, language, or reference graph is a fixture/protocol error and is classified separately from selected-baseline support failure; M0.7 must not generalize or modify the runner to accept it.

The closed derivation is:

- Run required `ubuntu-latest` and `windows-latest` cells with `X64` process architecture.
- Use the repository `global.json` policy of base SDK `10.0.102` with `latestFeature` roll-forward and the exact M0.4 package baseline from the transfer manifest; record the actually selected SDK/runtime/MSBuild identity for each cell.
- Invoke the frozen framework-dependent runner shape with the independent solution and expected-output manifest. Do not add a new runtime mode or modify the frozen runner to make the independent smoke pass.
- Preserve the runner's frozen fixture-shape preconditions: exactly two SDK-style C# projects named `SampleApp` and `SampleLibrary`, and exactly one `SampleApp` → `SampleLibrary` reference. The source and oracle remain newly authored even though this structural envelope is fixed.
- Record observed runtime RIDs `linux-x64` and `win-x64`; they describe framework-dependent execution observations and are not Native AOT publish inputs.
- Run fresh processes and compare canonical semantic artifact bytes, not environment, path, timestamp, duration, or process fields. The independently authored oracle is the comparison authority.

M0.7 outcomes have these meanings:

- Missing or incompatible toolchain, incomplete restore, or other conforming-environment absence is inconclusive and cannot satisfy #10; the required cell must be rerun under a conforming environment.
- Complete semantic-path failure, nondeterminism, or mismatch with the independent oracle is a selected-baseline failure. It keeps M0 open and requires this ADR or the selected baseline to be revised or superseded before validation is repeated.
- Fixture, provenance, public-safety, or contract errors are protocol failures, not baseline support evidence.
- Any change to the selected baseline during validation invalidates the candidate and returns to M0.6.
- Success requires all required cells to complete the frozen runner path, agree with the independent oracle, and preserve the documented deterministic/offline/public-safety boundaries.

After successful M0.7, a follow-up PR updates this ADR from `provisional candidate baseline pending M0.7` to `M0-validated baseline` and links the public evidence. A failed M0.7 leaves M0 open; #10 records the failure and links the revision/revalidation issue or PR that amends or supersedes this ADR. No failed or inconclusive result can be converted silently into M0 closure.

## M1 implications

M1 may plan around the framework-dependent semantic execution boundary and the versioned M0.1-M0.3 contracts, but it must keep the M0.4/M0.5 hosts and `semantic-payload.json` test-only. M1 must define production audit inputs and outputs through the M0.1 policy/configuration and M0.2 audit-result contracts, not through the experimental payload. M1 may introduce a production loader only through its own implementation contract and validation; this ADR does not create that project, API, process protocol, or CLI behavior.

The selected baseline remains limited to the tested synthetic project shape, SDK/MSBuild/package policy, Ubuntu/Windows X64 matrix, and no-network semantic path. Support for other project types, frameworks, analyzers, generators, operating systems, architectures, RIDs, toolchains, or installation channels requires separate evidence and explicit follow-up.

## Consequences and risks

Positive consequences:

- M0.7 has a directly exercised subject under test and an independent fixture/oracle boundary.
- M1 receives a concrete framework-dependent execution assumption without prematurely adding production runtime code or a package channel.
- The Native AOT result is preserved as a useful bounded negative without overclaiming.

Costs and residual risks:

- The selected framework-dependent path may require a compatible SDK/MSBuild environment and may not generalize beyond the tested synthetic solution.
- SDK, Roslyn, MSBuild, trimming, package, runner-image, or evidence-contract drift can invalidate the evidence basis.
- A future child-process or AOT split could improve deployment or isolation, but its protocol and runtime behavior are currently unknown.
- Deferring the distribution channel leaves installation and upgrade UX undecided; that is intentional until the execution baseline is independently validated and release governance is ready.

Concrete future experiments, runtime prototypes, platform expansions, and publication decisions require separate issues. General residual risks may remain here without speculative placeholder issues.

## Follow-up issues

The following live issues make the deferred boundaries actionable without selecting them in M0.6:

- [#10 — Validate the selected baseline on an independent synthetic repository](https://github.com/SolusQuest/contract-scribe/issues/10) owns the M0.7 validation gate and independent fixture/oracle.
- [#17 — Define production process topology after M0.7](https://github.com/SolusQuest/contract-scribe/issues/17) owns the later in-process versus child-process or split-runtime decision.
- [#18 — Define the first distribution and publication channel](https://github.com/SolusQuest/contract-scribe/issues/18) owns packaging, provenance, support, and compatibility decisions after execution validation and topology review.

These issues are dependencies for later decisions, not additional selected baselines or implementation scope for this ADR.

## Public-safety and compatibility boundary

This ADR and its evidence links contain only public repository artifacts and sanitized experiment records. They must not add private downstream identifiers, local paths, raw logs, credentials, environment dumps, prompts, or unbounded toolchain output.

Before a downstream-consumable release, package/tool/action publication, or external contribution path, the repository must make the separate license and contribution-policy decision. Internal loader/process boundaries, experimental host names, and distribution choices are not backward-compatibility commitments before that gate. The versioned M0.1-M0.3 contracts and their documented canonical semantics remain the relevant future contract inputs.

## References

The repository artifacts below are pinned to public commits on `main`. Issue links are live tracking references and are intentionally not commit-pinned:

- [Roadmap](https://github.com/SolusQuest/contract-scribe/blob/60ddd6f481a9514f069af001388ddfdf9bc83502/docs/90_roadmap/roadmap.md)
- [Initial issue plan](https://github.com/SolusQuest/contract-scribe/blob/60ddd6f481a9514f069af001388ddfdf9bc83502/docs/90_roadmap/initial-issue-plan.md)
- [Architecture](https://github.com/SolusQuest/contract-scribe/blob/749c339e3a8f54e000c2c6aebd1bb3b8d37720da/docs/20_architecture/architecture.md)
- [Distribution policy](https://github.com/SolusQuest/contract-scribe/blob/749c339e3a8f54e000c2c6aebd1bb3b8d37720da/docs/20_architecture/distribution.md)
- [M0.4 experiment record](https://github.com/SolusQuest/contract-scribe/blob/19de6b7d742cb496523567d9ddef11304e07bf09/docs/20_architecture/experiments/m0.4-framework-dependent-loading.md)
- [M0.4 transfer manifest](https://github.com/SolusQuest/contract-scribe/blob/19de6b7d742cb496523567d9ddef11304e07bf09/tests/fixtures/roslyn-msbuild/v1/transfer-manifest.json)
- [M0.5 experiment record](https://github.com/SolusQuest/contract-scribe/blob/63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e/docs/20_architecture/experiments/m0.5-native-aot-feasibility.md)
- [M0.5 manifest](https://github.com/SolusQuest/contract-scribe/blob/63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e/tests/fixtures/roslyn-msbuild/v1/m0.5-native-aot-manifest.json)
- [M0.5 Ubuntu evidence](https://github.com/SolusQuest/contract-scribe/blob/63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e/tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-linux-x64-evidence-v1.json)
- [M0.5 Windows evidence](https://github.com/SolusQuest/contract-scribe/blob/63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e/tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-win-x64-evidence-v1.json)
- [M0.5 aggregate evidence](https://github.com/SolusQuest/contract-scribe/blob/63fd9a0ab5ff33ae20d8f7b9e66714a96feea39e/tests/fixtures/roslyn-msbuild/v1/evidence/m0.5-summary-v1.json)
- [Issue #9](https://github.com/SolusQuest/contract-scribe/issues/9)
- [Issue #10](https://github.com/SolusQuest/contract-scribe/issues/10)
- [Issue #17](https://github.com/SolusQuest/contract-scribe/issues/17)
- [Issue #18](https://github.com/SolusQuest/contract-scribe/issues/18)
