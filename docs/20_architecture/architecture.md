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

The patch engine receives a validated structured proposal, resolves the exact selected declaration through Roslyn, renders XML documentation, applies it to a candidate workspace, and proves that no forbidden change occurred.

The model never owns a source diff. Source writes happen only through the patch engine after the target, source revision, documentation structure, and patch invariants are validated.

See [Documentation patch boundary](documentation-patch-boundary.md).

### GitHub adapter

The GitHub adapter reconciles campaign state with a GitHub Issue, branch, commit, and pull request. It is the only product component that may consume or use a GitHub write token to mutate GitHub.

The initial workflow permits at most one compatible active bot-owned proposal pull request for current work, creates proposals as drafts, never merges automatically, and refuses unsafe mutation after ownership mismatch, base drift, conflicts, ambiguous active work, or human modification. M5 selects any checkpoint, ledger, generation, operation-ID, append, or continuation representation from reproduced retry and idempotency failures.

See [Campaign and GitHub workflow](campaign-and-github-workflow.md).

### GitHub Action wrapper

The Action exposes caller-owned scheduling and manual invocation, configures the selected payload and provider adapter, applies concurrency controls, and binds wrapper provenance to the exact payload identity. It does not weaken any core boundary.

The wrapper is not a second product runtime. It is a thin, non-authoritative host that invokes the production .NET CLI. A composite action is sufficient when payload acquisition and cross-platform invocation remain simple. A small TypeScript-to-JavaScript wrapper is permitted only when host concerns justify it; it may normalize Action inputs and outputs, acquire the payload, propagate cancellation, and report results, but it does not implement campaign, ledger, proposal, patch, or GitHub publication rules. When selected, the host may receive and forward explicitly allowlisted credentials to the CLI, so it is inside the credential-handling boundary; it cannot interpret, persist, log, or independently use them.

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

M0 contracts and experiments are complete. M1 has implemented the production Roslyn/MSBuild loading, classification, documentation observation, policy/evidence, canonical result, and atomic `ProductionAuditHost` path, and #75 removed the retired validation and experiment machinery. The remaining M1 path is #41 exact-main validation, #30 production CLI audit command, and #42 read-only smoke. The patch engine, Documentation Scribe, campaign state, GitHub adapter, and consumable Action remain later-milestone work.

M0 experiment questions, conditions, results, limitations, and exact revisions remain historical evidence. PR #77 removed their preservation tests and historical Roslyn experiment project from ordinary test and solution authority. Issue #75 removed the remaining current-tree runners, manifests, and compatibility paths; concrete production regressions and reusable semantic fixtures live under their current production owners.

The current candidate M5 graph separates `Core`, `Roslyn`, `Patching`, `Agent`, `GitHub`, and `Cli`. Existing project boundaries are authoritative; future projects are added only when their implementing milestone demonstrates a real dependency or authority boundary. M1 does not create empty projects for later work.

## Contract lifecycle

Machine contracts begin as pre-release drafts whose exact semantics are identified by repository revision. Milestone closure records historical evidence for that revision without creating an active compatibility state. The first downstream-consumable release creates the external compatibility freeze. See [Contract lifecycle](../00_project/contract-lifecycle.md).
