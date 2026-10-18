# M7 remaining-scope audit after CR1

Status: adopted scope reconciliation upon merge. Tracker synchronization follows the merged documentation; this audit does not claim implementation completion. Baseline: `98ec9f0d41cb070c50679bd925c9dc7a18c31822`, verified reachable from main on 2026-10-18. CR1 #243/#244 and PRs #246/#247 are complete. Existing M7 product decisions remain authoritative. This audit uses source inspection, existing test assertions and recorded PR CI; it does not claim a new test run or complete M7 acceptance.

## Adopted scope

Retain M7's product scope and existing issue identities. Remove S2/#218 and S3/#219 as standalone future implementation deliveries after transferring every remaining acceptance obligation below. Their singleton foundations already exist; their shared-session obligations must be accepted with the real producer/consumer in S1, S4 and S5, not through isolated qualification PRs. The disposition is superseded/not planned, not completed. Also supersede A2/#227 after explicitly transferring its complete acceptance to A1/#226; A3/#228 directly depends on the automatic CLI/Action contract delivered by A1. Until governing docs merge and an authorized tracker synchronization occurs, all three issues remain open and the existing graph remains operative.

## Evidence and remaining outcomes

| Owner | Existing evidence / current limitation | Remaining delivery |
| --- | --- | --- |
| C1–C4 #213–#216 | All closed; fixed batches, optional campaign caps and shared invocation accounting implemented. | Preserve completion. C tracker #209 is eligible for administrative closeout after confirming linked leaf acceptance; no extra implementation or rerun. |
| G1 #222 | Closed; candidate-free authenticated observation implemented. | Preserve completion. |
| S1 #217 | `DocumentationScribeRequest.Target` and `CampaignScribeRequestBuilder` still require one target. | Complete bounded manifest, per-target instructions/evidence and ordinary singleton consumer cutover on the production adapter. Include manifest capacity/admission and preservation of existing accounting. |
| S2 #218 | Physical exchange reserves and settles every dispatch; nullable usage, retries, lost response and restored retry waits already have production regressions. | No separate provider/accounting engine. Transfer manifest correlation checks to S1, incremental/retry checks to S4, durable accepted-progress checks to S5. |
| S3 #219 | Defaults already specify 15s connect, 300s request, 900s invocation, 65536 request output. Product request uses remaining output; physical permit enforces remaining time. SDK capacities explicitly opt in. | Transfer representable manifest/bounded context input to S1, accumulated conversation/context stop and incremental remaining-resource behavior to S4. Do not treat SDK maxima as promised simultaneous capacity or raise every current safety cap automatically. |
| S4 #220 | Production still converts one terminal submission to Final; invalid singleton output ends rather than repairs. | Incremental submit/ack/finish, truthful coverage, bounded repair, exact continuation, shared budgets and safe partial stops. |
| S5 #221 | Existing checkpoint and cumulative patch authorities are useful, but campaign request/runtime remains singleton. | Persist incremental accepted multi-target progress, compose/reconstruct cumulative candidates and preserve valid siblings. |
| G2 #223 | Current production checkpoint store is file-backed; G1 supplies observation only. | Complete ref-backed checkpoints/CAS/claims before provider dispatch; current explicit consumers work. |
| G3 #224 | Current explicit consumers and append semantics do not implement full M7 lifecycle precedence. | Authenticated pre-discovery gates, changed-base and closed-same-base behavior, no-provider outcomes. |
| G4 #225 | `SameSnapshotAppend` still exists; publication business quotas remain in defaults. | Seal fixed published result set, recover exact original operation and remove business quotas while retaining structural safety. |
| A1 #226 | CLI/Action still use explicit start/resume; current CI already has a full acquisition/install/invoke test path. | Automatic CLI plus atomic current Action/caller cutover, matching payload proof, stale/mismatched payload rejection and caller guidance transferred from A2. |
| A2 #227 | Its complete acceptance is necessary for A1 to expose a usable and safe automatic Action. No separate release, platform or acquisition mechanism is requested. | Supersede standalone delivery; transfer every obligation to A1, retaining identity/history and no claim of completed M7 behavior. |
| A3 #228 | Current invoke/emit scripts expose the existing envelope and convenience fields; they do not produce the required usage contract. | Add CLI usage producer, Action projection and summary coherently after A1; direct blocker becomes #226. |
| A4 #229 | Existing tests establish foundations, not the whole multi-target continuation lifecycle. | Fresh-process production-path matrix and prepared bounded live observation procedure. |
| A5 #230 | No new live observation performed. | Exact-revision synthetic live GitHub/provider evidence under separately specified execution authority. |

## Exhaustive S2/S3 acceptance transfer

| Original obligation | Delivered foundation | Remaining owner and acceptance |
| --- | --- | --- |
| S2 known/partial/unknown usage, cache partitions and reasoning subset | `DocumentationScribeSolusAccountingTests` partial-cache and missing-usage/lost-response cases; provider codec tests. | S1 preserves transport/typed observations under manifest request changes; S4 tests multiple incremental submissions with known/partial/unknown observations and no double reasoning charge. |
| S2 physical/logical/retry correlation and exact continuation | `DocumentationScribePhysicalExchange`, session provider adapter and HTTP continuation tests. | S1 preserves commitments/correlation; S4 tests continuation across submit/ack and finite repair/retry, with actual send count and no counter reset. |
| S2 no historical double charge or exposure-as-measurement | C4 invocation budget and restored durable retry-wait production tests. | S4 restores accepted progress into fresh conversations without regeneration; S5 proves durable usage/proposal recovery without new-invocation recharging. G2 later verifies remote-store recovery. |
| S2 no raw continuation/provider prose/secrets in reports/state | Existing closed transport/diagnostic contracts. | S1/S4/S5 preserve this boundary in their affected contracts and tests; A3 tests the new output boundary. |
| S3 connect/full-request/shared deadline including body and retry wait | HTTP handler, physical permit and C4 deadline ownership; Solus deadline regressions. | S4 tests slow connect/headers/body, retry/repair, cancellation and timeout across incremental turns without deadline renewal; S1 preserves transport configuration. |
| S3 output min(65536, remaining) and no dispatch after depletion | Session ProductRequest and invocation allowance. | S4 proves decrement across multiple incremental submissions and zero subsequent dispatch at exhausted output/time. |
| S3 supported capacity and logical request/response byte bounds | Explicit SDK opt-in and codec/raw/logical bounds; default context/evidence limits remain finite. | S1 defines manifest representation and scoped/lazy evidence capacity with representative default-quota/oversized-group fixtures, rejecting excessive admission safely. S4 proves cumulative history exhaustion produces truthful accepted progress without automatic compaction. No numeric schema is frozen by this audit. |
| S3 no pricing/fallback/secret/live-call expansion | Existing adapter confinement. | S1/S4 retain these exclusions and credential-free tests. |

S1 remains independently usable: ordinary singleton consumers use the manifest shape and a multi-target request can end with truthful partial coverage until S4. S4 remains independently testable through deterministic Host acknowledgement and adapted current singleton campaign consumers; it must not claim durable multi-target campaign acceptance, which belongs to S5. S5 owns acceptance-critical durable recovery and patch integration in the same PR.

## A1/A2 boundary and A3 dependency audit

A1 and A2 overlap at the automatic CLI/Action cutover. The source and acceptance audit below establishes that A1 must absorb the complete A2 contract; it does not claim that future automatic behavior already passes acceptance.

### Complete A2 obligation transfer to A1

| A2 scope/acceptance obligation | Baseline evidence and gap | Receiving A1 acceptance |
| --- | --- | --- |
| Matching current payload acquisition/install/invoke binding; no manual artifact handoff | A1 already requires atomic current Action input/prepare/invoke, caller/example and packaged-process cutover. `case_real_chain` executes prepare → acquire → install → invoke. Current code still uses start/resume. | In A1's same PR, repeated identical consumer inputs on fresh runners reach the automatic CLI without previous artifact IDs or wrapper checkpoint parsing. All current callers use the new contract. |
| Current-source paired conformance, not historical M6 evidence | `ci.yml` action_packaged rebuilds the checked-out CLI, compares it to producer bytes and calls the current wrapper's `verify-action.sh --real` against loopback fakes. | Adapt and run that matching current-source path in A1; use its applicable evidence once. No second qualification-only PR, new certification lifecycle or historical payload substitution. |
| Stale/mismatched payload rejection rather than old-contract execution | Acquire verifies authorized map/digest; install verifies manifest identity, inventory and files. These integrity checks alone do not prove semantic compatibility with a new automatic command. | Explicit A1 negative acceptance: exercise a stale but internally valid old-contract payload paired with the new wrapper, and an identity/digest mismatch; reject without silently running start/resume or causing provider/product-GitHub effects. Reject unsupported current result contracts safely. Use existing contract/command admission and test seams; exact mechanism remains implementation-owned, with no speculative compatibility framework or required new format. |
| Manual/best-effort schedule/external triggers, campaign concurrency, 30-minute caller-job recommendation | Existing examples have dispatch/schedule, concurrency and 30-minute jobs, but still use activation/artifact handoff. A1 already owns example/helper/usage-guide replacement. | A1 updates matching consumer documentation and examples for all accepted trigger sources, stable campaign concurrency and the 30-minute recommendation. No reliable schedule promise, external scheduler, or wrapper-owned job timeout. |
| Separate acquisition/product/provider credentials and thin wrapper authority | `action.yml` scopes credentials by step; invoke strips acquisition credentials and leaves lifecycle decisions in the CLI. | A1 preserves and tests credential separation, disabled/default-inert behavior and no wrapper-owned ledger/PR policy. No new credential use. |
| Fresh-runner automatic invocation and no new public distribution promise | Production payload map is currently `payload:null`; current-source CI uses confined synthetic overrides. | Test automatic behavior with the current-source wrapper/payload pair. Production null-map refusal remains valid; A1 does not reauthorize the map, publish a Release/tag/Marketplace artifact or establish redistribution rights. |

A2 has no remaining independently acceptable deliverable after these obligations are included in A1. The missing semantic stale-payload assertion is a necessary part of the command/wrapper cutover, not evidence for a later independent gate. Merely preserving acquire/install files does not require rewriting them; adapt only affected behavior and reuse valid existing checks. The future A1 implementation must prove these assertions; this source audit does not claim those future tests pass.

### A3 remains a distinct outcome

A1 delivers a usable automatic CLI and matching thin Action with coherent existing result/outcome/diagnostic handling. A3 adds the versioned invocation usage producer and result envelope, Action JSON output and bounded human summary: work/call/retry/token/time/stop/completeness/linkage fields, unknown usage and restored-history separation. Current `invoke.py` physically validates and forwards the envelope; `emit.py` and `action.yml` project existing fields. A3 must update the real CLI producer, exact output assertions, invoke/emit/output consumers and affected packed tests in one PR, not just add a wrapper label or scrape logs.

The direct predecessor is A1's executable automatic invocation/result contract, including no-provider wait/pause and controlled-failure paths. A2 supplies no additional independent interface. Earlier C4/S4/S5 and G work supply accounting and recovery transitively through A1; no redundant direct edges are needed. A3 should not ship against the soon-retired start/resume contract or guess a future automatic envelope. Preparatory field analysis can overlap; executable contract cutover follows A1. A1's ordinary automatic result remains useful before the richer A3 usage contract, so A3 need not be folded into A1. A4 remains dependent on A3 to validate the end-to-end usage/lifecycle matrix, with existing tests reused.

## Dependency changes and serial-chain audit

Pre-reconciliation chain: C4 + CR1-I1 → S1 → S2 → S3 → S4 → S5 → G2 (also G1) → G3 → G4 → A1 → A2 → A3 → A4 → A5.

Remaining chain: S1 → S4 → S5 → G2 → G3 → G4 → A1 → A3 → A4 → A5. S1's C4/CR1-I1 prerequisites and G2's G1 prerequisite remain recorded and satisfied. Remove managed edges S2←S1, S3←S2, S4←S3 and add S4←S1. Also remove A2←A1 and A3←A2; add A3←A1. Preserve identities, native types, milestone membership and parentage, including superseded S2/S3/A2 nodes. No new issue, parent or milestone is needed.

The reconciliation retains 10 executable leaves instead of the original 13, not a duration estimate or a mandate to manufacture ten PRs if later evidence eliminates further work. S4 needs scoped manifest authority; S5 needs acknowledgement/result semantics; G2 needs the resulting durable checkpoint/proposal representation; G3 needs authoritative complete recovery; G4 needs lifecycle predicates; A1 needs sealed publication/recovery; A3 needs the automatic CLI/Action result contract; A4 validates that integrated lifecycle and usage; A5 needs the tested path and separate live authority.

Considered parallelism: G2 transport/store research and G3 lifecycle tables can be prepared while S work progresses, but current complete-store acceptance consumes changing state/authority paths. A3 field analysis may overlap with A1, but its concrete producers/consumers must target A1's final contract. No placeholder contract, second store, legacy wire bridge or extra qualification PR is introduced solely to create a parallel lane. Dependencies follow consumed results rather than numbering; re-evaluate an edge only if an earlier sufficient stable outcome is established.

## Publication and validation boundary

Tracker synchronization follows the merged governing docs, a reviewed field-level manifest, fresh native relationship reads, preserved native types and complete changed-graph readback. Update #208/#210/#212 coordination text and #217/#220/#221/#226/#228 contracts; mark #218/#219/#227 superseded with retained acceptance/evidence and transfer links; remove/add only the dependency edges named above. Publish receiving acceptance before supersession. #209 administrative closeout remains separate. Master #208, S/G/A trackers and milestone remain open. Unaffected leaves and completed leaves receive no writes.

| Targets | Synchronization operation | Preserved fields |
| --- | --- | --- |
| #208, #210, #212, #217, #220, #221, #226, #228 | Update bodies to the remaining outcomes and acceptance transfers above; retain open state. | Titles, native types, parentage, milestone, labels and assignees. |
| #218, #219, #227 | Record supersession and receiving owners; close as `not_planned`, without claiming completed implementation. | Titles, native types, parentage, milestone, labels and assignees. |
| #218←#217, #219←#218, #220←#219, #227←#226, #228←#227 | Remove these native `blocked_by` relationships. | All other relationships. |
| #220←#217, #228←#226 | Add these native `blocked_by` relationships. | S1's satisfied #216/#244 prerequisites; G2's #222/#221 prerequisites; all unrelated relationships. |

This documentation adjustment changes no machine contract. Per-implementation tests and docs stay with their current owners; no new certification framework, detached schema PR or historical reopening. Validate Markdown/link integrity and issue-draft consistency for this adjustment; execute affected tests and required Ubuntu CI during implementation. A live test, release, payload-map reauthorization and public redistribution remain separate gates.

## Inspected sources

- [src/ContractScribe.Core/DocumentationScribeContracts.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Core/DocumentationScribeContracts.cs)
- [src/ContractScribe.Core/DocumentationScribeInvocationBudget.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Core/DocumentationScribeInvocationBudget.cs)
- [src/ContractScribe.Cli/Campaign/CampaignScribeRequestBuilder.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Cli/Campaign/CampaignScribeRequestBuilder.cs)
- [src/ContractScribe.Agent/Runtime/DocumentationScribeSession.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Agent/Runtime/DocumentationScribeSession.cs)
- [src/ContractScribe.Agent/Runtime/DocumentationScribePhysicalExchange.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Agent/Runtime/DocumentationScribePhysicalExchange.cs)
- [src/ContractScribe.Agent/Runtime/RunState.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Agent/Runtime/RunState.cs)
- [src/ContractScribe.Agent/Providers/OpenAiCompatibleHttpModelExchange.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Agent/Providers/OpenAiCompatibleHttpModelExchange.cs)
- [src/ContractScribe.Cli/DocumentationScribeExecution.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/src/ContractScribe.Cli/DocumentationScribeExecution.cs)
- [tests/ContractScribe.Tests/DocumentationScribeInvocationBudgetTests.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/tests/ContractScribe.Tests/DocumentationScribeInvocationBudgetTests.cs)
- [tests/ContractScribe.Tests/DocumentationScribeSolusAccountingTests.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/tests/ContractScribe.Tests/DocumentationScribeSolusAccountingTests.cs)
- [tests/ContractScribe.Tests/DocumentationScribeSolusToolTests.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/tests/ContractScribe.Tests/DocumentationScribeSolusToolTests.cs)
- [tests/ContractScribe.Tests/DocumentationScribeProviderTransportTests.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/tests/ContractScribe.Tests/DocumentationScribeProviderTransportTests.cs)
- [config/defaults.json](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/config/defaults.json)
- [action.yml](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/action.yml)
- [scripts/action/prepare.py](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/scripts/action/prepare.py)
- [scripts/action/acquire.py](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/scripts/action/acquire.py)
- [scripts/action/install.py](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/scripts/action/install.py)
- [scripts/action/invoke.py](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/scripts/action/invoke.py)
- [scripts/action/emit.py](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/scripts/action/emit.py)
- [scripts/action/payload-map.json](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/scripts/action/payload-map.json)
- [tests/action/verify-action.sh](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/tests/action/verify-action.sh)
- [.github/workflows/ci.yml](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/.github/workflows/ci.yml)
- [examples/github-actions/contract-scribe-campaign.yml](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/examples/github-actions/contract-scribe-campaign.yml)
- [docs/10_workflow/action-usage.md](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/docs/10_workflow/action-usage.md)
- [tests/ContractScribe.IntegrationTests/GitHubProposalCliProcessTests.cs](https://github.com/SolusQuest/contract-scribe/blob/98ec9f0d41cb070c50679bd925c9dc7a18c31822/tests/ContractScribe.IntegrationTests/GitHubProposalCliProcessTests.cs)
