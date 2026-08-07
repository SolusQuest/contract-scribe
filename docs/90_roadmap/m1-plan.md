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

ADR 0001 records the framework-dependent execution evidence. ADR 0002 selects one in-process ContractScribe runtime per audit for the current production implementation. New evidence may reconsider that current choice in the issue that owns the affected boundary; it does not automatically reopen either closed issue.

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

The current v1 artifacts are pre-release drafts. A target-surface or observation change updates the affected current contract path coherently:

- normative docs;
- schemas and registries;
- affected fixture inventories and raw fixtures;
- test-only oracles;
- cross-contract conformance;
- dependent ADR and roadmap references.

The amendment retains version `1` unless incompatible revisions must coexist or a consumer compatibility condition in [Contract lifecycle](../00_project/contract-lifecycle.md) is met.

The corrected contract content and Issue #57 Host Validation bundle remain historical evidence at their original revisions, but current pre-release validation is ordinary production-focused CI rather than bundle-authorized or governed by successor-baseline identities. Repository CI currently preserves both a release-like Host certification system and completed M0 experiment protocols. Issue #75 removes those active historical authorities and establishes the minimum direct production execution and integration coverage needed before #41 runs.

M0, Issue #35, Issue #55, Issue #57, and Issue #70 evidence remains available at the exact revisions that produced it. Historical documents may describe their original acceptance lifecycle; their current-tree harnesses, manifests, allowlists, tombstones, or compatibility modes are not retained solely to keep that history executable.

## Workstreams

### W1 — M1 parent and decision gate

Create an independent M1 execution parent that owns the dependency graph, exit checklist, closure evidence, and blocker classification. It links the completed roadmap design gate as a design prerequisite but is not its child. No executable work issue is a child of the design gate. The M1 parent is coordination-only: it contains no unbounded implementation, validation, contract, documentation, or experiment work.

Every executable M1 issue is a direct child of the M1 parent or is recorded as an external dependency with an owner, milestone or track, and rationale. Release-gate and research work is never a child of the M1 parent.

Create one decision issue for target-surface and documentation-observation semantics. It must close before production classification or observation implementation begins.

### W2 — Coordinated pre-release contract amendment

Use one coordinated contract-change issue when separate planning is useful: establish the complete current pre-release v1 behavior for the selected target-surface and documentation-observation semantics. Its acceptance contract names the specifications, producers, consumers, schemas or registries, fixtures, validators, implementations, and checks that are actually affected.

Prefer one coordinated contract pull request while those artifacts are mutually required for acceptance. Split only when a separated artifact is independently useful and acceptable and requires its own pull request or genuinely separate authority review. Preserve historical milestone evidence without creating a new current-baseline record as a routine amendment step.

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

Use issue #30 for the thin CLI implementation and integration tests only. Its ordinary Ubuntu and Windows X64 CI invokes the real Host and records command mapping, composition, artifact, diagnostic, cancellation, and exit-code behavior. The pull request proves its exact head; the ordinary main-push run proves the merged revision. A later Host or CLI change leaves earlier runs historically true and requires fresh current CI only before a downstream claim consumes the changed behavior. Do not add a separate Host applicability ledger, protected-input manifest, evidence report, or post-merge repository mutation. Durable release artifacts, packaging layout, storage classification, channel evidence, and release provenance belong to the payload-distribution track.

### W5 — Executable validation

Issue #26 recorded an extensive M1 validation design and harness, and Issue #57 accepted one stable bundle without executing production evidence. They remain historical evidence for those revisions, not current authorities that later implementation must preserve or repair.

A repository audit found that `.github/workflows/ci.yml` validates the historical Host bundle structurally, performs platform dry-runs, self-tests the harness, and reproduces the completed M0.7 evidence-publication pipeline around the pinned M0.4 experiment. The Host path does not execute the production subject needed by #41, while M0.4/M0.5/M0.7 manifests, scripts, and tests continue binding development to historical experiment protocols. Both layers add development coupling without proving the current production audit path.

This documentation and CI correction removes the obsolete Host bundle validation/dry-run/self-test and historical M0.7 external-checkout, evidence-publication, and aggregation steps from ordinary CI without refreshing their locks or identities. Ubuntu and Windows continue to restore, build, test, format, and exercise the CLI smoke path. After it merges, Issue #75 completes the implementation cleanup and production-focused replacement in one pull request: it deletes the Host protected-input manifest, artifact lock, bundle/review identities, review-only transition, certification commands, and the evidence schemas, manifests, validators, mutation corpus, self-test, aggregation, publication, or public-preparation machinery that has no current consumer beyond validating the validation product. It also deletes M0.4/M0.5/M0.7 current-tree historical manifests, compatibility modes, tombstones, provenance/aggregate scripts, and preservation-only tests when no production consumer remains. Reusable semantic fixtures and regression cases move under the current production owner rather than retaining experiment authority. There is one coherent exact-head review, one human merge, no post-merge repository mutation, and no closed-issue reopen. The Bug remains a native blocker of #41.

The Issue #70 contract evidence at exact squash commit `67c149fbc105d2ccae94becd6b2158b68027cbfd` (`C2`) remains immutable history. Issue #75 does not reopen #70, regenerate its manifest identity, or create a successor solely to remove Host lifecycle machinery. It deletes the current-tree #70 manifest and any dependency that exists only to enforce historical certification. Current Host behavior is defined by the production source, contracts, fixtures, and tests at the current revision.

Until #75 merges, lower-level Host protocol, baseline, fixture, validator, harness, historical experiment, and CI bundle language describes defective or retired machinery assigned to #75, not the required future M1 lifecycle. It cannot authorize a new production Host execution, accepted snapshot, bundle refresh, or #41 evidence claim. Draft PR #76 remains paused until this documentation baseline is main-reachable and the reviewed tracker corrections are complete.

The correction validates its exact pull-request head through direct production entry points, focused tests, and ordinary Ubuntu and Windows CI. GitHub's source, workflow, run, attempt, and required-job facts identify the run. Add one small run-local result envelope or artifact digest only if one job actually transfers a result to another and the transfer cannot be checked safely without it; do not build a separate common/per-cell identity protocol. After the human merge closes #75, Issue #41 runs the required production validation on exact main and records its run/job links and conclusions in the Issue. A later Host change leaves older evidence historically true but requires ordinary current CI before a downstream task consumes the changed revision.

The retained production validation covers:

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

Use one issue for a real-world public repository or private downstream read-only smoke.

The smoke runs the current validated host and CLI revision. A later change leaves the old smoke historically true and requires a new smoke only before a current downstream claim consumes the changed behavior. A public target pins its commit. A private target records a sanitized Issue attestation containing:

- contract and tool identities;
- high-level project-shape classification;
- aggregate outcome counts;
- success or bounded failure;
- bounded unresolved risks without downstream identity or implementation detail.

It never publishes repository identity, private paths, source, configuration, prompts, raw logs, or detailed evidence. No evidence-only repository PR is required unless the smoke discovers a necessary repository change or adds an independently useful fixture or test.

## M1 issue ownership and review boundaries

The tracker graph uses the following ownership and expected review boundaries. Final issue bodies may tighten file scope or dependencies, but they must preserve these primary outcomes and the repository-wide decomposition rule.

| Work item | M1 relationship | Primary outcome and expected review boundary |
| --- | --- | --- |
| M1 execution parent | Independent coordination root | Tracker-only dependency graph and closure evidence; no unbounded executable work |
| Completed roadmap design gate | External completed prerequisite | Merged docs baseline and synchronized tracker contract; not an M1 child |
| Target/observation decision | Direct M1 child | One decision-document PR defining the semantics that block contract and implementation work |
| Coordinated v1 contract change | Direct M1 child | Ordinarily one coordinated contract PR updating the actually affected producer-consumer and conformance surfaces |
| Production loading, classification, observation, policy/evidence, and result units | Direct sibling M1 children | One focused implementation PR per independently acceptable outcome, including its required tests, fixtures, and conformance |
| #24 production host | Direct M1 child | One host-composition PR covering execution lifecycle, cancellation/failure precedence, stale-output invalidation, and atomic publication |
| #25 CLI contract | Direct M1 child | One CLI-contract PR with executable acceptance fixtures where applicable |
| #26 Host Validation design and harness | Completed direct M1 child | Historical design and harness evidence at its accepted revision; not current implementation authority |
| #57 Host Validation accepted-bundle promotion | Completed direct sibling M1 child | Historical two-commit acceptance sequence that established exact `S2` and `S3`; not a reusable default for new work |
| #75 pre-release validation simplification | Direct sibling M1 child | One correction PR deleting active Host certification and retired M0 experiment machinery while establishing production-focused CI |
| #41 Host-validation execution | Direct sibling M1 child | Tracker-only exact-main workflow execution and Issue-recorded run/job conclusions by default |
| #30 CLI implementation | Direct M1 child | One focused CLI implementation PR including ordinary Host/CLI integration tests |
| #42 independent read-only smoke | Direct M1 child | One bounded Issue attestation; a repository PR only for an independently useful code or fixture change |
| #17 process topology | External completed architecture dependency | No new executable M1 work unless an ADR reconsideration trigger creates a separate issue |

Issues #18, #27, and #29 belong to release or research tracks and are not M1 children.

## Refinement status and remaining review boundaries

The one-time tracker synchronization and Host Validation certification sequence are complete. Historical issue counts, changed-line forecasts, and the two-commit #57 acceptance sequence are not requirements for remaining M1 work.

The remaining critical path is intentionally small:

- #75 pre-release validation simplification: one focused implementation pull request after this documentation baseline merges;
- #41: one tracker-only exact-main Host validation run by default;
- #30: one focused CLI implementation and cross-platform integration pull request by default;
- #42: one focused independent smoke recorded through a bounded Issue attestation by default;
- #33: coordination and closure evidence only, with no executable pull request of its own unless a separately acceptable repository artifact is discovered.

Combine or split a remaining issue only when that creates independently acceptable outcomes under [Issue workflow](../10_workflow/issue-workflow.md). File count, line count, elapsed time, or an estimate miss never substitutes for that product boundary. Reusable M0 fixtures or regression cases may move under current production tests; the completed experiment harnesses, manifests, and compatibility entry points otherwise retire from the current tree.

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

#24 host composition + focused implementation siblings + #26 validation design/harness
    --> completed #57 accepted bundle at S2/S3
    --> #75 simplify active validation to production-focused CI
    --> #41 exact-main validation execution and Issue record
    --> validated host revision

validated host revision + #25 CLI contract --> #30 CLI implementation
#30 CLI implementation --> #42 independent read-only smoke
#42 independent read-only smoke --> #33 M1 closure checklist
```

Every currently tracked executable node in this diagram is a direct child of the existing M1 execution parent. Arrows express native dependencies, not parentage. #41 records workflow evidence without a repository PR; #30's ordinary cross-platform integration validates the CLI and Host composition, and #42 exercises that final combination.

## Current tracker disposition

The one-time synchronization described by earlier revisions of this plan is complete and remains available in Git history. It is not an active mutation procedure.

| Issue | Current disposition | M1 ownership |
| --- | --- | --- |
| #17 | Closed architecture evidence | External completed dependency |
| #18 | Deferred distribution/publication work | Outside M1; refine before activation |
| #27 | Deferred process-topology research | Outside M1; refine before activation |
| #29 | Release governance | Outside M1; no current body change required |
| #57 | Closed accepted-bundle certification at exact `S2` and `S3` | Completed direct child |
| #75 | Active; simplify pre-release validation to production-focused CI, blocking #41 until its one correction PR is accepted | Direct child |
| #41 | Blocked pending #75; then tracker-only exact-main Host validation | Direct child |
| #30 | After #41: CLI implementation and integration evidence | Direct child |
| #42 | After #30: independent read-only smoke | Direct child |
| #33 | M1 coordination and closure evidence | Coordination-only parent |

New tracker writes follow the current issue and pre-release engineering rules. They do not reproduce the historical full-manifest synchronization, automatically reopen completed certification work, or create metadata-only revalidation chains.

## Milestone exit evidence

The M1 parent closes only when it links:

- the merged target/observation decision;
- the coordinated current v1 contract behavior;
- production-host implementation revisions;
- production-focused Host validation on the exact accepted revision with passing required jobs;
- CLI contract and implementation;
- independent read-only smoke;
- exact M1 implementation revision and relevant toolchain observations;
- every unresolved non-blocking risk and its owner;
- every decision reconsideration triggered by failed evidence.

Distribution, licensing, proposal generation, patch generation, state, GitHub mutation, and child-process research are not M1 exit evidence.

## Evidence publication and compatibility

- Published fixtures are synthetic and contain no secrets, credentials, private source, private identifiers, or machine-local paths.
- A private downstream smoke publishes only the bounded sanitized attestation defined in W6.
- Historical acceptance evidence uses full commit-SHA links reachable from `main`; living draft guidance may follow the current `main` path.
- The M1 baseline is not described as a released support promise.
- A failed required matrix cell or smoke keeps M1 open while the current owner investigates or corrects it. Amend the milestone contract only when the intended product boundary actually changes.
