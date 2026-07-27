# M1 plan: deterministic audit MVP

## Status and purpose

M1 is the current product milestone. This document is the durable planning source for refining the M1 GitHub milestone and issue graph after the documentation baseline merges.

M1 turns the M0 execution and contract evidence into a production read-only audit. It does not generate documentation, modify source, call a model, write to GitHub, or select a public distribution channel.

## Decision inputs

- [Policy/Configuration v1](../20_architecture/contracts/policy-configuration-v1.md)
- [Symbol and Evidence Taxonomy v1](../20_architecture/contracts/symbol-evidence-taxonomy-v1.md)
- [Audit Result v1](../20_architecture/contracts/audit-result-v1.md)
- [ADR 0001](../20_architecture/decisions/0001-loader-and-distribution-boundary.md)
- [ADR 0002](../20_architecture/decisions/0002-process-topology.md)
- [Contract lifecycle](../00_project/contract-lifecycle.md)
- [Security boundary](../20_architecture/security-boundary.md)

ADR 0001 fixes the framework-dependent execution baseline. ADR 0002 fixes one in-process ContractScribe runtime per audit. M1 does not reopen those choices unless a recorded reconsideration trigger occurs.

## Product requirements owned by M1

### Target profiles

The original product requirement includes different documentation levels, at minimum external API and internal/assembly-visible targets. ADR 0003 accepted the exact semantics, and Issue #35 coordinates their pre-release v1 machine-contract baseline. The prior M0 baseline could not fully express the requirement because:

- Policy v1 selects by project and source path only.
- Taxonomy v1 enumerates an externally reachable target surface.
- Audit Result v1 composes those exact inputs.

The coordinated Issue #35 contract set must merge before downstream production implementation fixes target behavior. The durable affected-artifact and downstream disposition record is the [pre-release v1 baseline inventory](../20_architecture/contracts/pre-release-v1-baseline.md).

The decision must define:

- declared and effective accessibility;
- containing-type accessibility and reachability;
- public, protected, protected-internal, internal, and private-protected treatment;
- symbol-kind selection;
- nested types;
- interfaces, overrides, and explicit implementations;
- generated, synthesized, and mixed-origin declarations;
- profile names and exact membership.

The minimum accepted profiles are:

- `profile.external-api`;
- `profile.assembly-visible`.

The decision must not assume that C# accessibility is a simple numeric order. Profiles or explicit sets are preferred.

### Documentation observation

M1 must define what `documentation.present`, `documentation.absent`, and `documentation.unavailable` mean in production.

The decision covers:

- direct documentation trivia versus symbol-level documentation;
- `<inheritdoc/>`;
- malformed XML;
- whitespace-only blocks;
- partial declarations and aggregation;
- conditional compilation;
- generated source;
- interface and override inheritance;
- declaration ambiguity;
- evidence required for present and absent observations.

Documentation quality beyond presence, such as incomplete `<param>` coverage or weak prose, is post-MVP unless required to make presence unambiguous.

### Input and output

The first production host accepts:

- an explicit repository root;
- one explicit `.sln`, `.slnx`, or `.csproj` input;
- one explicit policy document;
- one explicit output path.

It does not perform directory auto-discovery or automatic restore. The caller prepares restore/build assets.

The canonical audit result contains no timestamps, duration, process ID, machine identity, absolute paths, or provider/platform metadata. Runtime diagnostics and execution metadata use a separate non-canonical envelope.

## M1 project boundary

M1 creates one production project: `src/ContractScribe.Roslyn/ContractScribe.Roslyn.csproj`.

The M1 product graph is:

```text
ContractScribe.Cli
  +-- ContractScribe.Core
  +-- ContractScribe.Roslyn --> ContractScribe.Core
```

Responsibilities:

- `ContractScribe.Core` owns normalized policy, target, observation, evidence, audit-result, failure, and port contracts that do not depend on Roslyn or the CLI.
- `ContractScribe.Roslyn` owns MSBuild/Roslyn loading, classification, documentation observation, bounded evidence, and implementations of the read-only audit ports.
- `ContractScribe.Cli` owns option parsing, composition, cancellation, diagnostics, atomic output invocation, and exit-code mapping.

The existing `tests/ContractScribe.Roslyn` assembly is M0 experiment support. Production code must not reference it. M1 may migrate reusable logic or lessons into the new `src` project, but the production implementation receives a clean namespace, dependency graph, API review, and validation rather than promoting the experiment assembly in place.

Create `tests/ContractScribe.IntegrationTests` when real workspace, filesystem, or process behavior would make the existing fast test suite slow or host-dependent. The M1 issue must decide the split before accumulating integration-only dependencies in `ContractScribe.Tests`.

M1 does not create `ContractScribe.Patching`, `ContractScribe.Agent`, `ContractScribe.GitHub`, an Action package, or a TypeScript workspace. See [Project structure](../20_architecture/project-structure.md).

## Contract completion strategy

M1 does not automatically create Policy v2, Taxonomy v2, or Audit Result v2.

The current v1 artifacts are pre-release drafts. The target-surface and observation decision is implemented as one coordinated amendment:

- normative docs;
- schemas and registries;
- fixture manifests and raw fixtures;
- test-only oracles;
- cross-contract conformance;
- dependent ADR and roadmap references.

The amendment retains version `1` unless incompatible revisions must coexist or a consumer compatibility condition in [Contract lifecycle](../00_project/contract-lifecycle.md) is met.

M0 historical evidence remains pinned to its exact revision. The M1 amendment creates a new milestone baseline after validation.

## Workstreams

### W1 — M1 parent and decision gate

Create an independent M1 execution parent that owns the dependency graph, exit checklist, closure evidence, and blocker classification. It links the completed roadmap design gate as a design prerequisite but is not its child. No executable work issue is a child of the design gate. The M1 parent is coordination-only: it contains no unbounded implementation, validation, contract, documentation, or experiment work.

Every executable M1 issue is a direct child of the M1 parent or is recorded as an external dependency with an owner, milestone or track, and rationale. Release-gate and research work is never a child of the M1 parent.

Create one decision issue for target-surface and documentation-observation semantics. It must close before production classification or observation implementation begins.

### W2 — Coordinated pre-release contract amendment

Create one coordinated contract-change issue with one primary outcome: establish the complete current pre-release v1 baseline for the selected target-surface and documentation-observation semantics. Its acceptance contract names every affected specification, schema, registry, fixture, oracle, implementation, compatibility consequence, and validation gate.

Prefer one coordinated contract pull request while those artifacts are mutually required for acceptance. Split only when a separated artifact is independently useful and acceptable and requires its own pull-request or review cycle. If a split is justified, every resulting issue remains a direct child of the M1 parent: component issues are dependency-linked siblings, and the coordinated contract-change issue owns final cross-contract consistency, conformance, and the current-baseline record. Dependency state alone never justifies an incomplete contract fragment.

The amendment is a blocker for the classification, policy, and result work in W3.

### W3 — Production audit host

Refine current issue #24 to one focused production-host composition outcome: compose the completed M1 audit components into the in-process execution lifecycle, enforce cancellation and failure precedence, invalidate stale output, and publish the canonical result atomically. It remains a direct child of the M1 parent and does not own nested executable children.

Create direct sibling M1 issues for these initial candidate outcomes:

1. Create the production `ContractScribe.Roslyn` project while implementing explicit repository and solution/project input resolution, Roslyn/MSBuild registration, workspace loading, compilation acquisition, prerequisite behavior, bounded loader diagnostics, reference-boundary tests, and the no-auto-discovery/no-automatic-restore contract.
2. Implement target-profile and symbol classification with its required fixtures and contract conformance.
3. Implement XML-documentation observation with partial, generated, inherited, malformed, absent, and unavailable fixtures.
4. Implement policy evaluation and bounded evidence binding against the completed v1 contracts.
5. Aggregate and serialize the canonical audit result with cross-contract conformance and fresh-process determinism tests.

These are refinement candidates, not permission to create fragments mechanically. Before tracker synchronization, each candidate and the refocused #24 must be reviewed against [Issue workflow](../10_workflow/issue-workflow.md). Combine candidates when they cannot be accepted independently; split further only when every resulting issue remains a coherent, independently useful outcome. Each implementation issue includes the unit tests, contract fixtures, and validation needed for its own acceptance. All executable results remain direct children of the M1 parent; dependencies express workstream ordering without creating a second issue hierarchy.

Split a separate calibration experiment only when a bound cannot be selected from implementation evidence in the owning child.

Internally enforceable cooperative bounds must be distinguished from externally enforced process limits and from limits that the in-process topology cannot guarantee.

### W4 — CLI contract and implementation

Use issue #25 for the CLI contract. It defines:

- `audit` command and help;
- repository-root, input, policy, and output options;
- path forms;
- stdout/stderr behavior;
- canonical artifact versus human diagnostics;
- numeric exit codes for audit, execution, process, cancellation, and invalid usage;
- retained `--help`, `--version`, and `doctor` behavior;
- explicit absence of provider, GitHub, auto-discovery, and auto-restore options.

Use issue #30 for the thin CLI implementation and integration tests only. Its exact-revision integration suite binds both the selected host revision and CLI revision, runs in the required Ubuntu and Windows X64 cells, and records command mapping, composition, artifact, diagnostic, cancellation, and exit-code evidence. A change to any host-protected input invalidates the applicable host-validation execution evidence, every #30 integration record bound to the prior host revision, and any downstream smoke bound to that host/CLI pair. The affected host execution, #30 integration matrix, and downstream smoke must rerun in dependency order against one exact revision set before M1 closure. Durable release artifacts, packaging layout, storage classification, channel evidence, and release provenance belong to the payload-distribution track.

### W5 — Executable validation

Refine issue #26 to one independently acceptable outcome: freeze the M1 validation protocol, matrix, expected observations, failure classification, evidence schema, and executable harness against the completed contracts and ADRs.

Create a direct sibling M1 issue to execute the frozen protocol and publish bounded aggregate evidence against the exact implementation revision.

The refocused #26 is independently acceptable because it creates the reviewed oracle for later execution. The execution issue is independently acceptable because it binds results to that frozen protocol and exact implementation revision. Each owns one focused pull request and its required validators. Neither issue is a second-level child; both are direct children of the M1 parent.

The #26 protocol and its execution establish the validated host baseline; they are not described as final CLI-composition evidence. Final M1 evidence combines that host baseline, #30's exact-revision cross-platform CLI integration record, and the independent smoke, all bound to one compatible exact revision set. Host-protected input drift reopens the affected protocol execution, invalidates the #30 integration record and downstream smoke that reference the prior host revision, and requires those applicable evidence stages to rerun even when unchanged CLI-only checks remain green.

The protocol covers:

- production project-reference and forbidden-dependency checks;
- proof that no `src` project references an M0 experiment assembly;
- canonical contract conformance;
- fresh-process determinism;
- Ubuntu and Windows X64;
- target-profile fixtures;
- cancellation and failure precedence;
- stale artifact invalidation and atomic publication;
- repository-root and symlink/reparse escape handling;
- working-directory independence;
- public diagnostics and credential-marker scans;
- repository source/project write scans;
- unsupported project and language inputs;
- analyzer, generator, custom-target, and multi-targeting support dispositions;
- no automatic restore or ContractScribe-initiated runtime download.

Ordinary CI does not prove enforced network isolation. Claims use the precise no-declared-network-dependency and no-ContractScribe-initiated-network wording.

### W6 — Independent read-only smoke

Create one issue for a real-world public repository or private downstream read-only smoke.

The smoke binds the exact validated host and CLI revision. A change to either revision invalidates the smoke and requires it to rerun after the applicable host and CLI integration evidence is current. A public target pins its commit. A private target publishes only a sanitized attestation containing:

- contract and tool identities;
- high-level project-shape classification;
- aggregate outcome counts;
- success or bounded failure;
- bounded unresolved risks without downstream identity or implementation detail.

It never publishes repository identity, private paths, source, configuration, prompts, raw logs, or detailed evidence.

## M1 issue ownership and review boundaries

The planned tracker graph uses the following ownership and expected review boundaries. Final issue bodies may tighten file scope or dependencies, but they must preserve these primary outcomes and the repository-wide decomposition rule.

| Work item | M1 relationship | Primary outcome and expected review boundary |
| --- | --- | --- |
| M1 execution parent | Independent coordination root | Tracker-only dependency graph and closure evidence; no unbounded executable work |
| Completed roadmap design gate | External completed prerequisite | Merged docs baseline and synchronized tracker contract; not an M1 child |
| Target/observation decision | Direct M1 child | One decision-document PR defining the semantics that block contract and implementation work |
| Coordinated v1 contract change | Direct M1 child | Ordinarily one coordinated contract PR; when independently acceptable component PRs are justified, this issue owns their final integration, conformance, and baseline record |
| Production loading, classification, observation, policy/evidence, and result units | Direct sibling M1 children | One focused implementation PR per independently acceptable outcome, including its required tests, fixtures, and conformance |
| #24 production host | Direct M1 child | One host-composition PR covering execution lifecycle, cancellation/failure precedence, stale-output invalidation, and atomic publication |
| #25 CLI contract | Direct M1 child | One CLI-contract PR with executable acceptance fixtures where applicable |
| #26 host-validation protocol | Direct M1 child | One frozen-protocol and executable-harness PR |
| Host-validation execution | Direct sibling M1 child | One exact-revision aggregate-evidence PR against #26 |
| #30 CLI implementation | Direct M1 child | One focused CLI implementation PR including integration tests |
| Independent read-only smoke | Direct M1 child | One exact-revision evidence PR or bounded attestation PR |
| #17 process topology | External completed architecture dependency | No new executable M1 work unless an ADR reconsideration trigger creates a separate issue |

Issues #18, #27, and #29 belong to release or research tracks and are not M1 children.

## Refinement status and non-binding sizing

The current synchronization baseline contains fourteen M1 tracker nodes:

- one coordination-only M1 execution parent;
- thirteen candidate executable direct children;
- four refocused existing executable issues: #24, #25, #26, and #30;
- nine proposed executable issues listed under [Current tracker disposition after docs merge](#current-tracker-disposition-after-docs-merge).

The completed process-topology decision #17 and the roadmap design gate are external prerequisites rather than executable M1 children and are not included in that count.

The final synchronized graph is expected to contain approximately twelve to fifteen executable issues and a similar number of focused pull requests. This is a planning forecast, not an acceptance criterion or a required issue count. A candidate is combined or split only when the result satisfies [Issue workflow](../10_workflow/issue-workflow.md); file count, line count, elapsed time, or an estimate miss never substitutes for an independently acceptable outcome.

Use these coarse sizing bands only to detect refinement risk:

| Band | Typical pull-request surface |
| --- | --- |
| S | Approximately 3–8 touched files and 150–500 changed lines |
| M | Approximately 8–18 touched files and 500–1,200 changed lines |
| L | Approximately 15–35 touched files and 1,000–2,500 changed lines |

Changed-line estimates include production code, tests, schemas, fixtures, scripts, evidence, and documentation. File ranges overlap because shared contract and fixture files may be touched by several issues. Generated CI evidence is excluded.

The current candidate sizing is:

- S–M: target/observation decision, validation execution, and independent smoke;
- S: #25 CLI contract;
- M: classification, documentation observation, policy/evidence, result aggregation, and #30 CLI implementation;
- M–L: production loading/input, #24 host composition, and #26 validation protocol;
- L: the coordinated v1 contract change.

The full M1 milestone is provisionally expected to touch approximately 80–140 unique files and produce 9,000–17,000 changed lines across 12–15 pull requests. These aggregate figures are non-binding capacity estimates and must not be copied into issue acceptance criteria.

Four boundaries require explicit review while drafting the synchronization manifest:

1. Keep the coordinated contract artifacts together when none of them is independently acceptable; do not split schemas, registries, fixtures, or oracles merely to reduce a pull-request diff.
2. The production loading/input issue must deliver an executable, tested loading path. Creating an otherwise empty `ContractScribe.Roslyn` project is not an acceptable issue outcome.
3. Split policy evaluation from evidence binding only if the evaluator has an independently useful contract, fixtures, and acceptance result; otherwise keep them together.
4. Keep #24 limited to host composition, terminal-state behavior, and atomic publication. If it absorbs classification, observation, policy, or result-component implementation, refine those outcomes back into direct sibling issues.

M0 experiment code provides execution evidence, fixtures, failure observations, and implementation lessons. Estimates do not assume that experiment source can be promoted, renamed, or reused line-for-line in the production project.

## Dependency graph

```text
Target/observation decision
    +--> coordinated v1 contract amendment
    |        +--> focused production implementation siblings
    |        |        +--> #24 host composition
    |        |
    |        +--> #26 frozen validation protocol
    |
    +--> #25 CLI contract

#24 host composition + #26 frozen protocol
    --> exact-revision validation execution
    --> validated host baseline

validated host baseline + #25 CLI contract --> #30 CLI implementation
#30 CLI implementation --> independent read-only smoke
independent read-only smoke --> M1 closure checklist
```

Every executable node in this diagram is a direct child of the M1 execution parent. Arrows express native dependencies, not parentage. The CLI contract may draft in parallel after the target-surface decision, but #30's cross-platform integration evidence binds the exact CLI and validated host revisions, and the smoke exercises that final combination.

## Current tracker disposition after docs merge

The following updates are planned but must not occur until this documentation is merged and full-commit-SHA links exist:

| Issue | Planned disposition | Ownership after synchronization |
| --- | --- | --- |
| #17 — production process topology | Keep closed as completed architecture evidence | External completed dependency of the M1 parent |
| #18 — distribution and publication channel | Move to Release Gate — Payload Distribution and refine for GitHub Action as the first consumer | Outside M1 |
| #24 — production audit host | Keep in M1; refocus to host composition, execution lifecycle, cancellation/failure precedence, and atomic publication; add dependencies on focused sibling implementation issues | Direct child of the M1 parent |
| #25 — M1 CLI surface | Keep in M1; refine contract and acceptance criteria | Direct child of the M1 parent |
| #26 — production topology validation | Keep in M1; refocus to the frozen validation protocol and executable harness; create a sibling exact-revision execution issue | Direct child of the M1 parent |
| #27 — child-process prototype | Move to Research — Deferred Process Topology; it does not gate M1 | Outside M1 |
| #29 — license and contribution policy | Move to Release Gate — Governance; it gates release, not M1 audit | Outside M1 |
| #30 — CLI implementation | Keep in M1; remove distribution and durable-release-artifact responsibilities | Direct child of the M1 parent |

New M1 issues:

- M1 parent;
- target-surface and documentation-observation decision;
- coordinated pre-release v1 contract amendment;
- production loading and input-boundary implementation;
- target-profile and symbol classification;
- XML-documentation observation;
- policy evaluation and bounded evidence binding;
- canonical audit-result aggregation and serialization;
- exact-revision host-validation execution;
- independent read-only smoke.

This is the current candidate issue set; synchronization may combine or further split a candidate only under the merged decomposition rule. Each resulting executable issue is a direct child of the M1 parent. The M1 parent itself owns the closure record; a separate closure issue is unnecessary unless the evidence publication is an independently useful and acceptable deliverable with its own pull-request and review cycle.

Before any tracker write, re-evaluate every existing and proposed M1 issue against [Issue workflow](../10_workflow/issue-workflow.md) and record its primary outcome, complete acceptance boundary, dependencies, expected pull-request boundary, and parent relationship in the reviewed synchronization manifest. Any intentionally larger executable issue must explain why one focused pull-request and review cycle remains its coherent boundary.

## Milestone exit evidence

The M1 parent closes only when it links:

- the merged target/observation decision;
- the coordinated current v1 contract baseline;
- production-host implementation revisions;
- frozen validation protocol and passing aggregate evidence;
- CLI contract and implementation;
- independent read-only smoke;
- exact M1 baseline commit and toolchain;
- every unresolved non-blocking risk and its owner;
- every decision reconsideration triggered by failed evidence.

Distribution, licensing, proposal generation, patch generation, state, GitHub mutation, and child-process research are not M1 exit evidence.

## Evidence publication and compatibility

- Published fixtures are synthetic and contain no secrets, credentials, private source, private identifiers, or machine-local paths.
- A private downstream smoke publishes only the bounded sanitized attestation defined in W6.
- Draft contract semantics are pinned with full commit-SHA links whose commits are reachable from `main`.
- The M1 baseline is not described as a released support promise.
- A failed required matrix cell or smoke keeps M1 open unless the milestone contract is explicitly amended and independently reviewed.
