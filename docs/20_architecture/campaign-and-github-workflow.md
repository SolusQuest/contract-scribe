# Campaign and GitHub workflow

## Goal

Campaign orchestration turns audit violations into deterministic, bounded, resumable documentation work and later reconciles that work with GitHub state without duplicate or overlapping pull requests.

The core state model is platform-neutral. GitHub Issues, branches, commits, and pull requests are adapter representations.

The production adapter is part of the .NET payload. A GitHub Action wrapper invokes that payload and does not duplicate publication logic in TypeScript, shell, workflow expressions, or a second state machine.

## Identity model

### Snapshot identity

A snapshot identifies the immutable audit input:

```text
repository identity
+ base commit
+ explicit input path
+ policy digest
+ audit tool and contract baseline
```

### Work-plan identity

A work plan identifies the ordered documentation work derived from a snapshot:

```text
snapshot identity
+ audit-result digest
+ target-selection policy
+ proposal contract
+ style profile
+ agent protocol and tool-registry identity
+ project-context selection policy
+ stable ordering rules
```

The work-plan identity is an execution identity, not merely a content digest. An implementation may compute a separate reusable content digest for diagnostics or caching, but cursors, checkpoints, target completion, replay, and idempotent operations bind the snapshot-scoped work-plan identity.

### Context-group identity

A context group identifies targets whose Scribe requests may reuse the same stable prompt prefix:

```text
provider and model configuration
+ agent protocol identity
+ tool-registry identity
+ repository-context identity
+ scope-context identity
```

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

### Symbol and result identity

`SymbolRef` is deterministic within its pinned compilation context; it is not a permanent entity identifier across arbitrary repository revisions. A rename, containing-type or signature change, target-framework change, or compilation-context change may produce a different identity. Cross-snapshot continuation reruns audit and applies explicit reconciliation rather than assuming continuity from a prior `SymbolRef`, method name, or array position.

A canonical Audit Result digest identifies result content, not the complete campaign execution identity. Snapshot, policy, contract baseline, target-selection, proposal, style, agent-protocol, tool-registry, and context-selection inputs remain independently identity-bearing. See [Semantic foundation](semantic-foundation.md).

## Work unit and ordering

The initial work unit is one documentation block attached to one canonical declaration. Summary, type-parameter, parameter, return, value, exception, and remarks fields for that declaration count as one block.

Targets are ordered deterministically by the canonical classification and locator rules. The planner selects a deterministic prefix that satisfies every active budget before model generation begins.

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

## GitHub Issue state adapter

The first adapter may use one managed issue per campaign:

- the issue body contains the current schema version, campaign identity, checkpoint digest, and current summary;
- comments contain append-only run and batch records;
- every mutation has an operation ID bound to the snapshot identity, work-plan identity, intended transition, and pull-request generation when the operation belongs to one;
- replay reconciles remote state before writing;
- malformed, manually corrupted, deleted, or checksum-mismatched state fails closed;
- large ledgers rotate under an explicit successor rule.

The issue stores only machine identities, hashes, counts, budgets, status, bounded diagnostics, validation summaries, and GitHub URLs. It does not store source excerpts, private or complete prompt content, raw provider responses, secrets, complete transcripts, or full diffs.

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

`ContractScribe.GitHub` consumes a validated publication plan and returns a publication record through Core-owned contracts. It does not invoke Roslyn or the model provider.

`ContractScribe.Cli` composes the campaign state machine, patch result, state adapter, and GitHub adapter. The Action host supplies documented inputs and credentials to that CLI command and maps its stable outcome to Action outputs.

If the eventual Action uses TypeScript, the wrapper may not:

- create or update the managed Issue;
- create refs, commits, or pull requests;
- choose whether an existing draft is safe to continue;
- interpret ledger or campaign-state payloads;
- repair, retry, or bypass a failed adapter transition.

Keeping those operations in C# makes local fake-server tests and Action executions exercise the same reconciliation implementation.
