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

The original product requirement includes different documentation levels, at minimum external API and internal/assembly-visible targets. ADR 0003 accepted the exact semantics. Issue #35 established their first pre-release v1 machine-contract shape, and Issues #55 and #70 record later corrections after implementation exposed classification-origin and lineage inconsistencies. Those exact revisions remain historical evidence rather than active successor authority. The prior M0 shape could not fully express the requirement because:

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

The removed `tests/ContractScribe.Roslyn` assembly remains M0 experiment history in Git. Reusable semantic cases now live under their current contract or production test owner; the production implementation keeps its clean `src` namespace and dependency graph without an experiment compatibility reference.

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

The corrected contract content and Issue #57 Host Validation bundle remain historical evidence at their original revisions, but current pre-release validation is ordinary production-focused CI rather than bundle-authorized or governed by successor-baseline identities. PR #77 removed the release-like Host certification and completed M0 experiment protocols from ordinary CI, solution membership, and test authority. Issue #75 removed their remaining current-tree executables and machine artifacts while retaining direct production execution and integration coverage for #41.

M0, Issue #35, Issue #55, Issue #57, and Issue #70 evidence remains available at the exact revisions that produced it. Historical documents may describe their original acceptance lifecycle; their current-tree harnesses, manifests, allowlists, tombstones, or compatibility modes are not retained solely to keep that history executable.

## Workstreams

### W1 — M1 parent and decision gate — Completed

Issue #33 is the existing M1 coordination parent and owns the remaining dependency graph, exit checklist, closure evidence, and blocker classification without executable work of its own. The completed roadmap design gate remains a historical prerequisite rather than a parent of executable issues.

Existing M1 relationships remain tracker history. New relationships follow the current issue workflow and are added only when they provide coordination beyond milestone membership; release-gate and research work remain outside M1.

[ADR 0003](../20_architecture/decisions/0003-target-profiles-and-documentation-observation.md) accepted the target-surface and direct documentation-observation semantics before their production implementation. No new decision issue or reopen is required for that completed boundary.

### W2 — Pre-release contract completion — Completed

Issues #35, #55, and #70 record the completed contract lineage that established and corrected the current pre-release v1 audit semantics. Their exact accepted revisions remain historical evidence, not active baseline authority.

Later draft corrections update the affected producer-consumer path coherently under the current contract lifecycle. They do not reopen these issues or create a successor baseline merely because implementation changes.

The accepted contract work unblocked the completed classification, policy, evidence, and result implementation in W3.

### W3 — Production audit host — Completed

Closed Issue #24 composed the production audit components into the in-process `ProductionAuditHost`, including cancellation and failure precedence, stale-output invalidation, and atomic canonical-result publication.

The completed production path includes:

1. Production `ContractScribe.Roslyn` explicit repository and solution/project input resolution, Roslyn/MSBuild registration, workspace loading, compilation acquisition, prerequisite behavior, bounded loader diagnostics, reference boundaries, and no automatic discovery or restore.
2. Production target-profile and symbol classification with its required fixtures and contract conformance.
3. Production XML-documentation observation with partial, generated, inherited, malformed, absent, and unavailable fixtures.
4. Policy evaluation and bounded evidence binding against the current v1 contracts.
5. Canonical audit-result aggregation and serialization with cross-contract conformance and fresh-process determinism tests.

These outcomes and their accepted tests are current production inputs to #41; they are not instructions to create new component issues or refocus #24.

The implemented Host distinguishes internally enforceable cooperative bounds from caller- or operating-system-enforced process limits and from limits that the in-process topology cannot guarantee. New calibration work is needed only when current implementation evidence cannot select a required bound.

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

A repository audit found that `.github/workflows/ci.yml` validated the historical Host bundle structurally, performed platform dry-runs, self-tested the harness, and reproduced the completed M0.7 evidence-publication pipeline around the pinned M0.4 experiment. The Host path did not execute the production subject needed by #41, while M0.4/M0.5/M0.7 manifests, scripts, and tests bound development to historical experiment protocols. Both layers added development coupling without proving the current production audit path.

PR #77 removed the obsolete Host bundle validation/dry-run/self-test and historical M0.7 external-checkout, evidence-publication, aggregation, preservation tests, and historical solution authority without refreshing their locks or identities. Issue #75 completed the current-tree cleanup in one pull request: it deleted the Host protected-input manifest, artifact lock, bundle/review identities, review-only transition, certification commands, evidence schemas, manifests, validators, mutation corpus, self-test, aggregation, publication, and public-preparation machinery whose only consumer was the validation product. It also deleted the remaining M0.4/M0.5/M0.7 manifests, compatibility modes, tombstones, and provenance/aggregate scripts. Reusable semantic fixtures moved under the current Taxonomy and Audit Result owners. Ubuntu and Windows retain ordinary exact-head restore, build, product and contract tests, format, and CLI smoke checks, with no post-merge repository mutation or closed-issue reopen.

The Issue #70 contract evidence at exact squash commit `67c149fbc105d2ccae94becd6b2158b68027cbfd` (`C2`) remains immutable history. Issue #75 did not reopen #70, regenerate its manifest identity, or create a successor solely to remove Host lifecycle machinery. It deleted the current-tree #70 manifest and dependencies that existed only to enforce historical certification. Current Host behavior is defined by the production source, contracts, fixtures, and tests at the current revision.

Lower-level Host protocol and M0 experiment documents describe immutable historical machinery at their linked revisions. Their deleted paths, identities, validators, harnesses, and bundle language do not authorize current production execution, refresh work, or #41 evidence claims.

Issue #41 runs the required production validation on exact main and records its run/job links and conclusions in the Issue. GitHub's source, workflow, run, attempt, and required-job facts identify the run. Add one small run-local result envelope or artifact digest only if one job actually transfers a result to another and the transfer cannot be checked safely without it; do not build a separate common/per-cell identity protocol. A later Host change leaves older evidence historically true but requires ordinary current CI before a downstream task consumes the changed revision.

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
| Target/observation decision | Completed direct M1 child | ADR 0003 accepted the implemented semantics; no current decision work |
| Pre-release v1 contract work | Completed direct M1 children | Historical #35/#55/#70 lineage; current corrections update only affected paths |
| Production loading, classification, observation, policy/evidence, and result units | Completed direct sibling M1 children | Current production implementation and accepted tests used by #41 |
| #24 production host | Completed direct M1 child | Current `ProductionAuditHost` execution lifecycle, cancellation/failure precedence, stale-output invalidation, and atomic publication |
| #25 CLI contract | Completed direct M1 child | Accepted CLI contract consumed by remaining #30 implementation |
| #26 Host Validation design and harness | Completed direct M1 child | Historical design and harness evidence at its accepted revision; not current implementation authority |
| #57 Host Validation accepted-bundle promotion | Completed direct sibling M1 child | Historical two-commit acceptance sequence that established exact `S2` and `S3`; not a reusable default for new work |
| #75 pre-release validation simplification | Completed direct sibling M1 child | Removed active Host certification and retired M0 experiment machinery while retaining production-focused CI |
| #41 Host-validation execution | Direct sibling M1 child | Tracker-only exact-main workflow execution and Issue-recorded run/job conclusions by default |
| #30 CLI implementation | Direct M1 child | One focused CLI implementation PR including ordinary Host/CLI integration tests |
| #42 independent read-only smoke | Direct M1 child | One bounded Issue attestation; a repository PR only for an independently useful code or fixture change |
| #17 process topology | External completed architecture dependency | No new executable M1 work unless an ADR reconsideration trigger creates a separate issue |

Issues #18, #27, and #29 belong to release or research tracks and are not M1 children.

## Refinement status and remaining review boundaries

The one-time tracker synchronization and Host Validation certification sequence are complete. Historical issue counts, changed-line forecasts, and the two-commit #57 acceptance sequence are not requirements for remaining M1 work.

The remaining critical path is intentionally small:

- #41: one tracker-only exact-main Host validation run by default;
- #30: one focused CLI implementation and cross-platform integration pull request by default;
- #42: one focused independent smoke recorded through a bounded Issue attestation by default;
- #33: coordination and closure evidence only, with no executable pull request of its own unless a separately acceptable repository artifact is discovered.

Combine or split a remaining issue only when that creates independently acceptable outcomes under [Issue workflow](../10_workflow/issue-workflow.md). File count, line count, elapsed time, or an estimate miss never substitutes for that product boundary. Reusable M0 fixtures or regression cases may move under current production tests; the completed experiment harnesses, manifests, and compatibility entry points otherwise retire from the current tree.

## Dependency graph

```text
completed target/observation decision
    +--> completed pre-release v1 contract work
    |        +--> completed production implementation siblings
    |        |        +--> completed #24 host composition
    |        |
    |        +--> completed historical #26 validation protocol
    |
    +--> completed #25 CLI contract

completed #24 host composition + production implementation siblings + #26 validation design/harness
    --> completed #57 accepted bundle at S2/S3
    --> completed #75 retired-machinery deletion and production-focused validation
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
| #75 | Completed pre-release validation simplification and retired-machinery deletion | Completed direct child |
| #41 | Next; tracker-only exact-main Host validation | Direct child |
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
