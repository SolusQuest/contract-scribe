# Roadmap

## Product direction

ContractScribe is built as six ordered product milestones after the completed M0 validation:

```text
M1 deterministic audit
  -> M2 deterministic documentation patch engine
  -> M3 Documentation Scribe
  -> M4 resumable campaign orchestration
  -> M5 GitHub proposal workflow
  -> M6 consumable GitHub Action release
```

Release governance, payload distribution, and deferred topology research are separate tracks. They must not block source-based product work that does not depend on them, but their explicit gates apply before the first consumable release.

The product is not complete when it can only report missing documentation. Its user value is evidence-grounded documentation writing. The earlier milestones exist to make that writing explainable, bounded, and safe to publish.

## Cross-milestone rules

- Deterministic audit stays independent of a model provider, provider secret, GitHub write token, and declared network-dependent operation.
- The Documentation Scribe is a self-developed, narrow model-assisted role with bounded read-only tools and structured output. It is not a general coding-agent dependency.
- Project-context selection and repository confinement are deterministic product behavior. The exact snapshot, manifest, grouping, and cache identities remain M3 implementation decisions until executable evidence requires them.
- M3 selects the smallest provider transport and evaluation set that can validate the current Scribe path. Provider names, compatibility corpora, normalization formats, and cache mechanisms are not frozen by the pre-M3 roadmap.
- Provider caching is never correctness or resume state. Observe cost and uncached input to the degree needed for the current product decision without creating a permanent economics protocol in advance.
- A model never emits or applies the source diff. The deterministic M2 patch engine owns every source modification.
- Resumable work state must not depend on GitHub-specific semantics, but the exact state, identity, ledger, cursor, and adapter shapes are refined when M4 has an executable workflow.
- Scheduling is caller-owned. Provider-specific batch or pricing policy stays in the provider adapter and run provenance.
- Pre-release contracts follow [Contract lifecycle](../00_project/contract-lifecycle.md). Draft revisions do not require version-number churn when no incompatible consumers need to coexist.
- Uncertainty, stale input, invalid model output, unsafe patch, corrupted state, and ambiguous GitHub ownership fail closed.
- Published fixtures are synthetic. Private downstream validation publishes only bounded sanitized evidence without repository identity, source, private paths, or raw logs.

## Status overview

| Track | Status | Primary outcome |
| --- | --- | --- |
| M0 — Product, contracts, and architecture validation | Done | Evidence-backed execution and topology inputs |
| M1 — Deterministic audit MVP | Current | Production read-only audit and CLI |
| M2 — Deterministic XML documentation patch engine | Implemented; closure pending | Safe documentation-only source changes |
| M3 — Documentation Scribe and proposal engine | Planned | Useful evidence-grounded structured documentation |
| M4 — Resumable campaign orchestration | Planned | Deterministic budgeting, resume, and lineage |
| M5 — GitHub proposal workflow | Planned | Idempotent ledger, branch, commit, and draft PR workflow |
| M6 — GitHub Action release | Planned | Downstream-consumable Action and payload |
| Release Gate — Governance | Open | License and contribution disposition |
| Release Gate — Payload Distribution | Open | Selected payload channel and ADR |
| Research — Deferred Process Topology | Triggered only | Eligibility evidence for child-process alternatives |

## Production-project evolution

The current candidate M5 product graph separates six production concerns. Existing projects remain authoritative; a future project is added only when its owning milestone demonstrates a real dependency or authority boundary:

| Milestone | Project change |
| --- | --- |
| M0 | Minimal `ContractScribe.Core` and `ContractScribe.Cli`; Roslyn work remains test-only experiment evidence. |
| M1 | Add production `ContractScribe.Roslyn`. |
| M2 | Added `ContractScribe.Patching` for the isolated candidate-write, validation, and accepted-candidate authority boundary. |
| M3 | Candidate: add `ContractScribe.Agent` if the read-only Scribe runtime boundary meets the split thresholds. |
| M4 | Candidate: keep platform-neutral campaign behavior in `ContractScribe.Core`; no new milestone-named project by default. |
| M5 | Candidate: add `ContractScribe.GitHub` if platform-mutation isolation meets the split thresholds. |
| M6 | Add the selected Action wrapper and release artifacts; no new C# project without an observed split need. |

The candidate graph is not created up front or treated as a project-name contract. Fixture, experiment, integration-test, and optional evaluation projects are classified separately. See [Project structure](../20_architecture/project-structure.md).

## M0 — Product, contracts, and architecture validation — Done

### Goal

Establish product boundaries, provisional contract baselines, and an evidence-backed Roslyn/MSBuild execution direction before production audit implementation.

### Completed outcomes

- Policy/Configuration v1, Audit Result v1, and Symbol and Evidence Taxonomy v1 have normative docs, machine schemas or registries, synthetic fixtures, and conformance oracles.
- The framework-dependent Roslyn/MSBuild semantic path was validated on the primary synthetic fixture.
- The tested historical Native AOT profile produced a bounded not-feasible result without becoming a general impossibility claim.
- ADR 0001 selected the framework-dependent semantic execution baseline.
- M0.7 independently validated that baseline on a separate synthetic repository shape.
- ADR 0002 selected the in-process M1 production topology and defined reconsideration triggers.

### Enduring semantic assets

M0 established a [semantic foundation](../20_architecture/semantic-foundation.md), not a production audit implementation. Policy provides the normative expectation, Symbol and Evidence Taxonomy provides the descriptive target and bounded-evidence vocabulary, and Audit Result provides the deterministic judgment. Canonical encoding, ordering, identity, provenance, public fixtures, and conformance oracles make that language executable across fresh processes and later milestones.

M1 through M6 reuse the foundation while owning capability-specific contracts for production observation, work planning, context, style, proposals, patches, campaign state, publication, and release. Audit evidence is not automatically a complete Scribe context pack; `SymbolRef` is compilation-context-bound rather than a permanent cross-revision entity identity; and canonical Audit Result content does not replace snapshot-scoped execution identity.

### Contract interpretation

M0 created historical evidence at an exact revision, not an active compatibility state or external freeze. Current pre-release v1 contracts may be completed through one coherent affected-path change. M0 evidence remains bound to the revision that produced it without requiring a successor baseline.

See [Initial issue plan](initial-issue-plan.md) for the completed M0 graph.

## M1 — Deterministic audit MVP

### Goal

Deliver a production read-only audit host and CLI that load an explicit C# project or solution, classify documentation targets, evaluate policy, observe XML documentation, and write a canonical audit result.

### Scope

- Complete the pre-release v1 contract set for the initial target surface and documentation-observation semantics.
- Support at least an external API profile and an assembly-visible profile that includes internal targets.
- Implement the framework-dependent, in-process Roslyn/MSBuild production host selected by ADR 0002.
- Implement deterministic classification, policy evaluation, evidence binding, audit-result aggregation, diagnostics, cancellation, and atomic output.
- Define and implement the M1 CLI.
- Use GitHub-hosted Ubuntu x64 as the sole required pre-release validation runner, then run an independent real-world or downstream read-only smoke on a target satisfying the accepted repository boundary.

### Exit criteria

- External API and assembly-visible target profiles have executable fixtures.
- Effective accessibility, containing-type reachability, symbol-kind selection, partial/generated behavior, and documentation-observation semantics are explicit.
- Required, optional, forbidden, compliant, violation, unavailable, and skipped outcomes are covered.
- Fresh processes produce byte-identical canonical results for fixed inputs.
- Every ordinary audit outcome is explainable through policy and bounded evidence references.
- Audit execution requires no provider, model secret, GitHub write token, automatic restore, or declared network-dependent operation.
- ContractScribe does not modify source or project files.
- Failure, cancellation, diagnostics, stale-output invalidation, and atomic publication behavior are executable.
- CLI contract, implementation, and integration validation are complete.
- Ordinary CI succeeds on the exact Host/CLI revision consumed by the next step, and the independent smoke records only its bounded attestation.
- An independent real-world or private downstream repository completes a read-only smoke with only a bounded sanitized attestation published upstream.

### Non-goals

- Documentation proposal generation.
- Source modification.
- Campaign state or GitHub side effects.
- Consumer-facing packaging or release-channel selection.
- Child-process production topology.
- A general claim of sandboxing untrusted MSBuild content.

### Plan

See [M1 plan](m1-plan.md).

## M2 — Deterministic XML documentation patch engine

### Goal

Given a validated structured documentation proposal, render and validate an XML-documentation-only source patch without using a model.

### Scope

- Patch request and patch-validation contract.
- Roslyn declaration resolution and stale-source detection.
- Deterministic XML rendering, escaping, tag ordering, indentation, and line-ending preservation.
- Candidate-workspace application.
- Syntax, token, symbol, signature, target, encoding, and idempotency validation.
- Adversarial fixtures for comments, preprocessors, partial declarations, generated source, records, interfaces, overrides, and multi-target files.

### Implementation status

The provider-independent M2 execution path is implemented: it consumes the validated Patch Request and matching classified session, composes the accepted M2-E1 baseline/resolution/rendering/candidate handoff, rereads the complete candidate from disk, reconstructs every represented Roslyn source/additional-file/analyzer-config context, reruns source generators, validates exact documentation-only bytes and semantic invariants, performs the final original-root rebind, and returns a bounded Patch Validation Result. Only acceptance returns an immutable managed candidate capability. This records executable implementation status and does not itself close M2, its coordination parent, or any later milestone.

### Exit criteria

- Every accepted change maps to a selected target and structured proposal.
- Pre/post non-documentation tokens are identical.
- Signatures, modifiers, attributes, constraints, symbols, tests, project files, and unselected documentation remain unchanged.
- Reapplying an accepted patch produces no diff.
- Formatting allowance is bounded and documented.
- Stale, ambiguous, malformed, unsupported, or unsafe input fails closed.
- Deterministic byte/text fixtures on GitHub-hosted Ubuntu x64 cover LF, CRLF, encoding, BOM, newline preservation, and documentation-block replacement without claiming native-Windows filesystem evidence.

### Non-goals

- Model calls or prompt design.
- Evidence discovery beyond validating proposal references.
- Campaign batching.
- GitHub writes.

See [Documentation patch boundary](../20_architecture/documentation-patch-boundary.md).

## M3 — Documentation Scribe and proposal engine

### Goal

Deliver the self-developed Documentation Scribe, which converts audit targets, bounded evidence, and a style profile into useful structured documentation proposals.

### Scope

- The minimum draft contracts needed to request, validate, and apply a structured documentation proposal.
- Deterministic repository instruction discovery and bounded repository-confined evidence access.
- One project-owned, read-only Scribe loop with a small semantic and repository-read tool set.
- One provider transport and evaluation set selected during M3 refinement from current executable evidence.
- Bounded attempts, time, input, output, tool use, and cost where the selected provider exposes usable observations.
- Prompt-injection resistance and public/private data boundaries.
- Documentation-quality and practical cost evaluation on a bounded corpus.
- M2 patch-engine integration as the only source-write path.

Exact context, snapshot, route, prompt-prefix, provider-normalization, identity, and storage formats are M3 decisions rather than pre-M3 roadmap contracts.

### Candidate initial tool set

- `get_symbol_context`
- `get_related_symbols`
- `find_symbol_usages`
- `find_tests_for_symbol`
- `get_symbol_source`
- `read_excerpt`
- `search_text`
- `list_files`
- `submit_documentation_proposal`

### Exit criteria

- The Scribe has no shell, arbitrary edit, GitHub mutation, web search, or subagent capability.
- The configured entrypoint and applicable nested instruction stack are loaded deterministically before the Scribe run.
- Repository and semantic reads are bounded and confined; cycles, escapes, stale files, and exhausted budgets fail closed.
- Repository instruction, project documentation, and source evidence remain distinct authority classes.
- Independent runs do not share hidden reasoning, mutable conversation history, or provider-owned correctness state.
- Tool, input/output, cost when observable, attempt, and wall-clock budgets are enforced.
- Invalid tool calls, invalid structured output, unsupported targets, and insufficient evidence return stable fail-closed outcomes.
- A deterministic test runtime proves orchestration and retry mechanics.
- At least one selected provider/model executes the bounded evaluation corpus through the production request and tool loop.
- Compatibility and support statements are limited to the executed provider/model corpus and observed behavior.
- The implementation records only the provenance needed to reproduce or interpret the current evaluation claim.
- Accepted model output passes the structured proposal validator and M2 patch engine.
- Evaluation records proposal validity, evidence support, patch acceptance, material quality failures, practical cost/latency observations, and sensitive-data results without defining a permanent metrics protocol in advance.

### Non-goals

- A reusable general coding-agent framework.
- A third-party coding-agent runtime as a production dependency.
- A separate context-curator agent, parent/child agent fork protocol, or shared hidden memory.
- A universal provider compatibility matrix or permanent provider exclusivity promise.
- Long-lived conversational sessions.
- Direct source or GitHub mutation.

See [Documentation Scribe](../20_architecture/documentation-scribe.md) and [Scribe context and prompt economics](../20_architecture/scribe-context-and-prompt-economics.md).

## M4 — Resumable campaign orchestration

### Goal

Create deterministic work planning, multi-dimensional budgets, resume, retry, and campaign lineage independently of GitHub.

### Scope

- Deterministic selection and ordering of documentation work from one repository revision.
- Independently enforced work, provider, patch, attempt, and time budgets required by the current workflow.
- Bounded retry, resume, and reconciliation after interruption or base change.
- Platform-neutral state sufficient to continue safely without duplicating accepted work.
- Crash, replay, and partial-failure behavior.

Exact snapshot, work-plan, batch, campaign, cursor, checkpoint, and identity formats are selected during M4 refinement from the minimum state needed by the executable workflow.

### Exit criteria

- The same accepted input and planning policy selects the same ordered work within one repository revision.
- The budgets required by the implemented workflow are independently enforced.
- A crash, retry, or replay does not duplicate accepted work or lose committed progress.
- Continuation rejects stale or incompatible repository state rather than applying work to the wrong base.
- Terminal target failure, retryable provider failure, budget exhaustion, and supersession have tested behavior.
- State contains no private source, private or complete prompt content, raw provider response, or full diff.
- No GitHub mutation is required to validate the core.

## M5 — GitHub proposal workflow

### Goal

Implement an idempotent GitHub workflow that publishes bounded proposal work to a human-reviewed pull request without duplicate or conflicting mutations.

### Scope

- The minimum durable state needed to reconcile GitHub Issues, branches, commits, and pull requests safely.
- Explicit branch and pull-request ownership.
- One implementation component that owns publication and reconciliation rules.
- At most one compatible active bot-owned proposal pull request for the current work at a time.
- Merge, close, conflict, base drift, human modification, corruption, and retry reconciliation.
- Caller-owned schedule and manual workflow integration.
- Least-privilege permissions and bounded concurrency.
- Synthetic test-repository end-to-end validation.

Exact checkpoint, append-only record, ledger, generation, operation-ID, and adapter representations are M5 decisions. Add them only when a reproduced retry, stale-state, or idempotency failure requires them.

### Exit criteria

- Every mutation reads, reconciles, applies at most one idempotent transition, and verifies the result.
- Reruns do not create duplicate issues, branches, commits, or pull requests.
- A compatible bot-owned draft may receive more bounded work; an unsafe, human-modified, stale, conflicting, or over-budget draft is not modified.
- Branch ownership mismatch, malformed state, unexpected active pull requests, and ambiguous reconciliation fail closed.
- Human review and merge remain explicit boundaries.
- No automatic merge exists.
- Scheduled and manual synthetic workflows complete with bounded sanitized evidence.

See [Campaign and GitHub workflow](../20_architecture/campaign-and-github-workflow.md).

## M6 — GitHub Action release

### Goal

Deliver the validated M5 workflow as a downstream-consumable GitHub Action bound to a selected payload and release policy.

### Dependencies

- M5 complete.
- Release Gate — Governance permits publication and its obligations are satisfied.
- Release Gate — Payload Distribution selects a compatible payload.
- The wrapper/payload composition passes release-grade validation.

### Exit criteria

- `action.yml`, wrapper code, payload version/hash, compatibility mapping, and permissions are immutable release inputs.
- The composite-versus-TypeScript Action host decision is recorded in the payload-distribution ADR with executable evidence.
- Any TypeScript wrapper is a thin host around the CLI and does not duplicate provider, campaign, ledger, patch, or GitHub-publication behavior.
- Any persistent release-authorizing bundle, signed release attestation, signature, release protected-input identity, or released compatibility family is introduced here only when the selected distribution or publication boundary demonstrates a concrete external-consumer or irreversible-release need. M1 through M5 use the minimum provenance and state required by their implemented behavior and do not maintain placeholder release certification or predeclare future identity families.
- Caller-owned `schedule` and `workflow_dispatch` examples are documented.
- Provider secrets and GitHub permissions are scoped and separated.
- Concurrency, cancellation, failure, retry, ownership, and active-pull-request uniqueness match the accepted M5 behavior.
- Exact-version pinning, update, rollback, retirement, and provenance behavior are documented and validated.
- A consumer repository completes the selected installation and workflow smoke.
- License, notice, inventory, package metadata, and release-control obligations pass on the exact release candidate.
- Maintainer review approves the release; green CI alone is insufficient.
- Marketplace publication occurs only through an explicit later decision.

## Release Gate — Governance

### Goal

Record the license, contribution, authority, third-party inventory, and publication disposition required before downstream consumption or external contribution intake.

This gate may proceed in parallel. It does not block M1 through M5 source development. It blocks M6 publication.

## Release Gate — Payload Distribution

### Goal

Select or explicitly defer the framework-dependent payload channel that the GitHub Action wraps.

The first-consumer scenario is a GitHub workflow. Candidate evidence must use the validated production CLI path. A no-selection outcome blocks M6 without invalidating M1 through M5 source results.

## Research — Deferred Process Topology

### Goal

Establish eligibility evidence for child-process loader alternatives only when ADR 0002 reconsideration triggers or post-M1 research priority justifies it.

This track never counts toward M1 progress while it remains non-gating.

## Post-MVP candidates

These are non-gating, unscheduled candidates outside M1 through M6. They may be considered after M6, but listing them does not commit implementation or delivery. Promoting any candidate into M1 through M6 requires a separately reviewed roadmap amendment. They do not retroactively change the direct-observation semantics or exit evidence of M1 through M6.

- effective-documentation resolution and completeness audit:
  - preserve M1 direct observations as a separate factual input rather than relabeling direct absence or marker presence as resolved effective documentation;
  - resolve explicitly supported `<inheritdoc/>` and `<include>` forms within reviewed source, metadata, repository, and retrieval boundaries;
  - distinguish effective presence, absence, incompleteness, broken or ambiguous delegation, cycles, and unavailable resolution without guessing compliance;
  - evaluate summary and represented-component coverage, present-but-incomplete XML, and later documentation-quality rules through explicit policy and evidence;
  - require a separate decision issue; record the accepted semantic decision in an ADR when appropriate; then update or introduce coordinated successor contracts, fixtures, compatibility disposition, and validation before implementation;
- richer exception documentation;
- analyzer, generator, custom-target, and multi-targeting support expansion;
- additional private or specialized target profiles;
- marker-based opt-in private XML documentation targets:
  - use declaration-local `cscribe:doc` and `cscribe:doc+components` placeholders; `+components` includes applicable taxonomy components, not descendant declarations;
  - extend M1 target selection and evidence, M2 placeholder-safe patching, and M3 proposal generation;
  - support deterministic marker-first discovery without requiring enumeration of the complete private surface;
  - reassess after the M3 end-to-end pipeline is validated; implementation remains non-gating Post-MVP work unless an explicit roadmap amendment promotes it.
- incremental audit and evidence caches;
- additional provider adapters and evaluation datasets;
- child-process or stronger isolation topology;
- Native AOT re-evaluation under a new tested profile;
- SARIF and other reporting adapters;
- multi-repository services or daemon lifetimes.
