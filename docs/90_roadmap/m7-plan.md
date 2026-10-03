# M7 plan — automatic continuation and shared-session Scribe

## Authority and implementation boundary

This is the repository planning owner for the maintainer-approved M7 behavior in [master #208](https://github.com/SolusQuest/contract-scribe/issues/208), adopted by [C1 #213](https://github.com/SolusQuest/contract-scribe/issues/213). The approved discussion has SHA-256 `eb12ac497618e14922f4ecb0f60cc157f45e897da5d8701f03774e55fd024a13`; the implementation baseline is main-reachable commit `0dae60c487c71c9984214afd8f89b656999c1945`.

Scope adoption is not implementation completion. C1 changes planning and affected architecture guidance only. Dependent M7 implementation begins after C1 is merged. Each owning code/schema issue updates its actual producers, consumers, contracts, validators, tests and documentation coherently; this plan does not make new schemas, configuration fields, CLI operations or Action outputs executable. The [current protocol references](#current-protocol-references) continue to describe the real runtime until those changes land.

M4–M6 acceptance and internal-candidate evidence remain historical at their original revisions. They are not M7 completion evidence or compatibility promises. [Governance #29](https://github.com/SolusQuest/contract-scribe/issues/29) and [first publication #189](https://github.com/SolusQuest/contract-scribe/issues/189) remain separate post-M7 gates requiring their own acceptance and explicit publication authority.

## Outcome and caller experience

Deliver one automatic caller-triggered Action entrypoint. A consumer configures a stable campaign once and invokes the same safeguarded path manually, through optional native GitHub schedule, or through its own external trigger. The caller owns when to trigger. Native schedule is best-effort with no delivery-time or interval guarantee; M7 builds no external scheduler.

The product selects first start, unpublished fixed-batch recovery, exact publication recovery, fresh work after terminal predecessors, no-op, awaiting review or pause. The consumer does not manually choose start/resume or hand off an artifact from the previous runner. Product state, ownership, resource and publication checks apply to every trigger source. Human review, ready/close decisions and merge remain operator actions.

## Work, snapshot and fixed batch

A target is one complete XML documentation block on a canonical declaration. A type plus ten eligible public properties is eleven targets; accessors do not add targets. A method's summary, parameters, type parameters, returns and remarks together count as one target. Already compliant declarations are excluded.

A snapshot binds the immutable base and correctness-bearing inputs, including the analysis input, policy, target profile, applicable instructions and style. Its full plan records all ordered pending targets. A batch is an immutable selected subset with its creation quota, not an expanding prefix across invocations. The checkpoint binds campaign/snapshot/plan/batch identities, target and validated proposal state, candidate commitments, claim/reservation, usage and publication progress. Proposal acceptance, patch validation, publication and merge are distinct facts; none implies a later stage succeeded.

Start with a configurable 100-target Action limit. Select in stable semantic groups preserving containing-type/direct-member relationships and project, compilation-context, applicable-instruction and style boundaries. Nested types are independent groups. Partial declarations and overload relationships need deterministic, unambiguous treatment within those boundaries; exact algorithms belong to the planning implementation and its representative fixtures.

Do not skip ahead to pack the quota. With 95 selected and a next group of 11, select 95 and defer that entire group. A group larger than the quota must make progress: split first at smaller semantic boundaries, then into stable quota-sized target chunks if still oversized. A 230-target group must not starve merely because the default quota is 100. Grouping selects the manifest and communicates relationships; it does not feed groups sequentially to the runtime.

Once saved, membership never grows. Raising a later invocation quota cannot expand the original batch. Lowering it limits newly started distinct targets within that original membership. Failed and skipped starts consume a distinct-target slot; a retry of that target consumes request/token/time resources without a second distinct-target slot. Reconstructing an already accepted result neither regenerates it nor counts as newly started work. Batch size never exceeds its creation quota.

## Lifecycle precedence before discovery or provider work

Resolve consumer configuration and stable campaign identity, then read the coordination ledger and authenticate actual target HEAD, proposal refs and PR facts before new Audit/plan discovery or provider work. Configuration validation does not bypass remote lifecycle gates. Missing ledger requires a managed-active-PR check before initial start; corrupt, incompatible or unverifiable state stops rather than becoming an empty campaign.

Apply the active-PR and closed-same-base rules before treating changed configuration or quotas as a reason to discover new work:

| Observed state | Required action |
| --- | --- |
| Ownership loss, human commits or unexpected changes, conflicts, ambiguous active work, or unverified recovery authority | Pause without discovery/provider dispatch or unsafe overwrite. |
| Published draft or ready PR open on the current base | Await review with no discovery/provider work and no new targets appended. |
| Open PR on an old base after target HEAD changes | Pause before discovery/provider work, even for an unrelated base change. |
| Closed-unmerged predecessor whose base equals current target HEAD | Pause that base, including when only configuration or quotas change. No automatic resend or additional unlock entrypoint. |
| Exact publication operation is incomplete or its response was lost | Recover only the original operation/PR with authenticated readback; never create a replacement because an acknowledgement is unknown. Active-work safety checks still apply. |
| Compatible unpublished state on the same base | Restore only the original fixed batch and accepted results; newly start only unresolved eligible members under this invocation's ceilings. |
| Merged predecessor and no active PR | Create a fresh snapshot and Audit on latest HEAD; discover actual remaining work and create the next generation only if needed. |
| Closed-unmerged predecessor, different target HEAD and no other active PR | Fresh snapshot/Audit is allowed. Any new commit, including README-only, unlocks discovery; unmerged proposals are not completed work on main. |
| No active PR and no same-base closure gate, and unpublished base/policy/profile/style or other correctness-bearing input changes | Create a new snapshot/Audit without migrating old proposals; retain history and usage. Execution-budget changes alone preserve compatible fixed membership. |
| Fixed batch terminal with no valid changes and no active PR | A later Action may select the next batch from the compatible complete plan. Skipped/failed targets remain unresolved and are not automatically retried on this snapshot. |
| No actionable work remains under the current plan | Emit completion/no-op with no provider request; report any unresolved skips/failures rather than claiming compliance. |

Fresh discovery after merge never subtracts historical accepted counts from a new Audit. For example, 12 initially pending and 5 merged normally leaves 7 only if the latest Audit observes that fact. Old `SymbolRef` values, proposals, candidate state, cursors or array positions grant no cross-snapshot continuity. Closing without merge does not permanently ignore symbols: later new snapshots may select them again, and durable history/usage remains.

Compatible recovery can freshly load Roslyn and reconstruct/validate proposals and the candidate against the pinned base and commitments. This correctness validation is distinct from new plan discovery: it neither selects a new manifest nor regenerates accepted documentation. No automatic rebase, semantic migration or model-assisted safe rebuilding crosses a base change.

## One bounded Scribe conversation

One normal Action with model work gives Scribe the complete selected manifest and necessary identities, constraints and relationships at conversation start. Already accepted results are provided as validated progress, not regeneration assignments. Scribe may organize reading and processing within that manifest, reuse bounded evidence and terminology, and submit per-target or small-batch structured proposals/skips while the conversation continues. No work means no empty model conversation.

Deterministic host code validates each submission's selected-target identity, evidence references and documentation constraints, returns an incremental acknowledgement and checkpoints accepted progress. Foreign or duplicate targets cannot advance state. Submission and final completion are separate: the host checks coverage against the complete manifest rather than trusting a model's statement that everything is finished. One invalid proposal or failed target must not erase other accepted results.

Lazy read-only tools obtain bounded source, signatures, usages, tests and maintained documentation as needed. Reuse deduplicates by source/content identity, revision and range, while preserving instruction-versus-evidence roles and target-specific style/authority. Shared context does not make a target's facts or instructions applicable to another target. Stable reusable prompt content and opaque assistant continuation stay within their validated scope; provider cache hits are observations, never correctness or recovery authority.

Transient provider errors get finite retry; invalid proposals get concrete rejection reasons and finite repair. Insufficient evidence may skip. Skips/failures remain unresolved and do not auto-loop on the same snapshot; a new snapshot may reconsider them. Exact retry/repair constants and wire layouts remain open to the minimum adequate implementation.

Controlled resource or context stops save progress and publish a safe validated subset when possible. Publication seals that subset and ends model work while awaiting human review; unfinished targets return through a later allowed fresh Audit. An abrupt unpublished interruption restores the original fixed batch, reconstructing accepted results without regeneration. A later Action starts a new bounded conversation for remaining work. M7 restores neither a complete provider conversation nor hidden reasoning, and performs no automatic context compaction to continue beyond the limit.

Scribe receives no shell, arbitrary edit, state-store, GitHub, credential, web or subagent capability. Core owns platform-neutral plan/state/accounting, deterministic Patching owns candidate source changes, GitHub owns authenticated platform mutations, and CLI alone composes them. The Action wrapper remains a thin host.

## Complete coordination-ref recovery and publication

One stable coordination Git ref per consumer-repository campaign stores the complete recovery state and publication facts. Preserve the existing deterministic owner/name + targetRef + campaignLineage naming; M7 adds no full-ref override. Compare-and-swap uses the exact expected predecessor and authenticated readback. Artifacts are diagnostic exports only, never the next invocation's required recovery authority.

Persist claim/reservation before provider dispatch. Persist incremental accepted, settled and unknown facts so a fresh runner can recover after an interruption, lost response or overlapping invocation without duplicate work or understated exposure. Known actual usage, in-flight exposure and conservative charges are separate. Complete history remains available even when aggregate caps are unlimited or an old proposal was never published/merged.

The consumer coordination history may contain only bounded host-validated structured generated documentation and the necessary identities, commitments, statuses, claims, usage and recovery/publication facts. This allowance includes unpublished and unmerged proposals; retention does not make them applied source or evidence of main completion. Never persist source-evidence bodies, complete context, prompts, raw provider replies/responses, transcripts, reasoning, secrets or candidate bytes. Reconstruct candidate files through the deterministic patch engine and freshly validate against bound inputs. Consumer recovery history does not authorize publication of private downstream material in ContractScribe's own repository, reports or CI artifacts. See [Security boundary](../20_architecture/security-boundary.md).

A fixed batch has at most one PR and a campaign at most one active PR. Published draft/ready PRs never accept more targets. A partially published result seals only its validated subset; original-operation recovery cannot add the next batch. Every generation preserves terminal predecessor and immutable base facts. Human changes and conflicts pause rather than being overwritten; no automatic ready, merge, close or deletion is introduced.

## Shared resources and initial defaults

Campaign aggregate resource caps are optional and default unlimited. Retain complete known and unknown history so later configured finite caps have a real accounting basis. Zero is a value, not an unlimited sentinel; the exact unlimited representation belongs to the consumer-schema owner. Every Action shares its ceilings across all selected targets, reads, retries and repairs, without renewing resources for each target or reserving a whole Action maximum for every target.

Remove PR documentation-block, changed-file and cumulative patch-size business quotas. The batch creation quota bounds target membership, not bytes or diff lines. Necessary parser, transport, context and deterministic patch safety bounds remain. Owning implementations align hidden legacy request/tool-round/token/time/byte caps so they do not silently defeat trusted larger execution settings; avoid multiple overlapping business-budget systems.

These are initial configuration defaults, not measured production guarantees or values already implemented by C1:

| Limit | Initial value and scope |
| --- | --- |
| Distinct targets per Action / new batch selection | 100, configurable |
| Provider requests per Action | 64; trusted configuration may raise to 128; retries consume this ceiling |
| Tool calls per Action | 512, across reading, searching and structured submissions |
| Tool calls per assistant response | 16 where the runtime needs a separate bound |
| Actual uncached input per Action | 2,000,000 tokens |
| Actual cached input per Action | 38,000,000 tokens |
| Output per Action | 524,288 tokens, including reasoning |
| Output per provider request | 65,536 tokens, also bounded by remaining output allowance |
| Agent wall time | 15 minutes, including provider, tool, retry and wait time |
| Complete provider request | 5 minutes including response reading, bounded by remaining agent time |
| Provider connect timeout | 15 seconds |
| Caller workflow job recommendation | 30 minutes, including preparation, recovery, publication and cleanup |

When known, total input equals cached hit plus uncached miss. Reasoning is an output subset and is never added to output a second time. Missing usage is not zero. A completed request's actual usage can stop later requests even if exact usage was unknowable before dispatch; unknown dispatched work retains exposure/conservative accounting. Exhausting one Action does not permanently terminate the campaign; an explicit aggregate cap is separate.

## Per-invocation observations

Provide versioned machine-readable usage JSON, the same usage in the result envelope and Action output, and a human-readable step summary. Field names and exact schema belong to the implementing CLI/Action contracts; required semantics are:

| Observation | Required meaning |
| --- | --- |
| Work | Selected, started, accepted, skipped, failed and remaining targets, distinguishing reconstructed prior progress from new invocation work |
| Calls | Provider, tool and retry counts with explicit logical-call versus actual-dispatch boundaries |
| Tokens | Total input, cached input, uncached input, output and its reasoning subset; known values separate from missing values |
| Accounting | Actual usage, unknown facts/request count, in-flight reservations and conservative charges distinguished |
| Timing | Agent and observable CLI/Action elapsed time with their measured scopes |
| Termination | Stop reason, including completion/no-op, target/resource/context limit, awaiting review, closed-same-base pause, changed-base pause and failure |
| Completeness and linkage | Complete or partially unknown measurement and invocation/campaign/snapshot/batch/checkpoint/PR linkage |

Observations describe this invocation. Restored historical provider usage is not charged as new Action work; any campaign totals are separately labeled. No-provider no-op/await/pause gates report zero new requests and provider usage. A sent request with a lost result, timeout or missing usage reports known values plus explicit unknowns. Optional estimated cost binds currency/rate-policy and remains distinct from provider billing.

Normal stops and controllable failures emit observations where possible. Hard kills cannot guarantee final outputs/summary; durable incremental facts let the next runner recover without charging historical work as new usage or pretending an unknown result consumed nothing.

## Exit evidence and semantic examples

M7 completion requires credential-free ordinary CI through production paths, plus separately authorized bounded opt-in live synthetic GitHub/provider observations on the exact merged revision. Paid/provider or credential-bearing proof and operator merges require their own execution authority. No historical M6 run substitutes for this evidence.

| Scenario | Expected evidence |
| --- | --- |
| 12 targets with quota 5 | PR with 5; open-period zero-provider wait; operator merge; fresh Audit sees 7; PR with 5; merge; fresh Audit and PR with 2; merge; final no-op. |
| 95 selected, next group 11, quota 100 | Select 95 without skip-ahead packing; after merge a fresh Audit selects the remaining group if still pending. |
| Oversized group with 230 targets | Smaller semantic splits then stable chunks make progress within quota without starvation. |
| Partial/nested types, overloads and different instruction/style scopes | Correct canonical targets, nested independence, stable grouping and evidence/instruction confinement. |
| Shared session and incremental submissions | Complete manifest visible initially, bounded reused reads, independent proposal validation/acknowledgement, foreign/duplicate/omitted targets cannot claim completion, safe partial candidate validation. |
| Quota raised or lowered during unpublished recovery | Original membership unchanged; smaller invocation starts only its allowed distinct targets; accepted results reconstructed without regeneration. |
| Controlled partial publication or context stop | Valid subset safely published/sealed; wait for review; remaining work needs a later allowed fresh Audit; no automatic compaction. |
| Fresh-runner interruption, overlapping runs, lost GitHub responses | Complete-ref checkpoint restores original batch/claims/usage; exact predecessor CAS and original-operation recovery prevent overwrite, duplicate work and duplicate PRs. |
| Unknown provider results or usage | Accepted/settled/unknown history survives; actual, missing, in-flight and conservative quantities remain distinct. |
| Open same-base PR or open changed-base PR | Await or pause before discovery/provider; draft/ready never receive new targets; ownership/human changes/conflicts stop unsafe mutation. |
| Closed-unmerged on same base, including config/quota changes | Pause before discovery/provider; configuration cannot unlock that base. |
| Closed-unmerged plus README-only new HEAD, no active PR | Fresh Audit may rediscover the same unmerged targets; never subtract unmerged proposals; preserve history/usage. |
| Unpublished correctness-input change without a lifecycle gate | Fresh snapshot/Audit without proposal migration; budget-only changes retain compatible fixed membership. |
| Finite retry/repair/skip, invocation limits and optional campaign caps | Retries share resources; skip/failure remains unresolved without same-snapshot auto-loop; default unlimited campaign still records complete history. |
| No-op, partial success, timeout and missing usage | Correct per-invocation counts/partitions/stop reason/completeness and no recharging restored history; controllable failures emit observations. |

Representative same-target comparisons report documentation quality/completeness and measured provider calls, cached/uncached input and output. Cache ratio alone proves neither savings nor quality improvement. Live comparisons are bounded opt-in evidence, not a general support or pricing guarantee.

## Delivery and open implementation choices

The [master graph](https://github.com/SolusQuest/contract-scribe/issues/208) owns live tracker dependencies and exit evidence. C1 adopts this plan; dependent leaves implement fixed selection, shared budgets, complete recovery, multi-target request/tools/runtime/patch composition, GitHub lifecycle, automatic CLI/Action integration, observations and production-path tests. Each leaf delivers one focused coherent PR at its accepted boundary, with no alternate legacy production runtime or compatibility framework retained merely for pre-release history.

Exact semantic grouping algorithms, checkpoint/usage JSON layouts, proposal/acknowledgement protocol, finite retry constants, claim/settlement ordering and context byte limits remain implementation choices within these product boundaries. Do not publish placeholder certification packages, introduce another general agent framework/project, or freeze release contracts in C1. Apply [pre-release proportionality](../00_project/pre-release-engineering.md) and [contract lifecycle](../00_project/contract-lifecycle.md).

## Exclusions

M7 excludes automatic rebase/semantic migration/model-assisted safe rebuilding; multiple active campaign PRs; cross-batch append; automatic merge; full hidden conversation restoration; automatic context compaction; sequential runtime group feeding; external scheduler construction; new general agent frameworks/projects; shell, arbitrary source edits or subagents; public Release/Marketplace and licensing/contribution governance. #29/#189 remain their separate post-M7 tracks.

## Current protocol references

Until their code/schema owners change them, [Campaign Planning v1](../20_architecture/contracts/campaign-planning-v1.md), [Campaign State v1](../20_architecture/contracts/campaign-state-v1.md), [GitHub Publication v1](../20_architecture/contracts/github-publication-v1.md), [github-proposal CLI](../20_architecture/github-proposal-cli.md), [Action interface](../20_architecture/action-interface.md) and [consumer configuration](../20_architecture/consumer-configuration.md) describe current implementation. Future-planning differences are deliberate and labeled; C1 neither changes their executable shapes nor authorizes applying M7 assumptions to old state. Historical M4–M6 evidence remains bound to its own revision.
