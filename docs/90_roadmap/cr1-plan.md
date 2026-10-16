# CR1 — Adopt SolusAgent for the Documentation Scribe

> **Status:** Plan adopted; qualification and implementation pending. Production still uses ContractScribe's current single-target runtime. Adoption of this plan does not claim migration completion or authorize a release.

## Outcome and boundary

Replace the general agent execution machinery with an exactly pinned, published SolusAgent internal package set. ContractScribe continues to own the narrow Documentation Scribe role, scoped instructions and evidence, semantic/read tools, proposal and skip validation, campaign policy and persistence, deterministic patching and GitHub publication. CLI remains the composition root. No model capability receives mutation, state-store, credential, shell or publication authority.

CR1 runs alongside [M7](m7-plan.md). It replaces the current execution foundation without requiring M7 to finish on the old runtime. It does not complete M7's multi-target shared conversation, cumulative candidate, lifecycle or automatic-continuation outcomes. CR1 preserves the invocation-budget and dispatch-accounting behavior already implemented by M7-C4 (#216); only remaining, unimplemented M7 capabilities stay with their existing owners. Historical M3–M6 and completed M7 acceptance remain bound to their original revisions.

Use public SolusAgent APIs and consistent exact direct package pins. Keep concrete runtime/provider construction at startup; retain `ContractScribe.Agent` for the Scribe-specific adapter. Core remains free of concrete runtime/provider dependencies. Existing project/reference and capability tests must be adapted to the actual new boundary, not removed wholesale. No new assembly is justified solely by package names, and no second general loop remains in production after cutover.

## Delivery outcomes

CR1 needs no coordination parent or separate tracker. The milestone, this plan and the two outcome contracts provide its scope, dependency and acceptance view.

1. **CR1-Q1 — Establish the supported SolusAgent integration contract.** Produce an evidence-backed package/API selection and Scribe mapping, with the real producer/consumer constraints exercised through existing deterministic seams where source inspection is insufficient. This is a bounded decision/qualification outcome, independently useful even if it identifies a specific upstream gap. A passing result selects a published version and makes the implementation contract ready; a blocked result names the upstream owner, missing contract and acceptance evidence without claiming the migration is ready.
2. **CR1-I1 — Run the production Documentation Scribe on SolusAgent.** Once a passing Q1 result and its required published dependencies exist, replace the old loop, integrate product-owned tools/accounting/acceptance and migrate the actual CLI, campaign and evaluation consumers in one coherent implementation PR. Include tests, affected contracts/docs and actual packaged CLI/Action validation in that outcome. Its draft is blocked, not agent-ready, while Q1's material choices remain unresolved.

The separate Q1 outcome prevents implementation from assuming an unsupported provider/schema/capacity contract. A source-only decision that is already proved should not grow a new certification framework. Q1 uses at most one focused contract/qualification PR; I1 uses one coherent implementation PR. There is no extra cleanup or validation issue whose sole purpose is making an otherwise incomplete cutover usable.

The essential chain is Q1's passing result and available dependencies → I1 → runtime-dependent M7 work. Q1 may prepare independent boundary checks together; partial qualification is not enough to start the cutover. Independent M7 GitHub/lifecycle work keeps its existing prerequisites. Low migration parallelism is justified by the single runtime cutover and the unresolved predecessor contract, not by shared file names alone.

## Qualification questions

The available `0.1.0-internal.1` package source is [SolusAgent `076bcda2fbe1da2677ac5c6fd03d75bc0fef66ad`](https://github.com/SolusQuest/solus-agent/tree/076bcda2fbe1da2677ac5c6fd03d75bc0fef66ad). Package installation and synthetic consumer evidence establish availability, not actual ContractScribe equivalence. Qualify at least:

| Surface | Required result |
| --- | --- |
| Classified input and read-only tools | Preserve instruction/data authority, bounded reads, rich evidence and all domain validation. The current closed scalar-object tool schema requires an explicit supported representation or upstream change. Never silently discard constraints. |
| Provider configuration | Preserve the selected `deepseek-v4-flash` profile and configured endpoint/model/control semantics. The built-in `.1` DeepSeek adapter fixes `deepseek-flash` and construction-time output bounds; it is optional. Select a supported provider integration without a silent model change or local generic runtime fork. |
| Capacity and bounds | Compare actual supported single-target/campaign limits and byte/record ceilings, separately from M7's later shared-conversation requirements. `.1` caps retained attempts/records at 64; M7 permits up to 128 requests. No hidden lowering or unsupported readiness claim. |
| Candidate and Host feedback | Product validators retain proposal/skip authority, finite repair and safe partial outcomes. A runtime final response or Host feedback acknowledgement is not patch or publication acceptance. |
| Dispatch and settlement | Bind public hooks to the actual durable reservation/checkpoint flow; preserve physical attempts/retries, known/unknown observations, conservative exposure, restored history and denied-dispatch behavior. Library in-memory accounting is not durable storage evidence. |
| Current consumers | Include CLI, campaign, evaluation and packaged payload behavior. Future Actions restore verified business progress into fresh executions; full conversation/reasoning restoration is excluded. |

Generic package gaps belong to SolusAgent. Record precise cross-repository dependencies with full URLs when they exist; upstream issue creation, implementation and package publication retain their own authorization. A later published version may be selected on evidence. Do not copy a missing general runtime implementation into ContractScribe to conceal a gap.

## M7 handoff

Preserve existing issue identity, type, milestone, parentage and historical completion. Reconcile only current execution assumptions and real direct prerequisites:

| Existing owner | Retained outcome after CR1 |
| --- | --- |
| [#217 / M7-S1](https://github.com/SolusQuest/contract-scribe/issues/217) | Multi-target manifest, scoped evidence/instructions and current consumers; use the adopted runtime adapter. Its runtime-dependent cutover follows CR1-I1. |
| [#218 / M7-S2](https://github.com/SolusQuest/contract-scribe/issues/218) | Product request-level usage and unknown-outcome semantics for the shared execution path; consume library observations rather than build a second provider engine. |
| [#219 / M7-S3](https://github.com/SolusQuest/contract-scribe/issues/219) | Remaining-budget provider bounds and adopted M7 capacity; consume the selected provider/runtime contract. |
| [#220 / M7-S4](https://github.com/SolusQuest/contract-scribe/issues/220) | Multi-target incremental acknowledgement, truthful coverage and shared-session product behavior using the candidate API. |
| [#221 / M7-S5](https://github.com/SolusQuest/contract-scribe/issues/221) | Durable cumulative candidate acceptance and recovery through existing product authorities. |

These remain executable product outcomes. CR1 does not close them by inference from upstream acceptance. If qualification proves an outcome is already delivered by the actual migration, reconcile its precise remaining scope against evidence before scheduling duplicate implementation; preserve historical state unless separately authorized.

## Acceptance and validation

- The actual production Scribe consumes one consistent published SolusAgent package set, with no sibling source references, private API use, alternate legacy loop or bypass in the evaluation harness.
- Existing accepted confinement, authority, proposal/skip, budget, accounting, cancellation/failure and business-progress recovery semantics pass through the replacement path. Do not present planned M7 capabilities as existing preservation evidence.
- Deterministic product tests cover success, rejection/repair, partial progress, cancellation/timeout, missing usage and checkpoint denial/failure, including no new effects after denied authority. Reuse existing suites and fixtures; ordinary tests make no live provider calls.
- Applicable focused tests, contract/schema and architecture checks, and exact-head Ubuntu CI pass under [test validation](../50_ai/skills/test-validation.md). The actual packaged CLI/Action path exercises the selected dependency set. Build-time private-feed credentials do not become runtime payload requirements or contents.
- Remaining M7 work, precise upstream blockers and release gates have explicit owners. Package restore alone, historical M6 evidence and synthetic library consumers cannot close CR1.

## Non-goals and execution authority

No full M7 delivery, MiMo/general-provider expansion, shell/edit tools, subagents, automatic compaction, full transcript persistence, speculative compatibility layer or historical reopening. First publication, redistribution/licensing, live/paid provider proof, GitHub live mutation experiments and payload-map reauthorization remain separate gates. The current payload map was revoked by [#241](https://github.com/SolusQuest/contract-scribe/pull/241); package-path validation does not reauthorize it.

Planning/tracker publication does not authorize product implementation, merge, upstream writes or release. Governing documents must be reachable from `main` before structural publication; [issue workflow](../10_workflow/issue-workflow.md) governs the reviewed synchronization and readback.
