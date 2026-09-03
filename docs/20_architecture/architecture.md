# Architecture

## Product pipeline

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

M4 will select deterministic work ordering and the independent work, provider, patch, attempt, and time budgets required by its executable workflow.

Any resumable state remains platform-neutral, rejects a stale or incompatible repository base, and prevents duplicate accepted work. Snapshot, work-plan, batch, cursor, checkpoint, replay, branch, and generation representations remain M4/M5 candidates until a reproduced continuation or idempotency failure demonstrates the minimum necessary shape. A GitHub Issue is an adapter surface, not the state model.

### Documentation Scribe

The Documentation Scribe is the project-owned, narrow model-assisted role. Its Scribe Runtime receives an allowlisted context pack, may call bounded semantic and repository-read tools, and terminates by submitting a structured documentation proposal or a structured skip.

It has no shell, arbitrary file edit, GitHub mutation, web search, subagent, or automatic-merge capability. A model runtime adapter handles provider transport, but provider behavior does not own the product contracts or tool policy.

Repository-entrypoint discovery, nested-instruction applicability, evidence selection, and target grouping are deterministic runtime behavior rather than separate model agents. The same Scribe may complete semantic context routing with read-only tools. Independent runs do not share mutable conversation history; M3 selects any context identity, storage, or prompt-prefix mechanism only when executable evidence needs it.

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

The initial workflow permits at most one compatible active bot-owned proposal pull request for current work, creates proposals as drafts, never merges automatically, and refuses unsafe mutation after ownership mismatch, base drift, conflicts, ambiguous active work, or human modification. M5 selects any checkpoint, ledger, generation, operation-ID, append, or continuation representation from reproduced retry and idempotency failures.

See [Campaign and GitHub workflow](campaign-and-github-workflow.md).

### GitHub Action wrapper

The Action exposes caller-owned scheduling and manual invocation, configures the selected payload and provider adapter, applies concurrency controls, and binds wrapper provenance to the exact payload identity. It does not weaken any core boundary.

The wrapper is not a second product runtime. It is a thin, non-authoritative host that invokes the production .NET CLI. A composite action is sufficient when payload acquisition and supported-runner invocation remain simple. A small TypeScript-to-JavaScript wrapper is permitted only when host concerns justify it; it may normalize Action inputs and outputs, acquire the payload, reject unsupported runners, propagate cancellation, and report results, but it does not implement campaign, ledger, proposal, patch, or GitHub publication rules. When selected, the host may receive and forward explicitly allowlisted credentials to the CLI, so it is inside the credential-handling boundary; it cannot interpret, persist, log, or independently use them.

[ADR 0004](decisions/0004-initial-runner-platform-support.md) selects GitHub-hosted Ubuntu x64 as the sole required M1-M5 pre-release runner and the planned initial M6 target, not as a released support claim. The initial repository boundary requires caller-prepared prerequisites and design-time MSBuild/Roslyn loading to succeed on that runner. Native-Windows-only workloads, targets, tooling, filesystem behavior, process behavior, and host assumptions are outside the boundary.

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

M0 contracts and experiments are complete. M1 has implemented the production Roslyn/MSBuild loading, classification, documentation observation, policy/evidence, canonical result, atomic `ProductionAuditHost`, and production CLI path, and #75 removed the retired validation and experiment machinery. Completed #41 and #30 remain revision-bound implementation and validation evidence; #42 is the only remaining executable M1 step before #33 closure. M2 has implemented the `ContractScribe.Patching` boundary, exact resolution and rendering, complete isolated candidate materialization, session-bound Roslyn and generator validation, root-state fail-closed results, and immutable accepted-candidate handoff. M3 now has the Core-only Agent runtime, bounded context and repository/Roslyn read tools, provider transport, and an internal CLI one-target Scribe-to-patch composition seam. The seam accepts only an opaque selected-audit capability bound to the exact live M1 session, injects a closed read-only tool registry into Agent, revalidates the terminal proposal, and delegates candidate creation exclusively to the existing M2 engine. It adds no public CLI command, live-provider claim, campaign state, or write-back to the original checkout. This implementation status does not authorize later milestone behavior; evaluation, campaign state, GitHub adaptation, and the consumable Action remain later work.

M0 experiment questions, conditions, results, limitations, and exact revisions remain historical evidence. PR #77 removed their preservation tests and historical Roslyn experiment project from ordinary test and solution authority. Issue #75 removed the remaining current-tree runners, manifests, and compatibility paths; concrete production regressions and reusable semantic fixtures live under their current production owners.

The current graph separates `Core`, `Roslyn`, `Patching`, `Agent`, `GitHub`, and `Cli`; all six projects exist because their implemented authority boundaries require them. M5-R1 freezes source-free publication contracts in Core, and M5-R2 implements the Core-only GitHub transport while leaving resource reconciliation and CLI publication composition to their named owners. CLI's current production edges to Agent and Patching do not broaden Agent's Core-only dependency or grant it mutation authority. GitHub has no CLI edge until H2. Future projects are added only when their implementing milestone demonstrates a real dependency or authority boundary.

## Contract lifecycle

Machine contracts begin as pre-release drafts whose exact semantics are identified by repository revision. Milestone closure records historical evidence for that revision without creating an active compatibility state. The first downstream-consumable release creates the external compatibility freeze. See [Contract lifecycle](../00_project/contract-lifecycle.md).
