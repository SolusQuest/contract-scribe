# Architecture

## Product pipeline

ContractScribe separates deterministic correctness from model-assisted writing and platform side effects:

```text
repository snapshot + policy
  -> deterministic audit
  -> canonical audit result
  -> deterministic work planning
  -> repository/scope context snapshot + bounded target evidence
  -> Documentation Scribe
  -> structured documentation proposal
  -> deterministic XML-documentation patch engine
  -> validated candidate patch
  -> optional platform adapter
  -> branch / commit / pull request
```

The product value is the documentation proposal. The audit and patch engine are the trust boundary that makes the proposal explainable and safe to apply.

Component boundaries are mapped to incrementally created C# projects in [Project structure](project-structure.md). The target project graph follows dependency and authority boundaries, not milestone names.

## Semantic foundation

M0 established a shared semantic foundation rather than a production audit implementation. Policy expresses normative documentation expectations; Symbol and Evidence Taxonomy describes targets, components, relations, provenance, support state, and bounded evidence; Audit Result combines those inputs into deterministic judgments.

Later stages reuse that language while adding capability-specific contracts for work planning, context, style, proposals, patches, campaign state, and publication. No single artifact replaces snapshot, contract-baseline, source, work-plan, and run provenance. See [Semantic foundation](semantic-foundation.md).

## Component boundaries

### Deterministic audit

The audit owns repository-root resolution, explicit input selection, SDK/MSBuild discovery, Roslyn loading, target classification, documentation observation, policy evaluation, bounded evidence, canonical audit-result production, diagnostics, cancellation, and atomic result publication.

It has no provider dependency, model secret, GitHub write token, or declared network-dependent operation. It does not modify source or project files.

ADR 0001 selects the framework-dependent semantic execution baseline. [ADR 0002](decisions/0002-process-topology.md) selects one in-process runtime per audit for M1.

### Work planner and campaign state

The planner converts a canonical audit result into a stable ordered work plan. It applies documentation-block, file, patch-size, provider-request, total/uncached-token, cost, attempt, and wall-clock budgets before work is published.

Campaign state records snapshot-scoped work-plan identity, batch progress, retry state, proposal-branch and pull-request-generation identity, and lineage across base commits. Cursors, checkpoints, and replay keys never cross snapshot identities. The model is platform-neutral. A GitHub Issue is an adapter surface, not the state model.

### Documentation Scribe

The Documentation Scribe is the project-owned, narrow model-assisted role. Its Scribe Runtime receives an allowlisted context pack, may call bounded semantic and repository-read tools, and terminates by submitting a structured documentation proposal or a structured skip.

It has no shell, arbitrary file edit, GitHub mutation, web search, subagent, or automatic-merge capability. A model runtime adapter handles provider transport, but provider behavior does not own the product contracts or tool policy.

Repository-entrypoint discovery, nested-instruction applicability, context identity, and target grouping are deterministic runtime behavior rather than separate model agents. The same Scribe may complete semantic context routing with read-only tools. Independent Scribe runs reuse immutable repository and scope context snapshots and stable request prefixes without sharing conversation history.

M3 selects the smallest provider transport and bounded evaluation set that can validate the executable Scribe path. Provider names, compatibility corpora, normalization formats, and prompt-prefix mechanisms remain candidate implementation details; provider cache availability is never correctness state.

See [Documentation Scribe](documentation-scribe.md) and [Scribe context and prompt economics](scribe-context-and-prompt-economics.md).

### Patch engine

The patch engine receives a validated structured proposal, resolves the exact selected declaration through Roslyn, renders XML documentation, applies it to a candidate workspace, and proves that no forbidden change occurred.

The model never owns a source diff. Source writes happen only through the patch engine after the target, source revision, documentation structure, and patch invariants are validated.

See [Documentation patch boundary](documentation-patch-boundary.md).

### GitHub adapter

The GitHub adapter reconciles campaign state with a GitHub Issue, branch, commit, and pull request. It is the only product component that may consume or use a GitHub write token to mutate GitHub.

The initial workflow permits at most one active bot-owned proposal pull request per campaign at a time. The adapter creates it as draft; human promotion to ready-for-review keeps it active but makes it ineligible for further batch appends. Same-snapshot runs may append bounded batches only while that draft's base, ownership, and state remain safe. After a merged or otherwise terminal predecessor, remaining work on a new snapshot may create a new numbered draft generation. Merge, base drift, conflicts, human edits, closure without merge, or corrupted ledger state trigger explicit reconciliation rather than silent mutation.

See [Campaign and GitHub workflow](campaign-and-github-workflow.md).

### GitHub Action wrapper

The Action exposes caller-owned scheduling and manual invocation, configures the selected payload and provider adapter, applies concurrency controls, and binds wrapper provenance to the exact payload identity. It does not weaken any core boundary.

The wrapper is not a second product runtime. It is a thin, non-authoritative host that invokes the production .NET CLI. A composite action is sufficient when payload acquisition and cross-platform invocation remain simple. A small TypeScript-to-JavaScript wrapper is permitted only when host concerns justify it; it may normalize Action inputs and outputs, acquire the payload, propagate cancellation, and report results, but it does not implement campaign, ledger, proposal, patch, or GitHub publication rules. When selected, the host may receive and forward explicitly allowlisted credentials to the CLI, so it is inside the credential-handling boundary; it cannot interpret, persist, log, or independently use them.

## Capability and authority matrix

| Component | Provider network | GitHub credential handling | Source write | Canonical output |
| --- | --- | --- | --- | --- |
| Deterministic audit | No | No | No | Audit result |
| Work planner/state core | No by default | No | No | Work plan and state checkpoint |
| Documentation Scribe | Explicit OpenAI-compatible provider only | No | No | Structured proposal or skip |
| Patch engine | No | No | Candidate workspace only | Patch validation result |
| GitHub adapter | GitHub only | Consumes and uses token for GitHub mutation | Proposal branch/worktree only | Publication record |
| Action wrapper | Payload acquisition only | Receives and forwards allowlisted credentials; no independent use | No direct product write | Action outputs and summary |

The table describes ContractScribe-owned behavior. Repository-controlled MSBuild logic executes under the caller's trust model and is not sandboxed by the in-process M1 topology.

The Documentation Scribe's lack of product mutation capabilities is enforced by more than project-reference direction: mutation-capable Core ports are internal and production-friend-visible only to Cli, `ContractScribe.Agent` is excluded from that allowlist, the CLI composition contract adapts infrastructure operations without injecting those capabilities into Agent, and architecture tests inspect both API surfaces and negative compilation fixtures. This is an in-process product-capability boundary, not an operating-system sandbox.

## Current implementation status

M0 contracts and execution experiments are complete. The repository still lacks the production audit host, production CLI audit command, patch engine, Documentation Scribe, campaign state, GitHub adapter, and consumable Action.

The M0 experiment runner and `semantic-payload.json` remain test-only. They are evidence inputs, not production APIs or migration predecessors.

The current candidate M5 graph separates `Core`, `Roslyn`, `Patching`, `Agent`, `GitHub`, and `Cli`. Existing project boundaries are authoritative; future projects are added only when their implementing milestone demonstrates a real dependency or authority boundary. M1 does not create empty projects for later work.

## Contract lifecycle

Machine contracts begin as pre-release drafts whose exact semantics are identified by repository revision. Milestone closure records historical evidence for that revision without creating an active compatibility state. The first downstream-consumable release creates the external compatibility freeze. See [Contract lifecycle](../00_project/contract-lifecycle.md).
