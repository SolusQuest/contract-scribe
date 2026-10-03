# Architecture

## Product pipeline

[M7 plan](../90_roadmap/m7-plan.md) owns the adopted automatic-entrypoint, fixed-batch and shared-conversation direction. C1 adopts planning, not executable behavior. The existing protocol documents remain current-state references until their owning code/schema issues update them; completed M4–M6 evidence remains revision-bound.

ContractScribe separates deterministic correctness from model-assisted writing and platform side effects:

```text
repository source + policy
  -> deterministic audit
  -> canonical audit result
  -> deterministic work planning
  -> bounded repository/scope context + target evidence
  -> Documentation Scribe
  -> structured documentation proposal
  -> deterministic XML-documentation patch engine
  -> validated candidate patch
  -> optional platform adapter
  -> branch / commit / pull request
```

The product value is the documentation proposal. The audit and patch engine are the trust boundary that makes the proposal explainable and safe to apply.

Existing component boundaries and candidate future project splits are described in [Project structure](project-structure.md). A future project is created only when executable dependency or authority evidence requires it, not to realize a roadmap name.

## Semantic foundation

M0 established a shared semantic foundation rather than a production audit implementation. Policy expresses normative documentation expectations; Symbol and Evidence Taxonomy describes targets, components, relations, provenance, support state, and bounded evidence; Audit Result combines those inputs into deterministic judgments.

Later stages reuse that language while adding only the capability-specific contracts needed by their executable producer-consumer paths. Exact context, work-planning, campaign-state, publication, and provenance shapes are milestone decisions rather than predeclared contract families. See [Semantic foundation](semantic-foundation.md).

## Component boundaries

### Deterministic audit

The audit owns repository-root resolution, explicit input selection, SDK/MSBuild discovery, Roslyn loading, target classification, documentation observation, policy evaluation, bounded evidence, canonical audit-result production, diagnostics, cancellation, and atomic result publication.

It has no provider dependency, model secret, GitHub write token, or declared network-dependent operation. It does not modify source or project files.

ADR 0001 selects the framework-dependent semantic execution baseline. [ADR 0002](decisions/0002-process-topology.md) selects one in-process runtime per audit for M1.

### Work planner and campaign state

Core owns platform-neutral work ordering, state transitions and resource accounting. The current M4 contracts describe the implemented snapshot-scoped plan/checkpoint/reducer. M7 selects a fixed semantic subset from the complete ordered plan, shares renewable invocation resources across all targets/retries, and makes campaign aggregate caps optional/default unlimited. Stable identity and complete recovery facts prevent duplicate or incompatible continuation; schemas and exact layouts belong to their implementing issues.

M7's consumer coordination ref is the complete recovery authority, with exact-predecessor CAS and authenticated readback. Diagnostic artifacts and GitHub Issues are not the state model. CLI authenticates ledger/PR lifecycle facts before new Audit/plan discovery or provider dispatch; compatible recovery may freshly load Roslyn to reconstruct and validate already accepted results without selecting new targets or regenerating proposals.

### Documentation Scribe

The Documentation Scribe is the project-owned, narrow model-assisted role. The current runtime accepts one selected target and terminates on a structured proposal/skip. M7 instead provides the complete selected batch manifest to one bounded conversation, validates and acknowledges incremental per-target/small-batch submissions, and checks final coverage deterministically. Submission does not itself imply conversation completion, patch acceptance or publication.

It has no shell, arbitrary file edit, GitHub mutation, web search, subagent, or automatic-merge capability. A model runtime adapter handles provider transport, but provider behavior does not own the product contracts or tool policy.

Repository-entrypoint discovery, nested-instruction applicability, evidence selection, and target grouping are deterministic runtime behavior rather than separate model agents. The same Scribe may complete semantic context routing with read-only tools. M7 reuses bounded evidence inside the selected conversation while preserving each target's instruction/style authority; it does not restore complete hidden conversations or reasoning across Actions or automatically compact context.

M3 selects the smallest provider transport and bounded evaluation set that can validate the executable Scribe path. Provider names, compatibility corpora, normalization formats, and prompt-prefix mechanisms remain candidate implementation details; provider cache availability is never correctness state.

See [Documentation Scribe](documentation-scribe.md) and [Scribe context and prompt economics](scribe-context-and-prompt-economics.md).

### Patch engine

The implemented patch engine receives a validated Patch Request and its matching classified repository session, resolves the exact selected declaration through Roslyn, renders XML documentation, applies it to an isolated candidate workspace, then rereads and validates the complete candidate before returning a Patch Validation Result. Acceptance is linearized only after a final original-root rebind and terminal candidate capture; only an accepted result carries an immutable in-process candidate capability, and no staging path or writer crosses the public boundary.

The model never owns a source diff. Source writes happen only through the patch engine after the target, source revision, documentation structure, and patch invariants are validated.

See [Documentation patch boundary](documentation-patch-boundary.md).

### GitHub adapter

`ContractScribe.GitHub` is the current production GitHub network/credential authority. M5-R2 (#159) adds the executable bounded transport with the single `ContractScribe.GitHub -> ContractScribe.Core` product edge and .NET HTTP/JSON only; it adds no package and migrates no existing source. GitHub-to-Roslyn, Agent, Patching, CLI, provider, shell, and test-project dependencies are forbidden. The audit, Core, model runtime and patch engine never receive its credential or transport capability. `GitHubArchitectureTests` and `GitHubApiClientTests` pin this boundary and its synthetic behavior.

The closed internal factory receives a Core-validated publication authority and an already-supplied opaque credential. It owns fixed GitHub REST/GraphQL endpoints, bounded typed request/response parsing, complete PR pagination, permission/rate observations, and conservative uncertainty handling. One fixed single-entry `updateRefs` mutation is the only ref write; the initial surface has no REST ref writes or Issues/comments endpoint. All mutations require owner readback even after an acknowledgement and are never automatically replayed. Only the private default-inert, reflection-registered numeric-loopback test hook can select synthetic transport, and it rejects every non-placeholder credential before use.

R3-R6 own coordination/content/PR reconciliation and publication orchestration over the R1 Core contracts; R2 does not decide remote ownership, operation transitions, legal replay or active-PR uniqueness. There is no public adapter API or CLI project edge in R2. CLI remains the sole production composition root; H2 owns the later CLI edge, single credential resolution and process startup-hook bridge. H3 owns live credential/platform proof. Scripted transport tests are not that proof. See [Security boundary](security-boundary.md#implemented-github-transport-boundary-m5-r2) for exact bounds, failure privacy, and recovery-context rules.

The workflow creates drafts and never merges automatically. M7 adopts at most one PR per fixed batch and one active PR per campaign; publication seals the validated subset and disallows further target append to draft or ready PRs. Open same-base PRs await review; open old-base PRs pause. Closed-unmerged on the same base pauses even after configuration changes; a different HEAD with no active PR permits fresh Audit. Ownership loss, conflicts, human changes and ambiguous active work pause. Lost responses recover only the exact original operation/PR. The complete precedence is owned by the M7 plan.

See [Campaign and GitHub workflow](campaign-and-github-workflow.md).

### GitHub Action wrapper

The Action exposes caller-owned scheduling and manual invocation, configures the selected payload and provider adapter, applies concurrency controls, and binds wrapper provenance to the exact payload identity. It does not weaken any core boundary.

The wrapper is not a second product runtime. It is a thin, non-authoritative host that invokes the production .NET CLI. A composite action is sufficient when payload acquisition and supported-runner invocation remain simple. A small TypeScript-to-JavaScript wrapper is permitted only when host concerns justify it; it may normalize Action inputs and outputs, acquire the payload, reject unsupported runners, propagate cancellation, and report results, but it does not implement campaign, ledger, proposal, patch, or GitHub publication rules. When selected, the host may receive and forward explicitly allowlisted credentials to the CLI, so it is inside the credential-handling boundary; it cannot interpret, persist, log, or independently use them.

[ADR 0004](decisions/0004-initial-runner-platform-support.md) selects GitHub-hosted Ubuntu x64 as the sole required M1-M5 pre-release runner and the planned initial M6 target, not as a released support claim. The initial repository boundary requires caller-prepared prerequisites and design-time MSBuild/Roslyn loading to succeed on that runner. Native-Windows-only workloads, targets, tooling, filesystem behavior, process behavior, and host assumptions are outside the boundary.

[ADR 0005](decisions/0005-d2-dll-payload-archive.md) selects D2 — a framework-dependent portable-DLL archive invoked as `dotnet <dll>` — as the payload channel for pre-release validation, and freezes its publish, archive, manifest, safe-install, pinning, update, rollback, and cleanup contract plus the producer/consumer packed-evidence path. It remains test-only output: no public release or installable support claim.

## Capability and authority matrix

| Component | Provider network | GitHub credential handling | Source write | Canonical output |
| --- | --- | --- | --- | --- |
| Deterministic audit | No | No | No | Audit result |
| Work planner/state core | No by default | No | No | Work plan and state checkpoint |
| Documentation Scribe | Provider transport selected by M3 evidence | No | No | Structured proposal or skip |
| Patch engine | No | No | Candidate workspace only | Patch validation result |
| GitHub adapter | GitHub only | Consumes and uses token for GitHub mutation | Proposal branch/worktree only | Publication record |
| Action wrapper | Payload acquisition only | Receives and forwards allowlisted credentials; no independent use | No direct product write | Action outputs and summary |

The table describes ContractScribe-owned behavior. Repository-controlled MSBuild logic executes under the caller's trust model and is not sandboxed by the in-process M1 topology.

The Documentation Scribe must not receive source-write, state-persistence, or publication authority. M3 selects the minimum compile-time and composition enforcement supported by the implemented project graph and covers it with negative capability tests; project-reference direction or prompts alone are insufficient. This is an in-process product-capability boundary, not an operating-system sandbox.

## Current implementation status

M0–M6 engineering is complete, including deterministic Audit and Patching, the Core-only read-only Agent, resumable local campaigns, the GitHub adapter, CLI proposal composition, layered consumer configuration and the thin composite Action. [M6 plan](../90_roadmap/m6-plan.md) records exact unpublished internal-candidate qualification and its limits. Current production code still uses single-target Scribe runs, explicit proposal start/resume, current finite configuration ceilings and M5 publication semantics. C1 adopts the M7 plan without implementing its automatic entrypoint, fixed batches, shared conversation, complete remote checkpoint or per-invocation usage outputs. Historical evidence does not authorize a public release or imply those new behaviors exist.

M0 experiment questions, conditions, results, limitations, and exact revisions remain historical evidence. PR #77 removed their preservation tests and historical Roslyn experiment project from ordinary test and solution authority. Issue #75 removed the remaining current-tree runners, manifests, and compatibility paths; concrete production regressions and reusable semantic fixtures live under their current production owners.

The current graph separates `Core`, `Roslyn`, `Patching`, `Agent`, `GitHub`, and `Cli`; all six projects exist because their implemented authority boundaries require them. M5-R1 freezes source-free publication contracts in Core, and M5-R2 implements the Core-only GitHub transport while leaving resource reconciliation and CLI publication composition to their named owners. CLI's current production edges to Agent and Patching do not broaden Agent's Core-only dependency or grant it mutation authority. H2 adds the CLI-to-GitHub composition edge; GitHub still references only Core. Future projects are added only when their implementing milestone demonstrates a real dependency or authority boundary.

## Contract lifecycle

Machine contracts begin as pre-release drafts whose exact semantics are identified by repository revision. Milestone closure records historical evidence for that revision without creating an active compatibility state. The first downstream-consumable release creates the external compatibility freeze. See [Contract lifecycle](../00_project/contract-lifecycle.md).
