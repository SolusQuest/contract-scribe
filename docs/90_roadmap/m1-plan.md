# M1 plan: deterministic audit MVP

## Status and purpose

M1 is the current product milestone. This document is the durable planning source for the remaining M1 GitHub milestone and issue graph.

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

The original product requirement includes different documentation levels, at minimum external API and internal/assembly-visible targets. ADR 0003 accepted the exact semantics. Issue #35 established their first coordinated pre-release v1 machine-contract baseline, and Issue #55 owns the corrected successor after implementation exposed a classification-origin inconsistency. The prior M0 baseline could not fully express the requirement because:

- Policy v1 selects by project and source path only.
- Taxonomy v1 enumerates an externally reachable target surface.
- Audit Result v1 composes those exact inputs.

The corrected Issue #55 successor contract set merged before downstream production implementation was accepted for the affected target behavior. The durable affected-artifact and downstream disposition record is the [pre-release v1 baseline inventory](../20_architecture/contracts/pre-release-v1-baseline.md).

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

M1 observes the direct presence of each represented parameter, type-parameter, return, and value component. It does not resolve `<inheritdoc/>` or `<include>` or treat their effective content as direct documentation. Effective-documentation resolution, rendered-document completeness, broader tag-coverage audits, and prose-quality audits are outside M1 through M6 and remain non-committed Post-M6 candidates. Only syntax and attachment handling needed to classify the directly attached documentation block under ADR 0003 belongs to M1.

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

The corrected contract-content baseline and accepted Host Validation bundle now unblock exact-revision Host evidence execution. Exact Issue #55 squash commit `S1`, also called contract baseline `C1`, owns the original successor manifest identity. Issue #57 completed main-reachable acceptance at bundle target `S2` (`9da769486aae370403ddd06a5172a6ddc5b25a0d`) and review-record commit `S3` (`35f9ec815108ec9934027dd67d97dd69a85c6d07`).

M0, Issue #35, Issue #55, and Issue #57 historical evidence remain pinned to their exact revisions. Frozen evidence artifacts may retain process language that described their own acceptance lifecycle; those records do not override the current [pre-release engineering rules](../00_project/pre-release-engineering.md) for new tracker actions.

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

Issue #26 froze the M1 validation protocol, matrix, expected observations, failure classification, evidence schema, and executable harness against the completed contracts and ADRs. Issue #57 then promoted one stable candidate into the accepted, main-reachable bundle without executing production evidence. Issue #41 owns the remaining exact-revision execution and bounded aggregate evidence.

The accepted bundle identity is `m1hvp1.17a0515ea992abaaa39c2111836891c5e87c0322f93c0b51f349712839008d28`; its accepted review identity is `review.ea3cbe944821d110ec28cdbbf86a8056890fa9f9b2a6442f4809931067f63fe0`. Before #41 executes, a fresh drift check must prove that the protected inputs and bundle members still match that exact accepted identity. A pull request that does not change a protected input needs no separate reviewed no-change record; its diff and required CI are the proof.

If the fresh check finds real protected-input or bundle-member drift, #57 remains closed as historical certification evidence. The current failing boundary receives the smallest bounded new issue required to restore a current accepted bundle, and that issue blocks #41 until complete. Only evidence that consumes the changed runtime or protected input becomes stale and reruns in dependency order. Tracker metadata, documentation, or other changes outside an identity preimage do not replay the certification chain. Immediate safety containment may proceed without waiting for ordinary correction limits.

The accepted protocol and #41 execution establish the validated host baseline; they are not final CLI-composition evidence. Final M1 evidence combines that host baseline, #30's exact-revision cross-platform CLI integration record, and #42's independent smoke, all bound to one compatible exact revision set.

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

The tracker graph uses the following ownership and expected review boundaries. Final issue bodies may tighten file scope or dependencies, but they must preserve these primary outcomes and the repository-wide decomposition rule.

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
| #57 Host Validation accepted-bundle promotion | Completed direct sibling M1 child | Historical two-commit acceptance sequence that established exact `S2` and `S3`; not a reusable default for new work |
| Host-validation execution | Direct sibling M1 child | One exact-revision aggregate-evidence PR against #26 |
| #30 CLI implementation | Direct M1 child | One focused CLI implementation PR including integration tests |
| Independent read-only smoke | Direct M1 child | One exact-revision evidence PR or bounded attestation PR |
| #17 process topology | External completed architecture dependency | No new executable M1 work unless an ADR reconsideration trigger creates a separate issue |

Issues #18, #27, and #29 belong to release or research tracks and are not M1 children.

## Refinement status and remaining review boundaries

The one-time tracker synchronization and Host Validation certification sequence are complete. Historical issue counts, changed-line forecasts, and the two-commit #57 acceptance sequence are not requirements for remaining M1 work.

The remaining critical path is intentionally small:

- #41: one focused exact-revision Host evidence pull request by default;
- #30: one focused CLI implementation and cross-platform integration pull request by default;
- #42: one focused independent smoke or bounded attestation pull request by default;
- #33: coordination and closure evidence only, with no executable pull request of its own unless a separately acceptable repository artifact is discovered.

Combine or split a remaining issue only when that creates independently acceptable outcomes under [Issue workflow](../10_workflow/issue-workflow.md). File count, line count, elapsed time, or an estimate miss never substitutes for that product boundary. M0 experiment code remains evidence and implementation input; it is not promoted, renamed, or reused line-for-line without production review.

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

#24 host composition + focused implementation siblings + #26 frozen protocol
    --> completed #57 accepted bundle at S2/S3
    --> #41 exact-revision validation execution
    --> validated host baseline

validated host baseline + #25 CLI contract --> #30 CLI implementation
#30 CLI implementation --> #42 independent read-only smoke
#42 independent read-only smoke --> #33 M1 closure checklist
```

Every executable node in this diagram is a direct child of the M1 execution parent. Arrows express native dependencies, not parentage. #30's cross-platform integration evidence binds the exact CLI and validated host revisions, and #42 exercises that final combination.

## Current tracker disposition

The one-time synchronization described by earlier revisions of this plan is complete and remains available in Git history. It is not an active mutation procedure.

| Issue | Current disposition | M1 ownership |
| --- | --- | --- |
| #17 | Closed architecture evidence | External completed dependency |
| #18 | Deferred distribution/publication work | Outside M1; refine before activation |
| #27 | Deferred process-topology research | Outside M1; refine before activation |
| #29 | Release governance | Outside M1; no current body change required |
| #57 | Closed accepted-bundle certification at exact `S2` and `S3` | Completed direct child |
| #41 | Next: exact-revision Host validation evidence | Direct child |
| #30 | After #41: CLI implementation and integration evidence | Direct child |
| #42 | After #30: independent read-only smoke | Direct child |
| #33 | M1 coordination and closure evidence | Coordination-only parent |

New tracker writes follow the current issue and pre-release engineering rules. They do not reproduce the historical full-manifest synchronization, automatically reopen completed certification work, or create metadata-only revalidation chains.

## Milestone exit evidence

The M1 parent closes only when it links:

- the merged target/observation decision;
- the coordinated current v1 contract baseline;
- production-host implementation revisions;
- frozen validation protocol, accepted main-reachable bundle, and passing aggregate evidence;
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
