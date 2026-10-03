# Campaign and GitHub workflow

> **Status:** M4–M6 runtime and internal qualification are implemented. [M7 plan](../90_roadmap/m7-plan.md) owns the adopted fixed-batch/automatic-continuation direction; C1 changes planning only. Current protocol documents remain implementation references until their owning code/schema issues update them.

## Goal and ownership

Campaign orchestration turns deterministic Audit violations into bounded, resumable documentation work and reconciles that work with GitHub without duplicate or overlapping PRs. Core owns platform-neutral plan/state/accounting; Patching alone materializes and validates source candidates; GitHub alone owns authenticated remote reads and mutations; CLI composes these capabilities. The Action is a thin host around the .NET payload and does not implement a second state machine.

The model produces structured documentation or a skip. It does not select new targets, mutate source, persist checkpoints, use GitHub credentials or decide publication authority. Accepted proposal, validated patch, published PR and merged source are separate facts.

## Current implementation references

The current `CampaignPlanner` consumes exact M1 Classification/Observation/Evidence/Audit authority, caller-attested snapshot and complete source/declaration projection. It emits every and only current violation as ordered complete-block work, with explicit executable/terminal dispositions. It neither discovers Git nor invokes provider, Patching or GitHub. [Campaign Planning v1](contracts/campaign-planning-v1.md) owns exact commitments, ordering, source-free plan shape and rejection behavior.

[Campaign State v1](contracts/campaign-state-v1.md) owns the complete current checkpoint, validated proposal projection and candidate commitments. The C3 reducer owns transitions and consumptive charges; the X2A store supplies conditional predecessor replacement and canonical readback. Current same-snapshot recovery and caller-attested changed-base supersession are implemented by their runtime owners. They do not automatically discover the appropriate consumer GitHub lifecycle transition.

[GitHub Publication v1](contracts/github-publication-v1.md) owns current source-free publication authority, policy, exact operation commitments and result vocabulary. The M5 R2–R6 adapter performs authenticated complete resource reads, coordination/proposal-ref transitions, exact Git object publication, PR ownership/uniqueness and reconciliation. [github-proposal CLI](github-proposal-cli.md) composes it with campaign execution through explicit `start` and `resume`. Its current append transition and block/file/patch-size business ceilings describe existing behavior, not M7 direction. C1 does not delete executable fields or change old state semantics before their owning implementation PRs.

The [M5 protocol decision](validation/m5-publication-protocol-decision.md) and [M6 internal qualification](../90_roadmap/m6-plan.md) retain their original questions, tested revisions and limits. Historical recovery/replay proof does not establish M7 automatic remaining-work consumption.

## M7 planning direction

The complete decisions, numeric defaults and exit scenarios live in the M7 plan. The guidance below summarizes component responsibilities without defining a competing wire protocol.

### Immutable selection and identity

A snapshot binds immutable base and correctness-bearing inputs. The full plan contains all ordered pending canonical documentation targets; a fixed batch selects a semantic subset within its creation quota. A type and ten public properties count as eleven blocks; a method's documentation components count as one. Compliant declarations are excluded.

Stable semantic ordering respects containing-type/direct-member, project/compilation, instruction and style boundaries; nested types are independent. Selection defers a whole next group that does not fit, never packs later smaller groups ahead of it, and splits oversized groups at smaller boundaries then stable quota-sized chunks to avoid starvation. Group selection does not mean sequential runtime group feeding.

Same-base compatible unpublished recovery restores only that original membership. A raised quota never expands it; a lowered invocation quota limits newly started distinct targets. Retrying a target consumes resources without another target slot; accepted results are reconstructed without regeneration. Fresh Roslyn/patch validation of bound recovery state is distinct from new Audit/plan discovery.

`SymbolRef` is compilation/snapshot scoped, not a permanent entity identifier across revisions. Audit content identity alone is insufficient execution identity. The actual implementing contract owns the minimal snapshot/plan/batch/checkpoint/attempt/operation bindings; no old cursor, proposal or count authorizes work on a new snapshot.

### Ledger and lifecycle first

CLI reads the complete consumer coordination ledger and authenticates target HEAD, proposal refs and PR state before new Audit/plan discovery or provider work. Missing ledger still requires the managed-active-PR check. Corrupt/incompatible/unverifiable state stops, rather than initiating a second campaign. Active-work and closed-same-base gates take precedence over configuration drift or quota changes.

An open same-base draft or ready PR awaits review without new discovery/provider work. An open old-base PR pauses even after unrelated target changes. Ownership loss, human commits, conflicts or ambiguous active work pause. A closed-unmerged predecessor blocks only its exact base, including when quotas/configuration change. A different HEAD with no other active PR permits a fresh Audit, including for README-only changes.

After merge and with no active PR, build a fresh snapshot/Audit on latest HEAD and discover actual remaining work. Never subtract prior accepted counts or treat unmerged proposals as completed source. Without a lifecycle gate, unpublished correctness-input changes create a new snapshot without migrating old proposals; execution-budget changes alone preserve compatible membership and history.

### Shared execution and resource stops

One normal Action presents the complete selected manifest to one bounded Scribe conversation. Lazy bounded reads reuse context while preserving target-specific instruction/evidence/style authority. Host validation and incremental acknowledgement accept only selected, nonduplicate targets; final coverage is checked independently of model claims. Invalid submissions get finite repair, transient provider errors finite retry, and insufficient evidence may skip. Skip/failed work stays unresolved without an automatic loop on the same snapshot.

All targets, tools and retries share one invocation's limits. Optional campaign aggregate caps default unlimited while retaining complete accounting history; zero is not an unlimited sentinel. Remove PR block/file/patch-size business quotas, retaining parser/transport/context/patch safety bounds. [M7 resource defaults](../90_roadmap/m7-plan.md#shared-resources-and-initial-defaults) are initial intended configuration, not values implemented by C1.

Controlled resource/context stops publish a safe validated subset when possible. Publication seals that subset and waits for human review; unfinished work returns through a later allowed fresh Audit. An abrupt unpublished interruption restores the original fixed batch and accepted results. No complete hidden-conversation restoration or automatic compaction across Actions is required.

### Complete recovery and one PR per batch

One stable consumer-repository coordination ref per campaign stores complete recovery and publication facts. Preserve deterministic owner/name + targetRef + campaignLineage naming, exact-predecessor CAS and authenticated readback. No full-ref override is added. Artifacts are diagnostic exports, not recovery authority.

Claims/reservations precede provider dispatch; accepted, settled and unknown facts are incremental. The checkpoint may retain bounded host-validated structured generated documentation, including unpublished/unmerged proposals, with necessary recovery facts. It excludes source-evidence bodies, prompts, raw provider responses, reasoning/transcripts, secrets and candidate bytes. Reconstruct and validate candidate source through Patching; see [Security boundary](security-boundary.md).

A batch has at most one PR and a campaign at most one active PR. Published draft/ready PRs receive no additional targets. Lost publication responses recover only the exact original operation/PR. Partial publication never authorizes a second PR for that batch. Human ready/merge/close decisions remain explicit; automatic rebase, merge or deletion is outside M7.

### Invocation observations

CLI/Action expose versioned usage JSON, result-envelope linkage and a step summary. Report selected/started/accepted/skipped/failed/remaining work, provider/tool/retry counts, input hit/miss and output/reasoning partitions, timing, stop reason, measurement completeness/unknowns, and invocation/campaign/snapshot/batch/checkpoint/PR linkage. Actual usage, missing usage, exposure and conservative charges remain separate. Recovered historical usage is not new invocation spend; no-provider gates report zero new requests. Hard kills rely on incremental durable facts rather than guaranteed final outputs.

## Existing platform safety boundaries

The adapter's authenticated repository and resource observations cannot be injected through caller authority. Current coordination/proposal-ref admission uses one GraphQL `updateRefs` entry with exact predecessor or forty-zero expected absence, nonzero successor and `force=false`; REST ref create/update is not admission. Writes require direct readback and are not blindly replayed.

The current reconciler performs at most one proposal transition or one PR create per port call. Exact metadata/marker/OID checks and authority-bound readback identify lost-response partial operations. An active stale draft blocks a second create. Future M7 recovery keeps those safety properties without treating a lost acknowledgement as success or permission to append targets.

The Action's repository/branch/campaign concurrency control complements operation-level state checks; it does not replace exact-predecessor CAS. Read/reconcile/validate, apply at most one authorized transition, then read back and verify. An overlapping run or partial failure never justifies another active campaign PR.

The GitHub adapter consumes Core-owned publication authority and a separate candidate byte payload; it does not invoke Roslyn, Patching, Agent or the model provider. The wrapper may pass inputs/credentials and project bounded outputs, but cannot interpret checkpoints, choose safe continuation, mutate refs/PRs or bypass adapter failure. [Security boundary](security-boundary.md) owns credential, parser/transport and diagnostic rules.

## Trigger and validation boundary

Manual, optional native schedule and caller-selected external triggers all invoke the same product safeguards. Cron timing and provider pricing windows are caller/provider observations rather than delivery or cost guarantees. Cache availability changes measured cost, never target selection, accepted work or recovery authority.

M7 tests exercise shared-session multi-target confinement, fixed membership, fresh-runner interruptions, overlap, unknown results/usage, exact original-operation recovery, open changed-base pause, closed same-base/config pause, README-only new-head unlock and safe partial publication through production paths. The full matrix and separately authorized live-proof boundary are in [M7 exit evidence](../90_roadmap/m7-plan.md#exit-evidence-and-semantic-examples). Existing M4–M6 evidence remains historical at its recorded revisions.
