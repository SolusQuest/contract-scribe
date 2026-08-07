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
- Project-context bootstrap, snapshot identity, and target grouping are deterministic runtime behavior; semantic context routing remains a phase of the same Scribe rather than a second agent or subagent.
- The initial Scribe Runtime transport is OpenAI-compatible, with DeepSeek as the primary evaluation provider and MiMo as the compatibility-evaluation provider. This is an M3 validation direction, not a current support claim.
- Before any provider compatibility or support claim, an M3 provider-validation issue freezes an executable request corpus, expected observations, normalization rules, failure taxonomy, and required evidence. The current baseline does not assume that usage accounting is the only provider divergence.
- Stable prompt-prefix reuse, bounded uncached input, and observable token economics are architecture requirements. Provider cache retention is never correctness or resume state.
- A model never emits or applies the source diff. The deterministic M2 patch engine owns every source modification.
- Campaign state is platform-neutral. A GitHub Issue is one adapter, not the core ledger abstraction.
- Scheduling is caller-owned. Provider-specific batch or pricing policy stays in the provider adapter and run provenance.
- Pre-release contracts follow [Contract lifecycle](../00_project/contract-lifecycle.md). Draft revisions do not require version-number churn when no incompatible consumers need to coexist.
- Uncertainty, stale input, invalid model output, unsafe patch, corrupted state, and ambiguous GitHub ownership fail closed.
- Published fixtures are synthetic. Private downstream validation publishes only bounded sanitized evidence without repository identity, source, private paths, or raw logs.

## Status overview

| Track | Status | Primary outcome |
| --- | --- | --- |
| M0 — Product, contracts, and architecture validation | Done | Evidence-backed execution and topology inputs |
| M1 — Deterministic audit MVP | Current | Production read-only audit and CLI |
| M2 — Deterministic XML documentation patch engine | Planned | Safe documentation-only source changes |
| M3 — Documentation Scribe and proposal engine | Planned | Useful evidence-grounded structured documentation |
| M4 — Resumable campaign orchestration | Planned | Deterministic budgeting, resume, and lineage |
| M5 — GitHub proposal workflow | Planned | Idempotent ledger, branch, commit, and draft PR workflow |
| M6 — GitHub Action release | Planned | Downstream-consumable Action and payload |
| Release Gate — Governance | Open | License and contribution disposition |
| Release Gate — Payload Distribution | Open | Selected payload channel and ADR |
| Research — Deferred Process Topology | Triggered only | Eligibility evidence for child-process alternatives |

## Production-project evolution

The long-lived M5 product graph contains six production projects, added only when their owning capability starts:

| Milestone | Project change |
| --- | --- |
| M0 | Minimal `ContractScribe.Core` and `ContractScribe.Cli`; Roslyn work remains test-only experiment evidence. |
| M1 | Add production `ContractScribe.Roslyn`. |
| M2 | Add `ContractScribe.Patching`. |
| M3 | Add `ContractScribe.Agent`. |
| M4 | Add platform-neutral campaign behavior to `ContractScribe.Core`; no new milestone-named project by default. |
| M5 | Add `ContractScribe.GitHub`. |
| M6 | Add the selected Action wrapper and release artifacts; no new C# project by default. |

The target graph is not created up front. Fixture, experiment, integration-test, and optional evaluation projects are classified separately. See [Project structure](../20_architecture/project-structure.md).

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

M0 created a commit-pinned milestone baseline, not an external compatibility freeze. Pre-release v1 contracts may be completed through a coordinated amendment and revalidation. M0 historical evidence remains bound to the exact revision that produced it.

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
- Validate on Ubuntu and Windows X64 plus an independent real-world or downstream read-only smoke.

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
- The validated source/toolchain baseline succeeds on the required matrix.
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

### Exit criteria

- Every accepted change maps to a selected target and structured proposal.
- Pre/post non-documentation tokens are identical.
- Signatures, modifiers, attributes, constraints, symbols, tests, project files, and unselected documentation remain unchanged.
- Reapplying an accepted patch produces no diff.
- Formatting allowance is bounded and documented.
- Stale, ambiguous, malformed, unsupported, or unsafe input fails closed.
- Ubuntu and Windows fixtures cover encoding, BOM, newline, and replacement behavior.

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

- Context Pack v1, Style Profile v1, Proposal Request v1, and Documentation Proposal v1 drafts.
- Deterministic repository-entrypoint bootstrap and nested-`AGENTS.md` applicability.
- Repository context snapshots, scope overlays, target evidence packs, route manifests, and evidence authority.
- Semantic multi-file context routing inside the same bounded Scribe loop.
- Bounded Roslyn semantic and repository-read tools.
- Project-owned tool-call loop and terminal structured proposal submission.
- Stable reusable prompt-prefix construction and deterministic context-group identity.
- Deterministic test runtime and an OpenAI-compatible provider transport.
- DeepSeek primary live evaluation and MiMo compatibility validation.
- Provider observation capture and corpus-defined normalization for total, cached, uncached, output, reasoning, cost, timeout, cancellation, retry, and failure behavior where evidenced.
- Prompt-injection resistance and public/private data boundaries.
- Documentation-quality, cache-locality, and cost evaluation.
- M2 patch-engine integration as the only source-write path.

### Initial tool set

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
- Semantic routes are followed by the same Scribe through bounded read tools; cycles, escapes, stale files, and exhausted context budgets fail closed.
- Repository instruction, project documentation, and source evidence remain distinct authority classes.
- Independent Scribe runs reuse immutable repository and scope context without sharing conversation history.
- Compatible runs produce the same locally computed cacheable-prefix identity across fresh processes.
- Target-specific evidence and run metadata do not appear before the reusable-prefix boundary.
- The default context pack handles ordinary targets; extra reads are bounded, repository-confined, deterministic, and evidence-labeled.
- Tool, total/uncached-token, cost, attempt, and wall-clock budgets are enforced; cached input is retained as usage evidence when reported.
- Invalid tool calls, invalid structured output, unsupported targets, and insufficient evidence return stable fail-closed outcomes.
- A deterministic test runtime proves orchestration and retry mechanics.
- The M3 provider-validation issue freezes the executable request corpus, expected observations, normalization rules, failure taxonomy, and evidence required for compatibility statements.
- DeepSeek synthetic evaluation produces useful proposals under the same contracts, and MiMo executes the compatibility corpus through the same OpenAI-compatible request and tool loop.
- Every evidenced DeepSeek and MiMo response shape has deterministic normalization fixtures; unknown provider fields remain available for bounded diagnostics.
- Compatibility and support statements are limited to the executed provider/model corpus and observed behavior.
- Provider, model, prompt, tool-policy, evidence, and style provenance are recorded.
- Accepted model output passes the structured proposal validator and M2 patch engine.
- Evaluation records proposal validity, evidence support, patch acceptance, hallucinated-claim rate, total and uncached input, cache reuse, output, cost per accepted documentation block, latency, tool calls, and sensitive-data/publication-boundary results.

### Non-goals

- A reusable general coding-agent framework.
- A third-party coding-agent runtime as a production dependency.
- A separate context-curator agent, parent/child agent fork protocol, or shared hidden memory.
- A universal provider compatibility matrix or permanent DeepSeek/MiMo exclusivity promise.
- Long-lived conversational sessions.
- Direct source or GitHub mutation.

See [Documentation Scribe](../20_architecture/documentation-scribe.md) and [Scribe context and prompt economics](../20_architecture/scribe-context-and-prompt-economics.md).

## M4 — Resumable campaign orchestration

### Goal

Create deterministic work planning, multi-dimensional budgets, resume, retry, and campaign lineage independently of GitHub.

### Scope

- Snapshot, Work Plan, Batch, and Campaign identities.
- Context-group identity and cache-local target grouping.
- Stable target ordering and cursor semantics.
- Run and pull-request budget planning.
- Retry taxonomy and bounded attempts.
- Same-snapshot continuation and new-base reconciliation.
- Platform-neutral Campaign State and state-adapter interface.
- Crash, replay, and partial-failure behavior.

### Exit criteria

- Within one snapshot, identical audit results and planning policies produce identical work-plan content and identity. Different snapshots may reuse an identical content digest but always have distinct execution identities.
- Documentation-block, file, patch-size, provider-request, total/uncached-token, cost, attempt, and time budgets are independently enforced.
- A crash or replay does not duplicate accepted work or lose a committed checkpoint.
- Same-snapshot continuation binds the work-plan identity and evolving proposal head.
- Merge lineage creates a new snapshot on the new base while preserving the campaign identity.
- Stale base, terminal target failure, retryable provider failure, budget exhaustion, and supersession have executable state transitions.
- State contains no private source, private or complete prompt content, raw provider response, or full diff.
- Two different base commits that produce byte-identical canonical Audit Result artifacts still produce distinct snapshot-scoped work-plan and batch identities; old cursors, checkpoints, operations, and pull-request generations fail closed under the new snapshot.
- No GitHub mutation is required to validate the core.

## M5 — GitHub proposal workflow

### Goal

Implement the GitHub Issue state adapter and an idempotent branch, commit, and generation-based active proposal pull-request workflow.

### Scope

- GitHub Issue checkpoint and append-only run records.
- Branch and commit ownership.
- A .NET GitHub adapter that owns all publication and reconciliation rules.
- At most one active bot-owned proposal pull request per campaign at a time; the adapter creates draft generations, and a human-promoted ready pull request remains active until terminal.
- Same-snapshot batch append while safety conditions and pull-request budgets hold.
- Merge, close, conflict, base drift, human modification, corruption, and retry reconciliation.
- Caller-owned schedule and manual workflow integration.
- Least-privilege permissions, concurrency, and operation IDs.
- Synthetic test-repository end-to-end validation.

### Exit criteria

- Every mutation reads, reconciles, applies at most one idempotent transition, and verifies the result.
- Reruns do not create duplicate issues, branches, commits, or pull requests.
- Pull-request state records the active identity, generation, bound snapshot, terminal predecessor, and legal conditions for creating the next generation.
- A compatible bot-owned draft may receive another bounded batch; an unsafe or over-budget draft is not modified.
- Reaching the pull-request budget transitions to awaiting review.
- Merge starts a new snapshot and continues campaign lineage.
- Human changes, branch ownership mismatch, conflict, stale base, malformed ledger, and unexpected active PRs fail closed.
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
- Any persistent release-authorizing bundle, signed release attestation, signature, release protected-input identity, or released compatibility family is introduced here only when the selected distribution or publication boundary demonstrates a concrete external-consumer or irreversible-release need. M1 through M5 use the minimum provenance required by their owning contracts and do not maintain placeholder release bundle certification. This does not prohibit exact-revision milestone evidence, bounded public or private smoke attestations, draft contract-baseline or provenance identities, or snapshot, work-plan, batch, campaign, ledger, cursor, checkpoint, and operation identities required before M6.
- Caller-owned `schedule` and `workflow_dispatch` examples are documented.
- Provider secrets and GitHub permissions are scoped and separated.
- Concurrency, cancellation, failure, retry, active-pull-request uniqueness, and snapshot-bound generation behavior match M5.
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
