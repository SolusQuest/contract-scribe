# Campaign and GitHub workflow

> **Status:** Current pre-release M4 local campaign boundary plus accepted M5-R1 GitHub publication protocol. `CampaignPlanner`, Campaign State v1, the C3 reducer, X2A checkpoint store, X1B/X1C execution, and the production campaign CLI implement snapshot-scoped planning, durable same-snapshot recovery, and caller-attested changed-base supersession. M5-R1 now freezes the credential-free Core publication authority, coordination-ref admission protocol, deterministic remote intent, policy, results, and adapter port. Production GitHub transport/reconciliation and CLI composition remain later M5 leaves; automatic rebase, ready, merge, close, deletion, and Action scheduling remain M6 deferrals.

## Goal

Campaign orchestration turns audit violations into deterministic, bounded, resumable documentation work and later reconciles that work with GitHub state without duplicate or overlapping pull requests.

The core state model is platform-neutral. GitHub Issues, branches, commits, and pull requests are adapter representations.

The production adapter is part of the .NET payload. A GitHub Action wrapper invokes that payload and does not duplicate publication logic in TypeScript, shell, workflow expressions, or a second state machine.

The normative M5-R1 contract is [GitHub Publication v1](contracts/github-publication-v1.md). The exercised alternative comparison and minimum-protocol rationale are in the [M5 publication protocol decision](validation/m5-publication-protocol-decision.md).

## Current M4-C1 planning boundary

M4-C1 defines one pure Core planning operation. It consumes the current validated `ClassificationSet`, `DocumentationObservationSet`, exact bound-evidence authority, canonical `AuditDocument`, an opaque caller-attested snapshot binding, family-tagged canonical-JSON content authority, and a complete exact-set owner/source/declaration authority projection. It emits one immutable `CampaignWorkPlan`.

The planner selects every and only current Audit violation. Target and component violations that share one physical documentation owner collapse into one complete-block work item without losing their parent-target association. A work item is either executable under the current M2/M3 boundary or terminal with a closed, ordered, deduplicated reason set and fixed primary reason. Terminal work remains visible but cannot be dispatched.

The current planner is deliberately source-free and platform-neutral. It does not discover Git state, read a filesystem or workspace, invoke Roslyn or Patching, contact a provider, persist progress, mutate GitHub, or claim that an opaque snapshot attestation is live. The composition owner must produce and attest the complete evidence/source/declaration projection for the same snapshot as the three existing M1 authorities, including a domain-separated commitment over the current load session's `PhysicalSourceIdentity` while retaining each lexical repository path as its exact locator. Core builds one bounded source-session graph before owner partitioning: compilation context maps uniquely to project, repository path and physical commitment mappings close in both directions, repository authority retains separate decoded-observation-text and exact-file-byte commitments, and generated outputs are scoped by project and compilation context. Core commits a bounded complete current observation projection and validates every complete bundle as an exact declaration-to-item bijection while validating every included non-complete item against current declaration text and source authority before applying Audit precedence. Included evidence always retains production-selected kind and relation. Source-unavailable incomplete observations alone admit an empty `Unavailable`/`SourceUnavailable` bundle; all other non-complete observations require a non-empty `Partial`/`BudgetExhausted` bundle. Global Classification/Observation/Evidence/Audit authority is validated for every supported subject, while physical owner authority is required only for the exact closure of selected violation parents. Missing, extra, duplicated, substituted, conflicting, stale, or contradictory authority fails the entire plan.

The plan contains identities, exact source commitments and encoding, spans, target/component facts, Audit-row commitments, Style Profile values for executable targets, dispositions, and bounded counts. Terminal targets must not carry execution-only Style Profile authority. It contains no source excerpts, trivia, documentation text, prompt content, provider output, candidate bytes, diff, credential, transcript, machine-absolute path, checkpoint history, or GitHub state.

The normative planning contract is [Campaign Planning v1](contracts/campaign-planning-v1.md). [Campaign State v1](contracts/campaign-state-v1.md) is the accepted persisted C2 representation; the C3 reducer owns transitions and budget consumption; X2A owns secure exact-predecessor checkpoint publication; and X1B/X1C plus the campaign CLI compose provider and cumulative M2 execution. GitHub Publication v1 consumes a source-free projection of accepted M4 authority without retroactively adding remote identity or mutation semantics to the local M4 contracts.

## Identity model

### Snapshot identity (current M4-C1 subset)

A snapshot binding identifies the caller-attested immutable planning input. M4-C1 binds the opaque value directly together with explicit repository, input, policy, target-profile, and product-contract commitments; it does not derive or interpret Git identity:

```text
opaque snapshot binding
+ repository commitment
+ input commitment
+ policy-authority commitment
+ target profile
+ product/contract revision commitment
```

### Work-plan identity (current M4-C1 subset)

A work plan uses one composite execution commitment over the validated snapshot, canonical Audit bytes, the complete canonical M1 relation set consumed by semantic evidence, all correctness-bearing execution-policy content and typed ceilings, and the canonical complete ordered owner/target/component/violation/disposition graph:

```text
snapshot commitments
+ canonical Audit Result digest
+ complete canonical relation observations
+ selection and ordering policy revisions
+ proposal, agent, context, tool, provider, retry, M2, cost, and product content authorities
+ typed Scribe and campaign limits
+ ordered complete-block facts and per-executable-target Style Profile content
```

The execution commitment is not a permanent entity identifier or merely an Audit content digest. Its SHA-256 preimage uses a domain tag and explicit big-endian length-framed strict UTF-8 fields. Work-item keys use a separate domain and bind the exact execution commitment plus the complete item descriptor. A changed opaque snapshot therefore changes the execution commitment and every key even if canonical Audit bytes collide. Cursor, checkpoint, replay, and operation binding remain deferred to M4-C2 and later work.

### Context-group identity

A context group identifies targets whose Scribe requests may reuse the same stable prompt prefix:

```text
provider and model configuration
+ agent protocol identity
+ tool-registry identity
+ repository-context identity
+ scope-context identity
+ every other prefix-resident input identity, including style-profile identity when present
+ locally computed cacheable-prefix identity
```

The context-group identity must cover every input that can change the reusable prefix and the resulting locally computed cacheable-prefix identity. Two targets cannot share a context group merely because their repository and scope contexts match when a prefix-resident style profile, request template, tool definition, or other prefix input differs.

Context-group identity is a scheduling and economic boundary, not target-completion state. Target-specific evidence, attempts, and proposals do not belong to it.

### Batch identity

A batch identifies one bounded execution prefix:

```text
work-plan identity
+ cursor start
+ budget configuration
+ batch sequence
```

Batch identity inherits snapshot identity through work-plan identity. Every persisted cursor, checkpoint, attempt record, operation ID, and replay key also carries or is derived from the snapshot and work-plan identities; publication operations additionally bind the pull-request generation. A missing or mismatched identity fails closed.

### Campaign identity

A campaign is the stable lineage that continues across snapshots as documentation pull requests merge and the target branch advances. A campaign does not pretend that two different base commits are one immutable snapshot.

### Current M4 changed-base supersession

A valid resume with the same opaque snapshot uses exact same-snapshot continuation. A different caller-attested opaque snapshot enters changed-base reconciliation only after the production M1 load/audit and complete C1 plan succeed. Policy or configuration drift, input-identity or target-profile substitution, product/contract incompatibility, missing or invalid state, and checkpoint or lease conflict remain distinct fail-closed outcomes and cannot trigger migration.

The composition owner builds one clean revision-zero C2 template from the new M1/C1 authority. `CampaignStateReducer.Supersede` is the only transition authority: it preserves the stable lineage, configured lineage policy, and monotonic consumptive charges; conservatively settles old active exposure; retains one bounded immediate-predecessor summary; and replaces current work, proposals, attempts, candidate state, and completion state with the fresh plan. `CampaignCheckpointAcceptance` and X2A then conditionally replace the exact predecessor and require canonical readback before X1B/X1C may reserve or dispatch successor work.

Opaque snapshot identity is authoritative even when two exact repository fixture revisions produce byte-identical canonical Audit and consumed content commitments. The new execution commitment and every work-item and attempt namespace still change. No display name, array position, old `SymbolRef`, proposal, Patch candidate, or completion record grants cross-snapshot continuity. The CLI performs no Git discovery and creates no branch, pull-request generation, or remote operation identity; those remain M5/M6 responsibilities.

### Symbol and result identity

`SymbolRef` is deterministic within its pinned compilation context; it is not a permanent entity identifier across arbitrary repository revisions. A rename, containing-type or signature change, target-framework change, or compilation-context change may produce a different identity. Cross-snapshot continuation reruns audit and applies explicit reconciliation rather than assuming continuity from a prior `SymbolRef`, method name, or array position.

A canonical Audit Result digest identifies result content, not the complete campaign execution identity. Snapshot, policy, contract baseline, target-selection, proposal, style, agent-protocol, tool-registry, and context-selection inputs remain independently identity-bearing. See [Semantic foundation](semantic-foundation.md).

## Work unit and ordering (current M4-C1 draft; prefix selection deferred)

The initial work unit is one documentation block attached to one canonical declaration. Summary, type-parameter, parameter, return, value, exception, and remarks fields for that declaration count as one block.

Work items are ordered by the canonical physical-owner descriptor using ordinal text and numeric spans. Input order, current culture, time, randomness, display names, machine paths, and platform filesystem comparers have no authority. M4-C1 emits the complete current violation plan. Choosing and consuming a budget-bounded execution prefix is deferred to M4-C3.

## Budgets

At minimum, support independent limits for:

- documentation blocks per run;
- documentation blocks per pull request;
- changed files;
- patch bytes or changed lines;
- provider requests;
- total and uncached input-token consumption when observable;
- output-token consumption and reasoning-token observations when available;
- estimated cost when available;
- wall-clock duration;
- attempts per target.

A target attempt consumes request, token, cost, and time budgets even when its proposal is skipped or rejected. Accepted documentation-block count increases only after deterministic patch validation succeeds.

The planner groups compatible targets by context-group identity and keeps each group's request prefix stable. A provider cache miss still consumes uncached-input and cost budgets. Cache availability never changes target ordering, evidence authority, accepted work, or state-transition semantics.

## State model

Representative states include:

- `planned`;
- `generating`;
- `proposal-complete`;
- `patch-validated`;
- `draft-pr-open`;
- `awaiting-review`;
- `partial-budget-exhausted`;
- `blocked-open-pr`;
- `blocked-human-change`;
- `stale-base`;
- `merged`;
- `closed-unmerged`;
- `failed-retryable`;
- `failed-terminal`;
- `superseded`;
- `complete`.

The final contract defines legal transitions, terminal states, retry ownership, and precedence.

Campaign state records the current snapshot and work-plan identities, cursor and batch progress, the active pull-request identity when one exists, the pull-request generation, the snapshot bound to that generation, and the merged or closed predecessor. A transition may create a new pull-request generation only when any predecessor is terminal and reconciliation proves that no active campaign pull request exists.

## Same-snapshot continuation

The initial GitHub workflow permits at most one active bot-owned proposal pull request per campaign at a time. The adapter creates each generation as draft; an open pull request remains active after a human marks it ready for review and until it is merged or closed. A campaign may create successive pull-request generations as snapshots advance; each generation is bound to one snapshot identity and records its sequence and predecessor.

A later run may append another bounded batch to the same draft only when:

- the target-branch base is still compatible with the recorded snapshot or has been reconciled under an explicit safe rule;
- the proposal branch head matches the recorded state;
- the pull request is still draft and bot-owned;
- no unexpected human commit or unmodeled file change exists;
- no conflict exists;
- the pull-request documentation-block, file, and patch-size budgets remain available.

The state records both the immutable base commit and the evolving proposal-branch head. Continuation never uses a bare array index without snapshot and work-plan identity.

When the pull-request budget is reached, the adapter stops appending and moves to `awaiting-review` even if the campaign has remaining work.

## Merge and base advancement

After merge, the target branch has a new commit. The next run:

1. creates a new snapshot;
2. reruns deterministic audit;
3. observes merged documentation as compliant;
4. reconciles remaining targets by stable identity;
5. continues the same campaign lineage with a new snapshot-scoped work plan;
6. creates a new pull-request generation only when remaining work exists and the predecessor is terminal.

It does not continue to mutate the old snapshot cursor as if the base were unchanged. The prior generation remains immutable lineage evidence and cannot be treated as the active pull request for the new snapshot.

Unrelated base advancement before merge triggers reconciliation. If safe rebase and revalidation cannot be proven, the run records `stale-base` and stops.

## Human changes and pull-request closure

Unexpected human commits, manual modifications to generated documentation, branch replacement, or a non-bot owner trigger `blocked-human-change`. The adapter does not overwrite them.

Closing a pull request without merge requires an explicit policy outcome:

- abandon the batch;
- replan on the latest base;
- allow a maintainer-requested retry;
- mark the campaign terminal.

No automatic merge is permitted in the initial workflow.

## Accepted M5 coordination protocol

The initial adapter uses one deterministic coordination ref per campaign. A
managed Issue/comment ledger and an externally serialized caller were executed
against the same bounded race/recovery vectors and rejected as the initial
authority. The accepted protocol needs no Issues write or external lock service.

Credential-free local admission validates repository/target/configured-base,
M4 authority, complete changed-path/full-file-hash facts, stable operation,
generation/predecessor, immutable publication policy, and optional exact
closed-successor authorization. Ephemeral candidate bytes are a separate exact
path-set input. Actual base trees, entry types/modes, current refs, proposal
commits/trees, and PR state are authenticated adapter observations and cannot be
injected through caller authority.

Both coordination and proposal ref creation/advancement use one GraphQL
`updateRefs` `RefUpdate` with exact predecessor or forty-zero expected absence
`beforeOid`, exact nonzero `afterOid`, and `force=false`. REST ref create/update
is not an admission primitive. Every later content, ref, or PR request is gated
by an exact claim/predecessor/base reread, performs at most one bounded mutation,
and requires direct readback before another create is permitted.

Deterministic source-free state, tree/commit intent, ownership markers, locally
expected object IDs, and the exact draft-PR request are committed. Response loss
is recovered only by exact OID/ref/marker discovery. Target movement during PR
create processing may leave one exact marker-owned stale draft; it records no
completed transition and blocks automatic continuation or a second create.

## Scheduling

The Action supports caller-owned `schedule`, `workflow_dispatch`, and composed-workflow triggers. The caller selects cron and timezone behavior.

Provider batch modes or time-dependent pricing are optional adapter configuration. A run records the selected provider policy and cost ceiling but does not claim that a particular local time guarantees lower pricing.

Within one run, the campaign scheduler should process targets from the same context group close together so the selected OpenAI-compatible provider can reuse the repository and scope prefix. It may account for an initial cache warm-up before widening concurrency. Group ordering, concurrency, and warm-up behavior remain bounded and deterministic enough to replay; a provider cache miss is an economic observation rather than a failed campaign transition.

On same-snapshot resume, the campaign reuses local context identities and reconstructs the same logical prefix. It does not assume that a provider cache entry or conversation survived between scheduled runs.

## Concurrency and idempotency

The Action uses a repository/target-branch/campaign concurrency key. The state adapter additionally enforces operation-level idempotency. Operation IDs and replay keys bind the snapshot identity, work-plan identity, and transition-specific inputs; publication operations additionally bind the pull-request generation.

Every mutation follows:

```text
read current remote state
  -> validate ownership and digest
  -> reconcile existing branch / commit / pull request
  -> apply at most one idempotent transition
  -> read back and verify
```

Partial failures never justify creating a second overlapping active campaign pull request.

The M4 conformance corpus includes a collision vector in which two different base commits produce byte-identical canonical Audit Result artifacts. The new snapshot must still produce a distinct work-plan identity; the old cursor, batch, checkpoint, attempt, operation record, and pull-request generation must all be rejected for continuation or deduplication under the new snapshot.

## Host integration

The later `ContractScribe.GitHub` project consumes the Core-owned validated publication authority and separate byte payload through the single publication port and returns a structured Core result. It does not invoke Roslyn, Patching, or the model provider. Before its first mutation it performs authenticated complete reads and binds those observations into the prepared remote-operation commitment.

`ContractScribe.Cli` composes the campaign state machine, patch result, state adapter, and GitHub adapter. The Action host supplies documented inputs and credentials to that CLI command and maps its stable outcome to Action outputs.

If the eventual Action uses TypeScript, the wrapper may not:

- create or update the managed Issue;
- create refs, commits, or pull requests;
- choose whether an existing draft is safe to continue;
- interpret ledger or campaign-state payloads;
- repair, retry, or bypass a failed adapter transition.

Keeping those operations in C# makes local fake-server tests and Action executions exercise the same reconciliation implementation.
