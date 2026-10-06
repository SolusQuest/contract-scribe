# Campaign State v1

> **Status:** Current pre-release in-process contract introduced by M4-C2 and
> completed with the pure reducer and conditional store port in M4-C3. It is not
> a physical storage implementation, migration format, or compatibility promise.

## Purpose and ownership

`CampaignCheckpointState` is one complete, bounded, privacy-safe snapshot of the current campaign lineage. `CampaignStateFactory` is the only public construction boundary and `CampaignStateJson` owns exact canonical bytes plus the artifact SHA-256. A consumer either accepts the whole checkpoint or fails closed; partial recovery and best-effort parsing are forbidden.

C2 owns the versioned state vocabulary, immutable typed state, validation, exact
JSON projection, current-context revalidation, trusted M3-to-M2 proposal
projection, M2 request reconstruction, and typed M2 result commitment. C3 owns
checked budget accounting, pure transitions, exact replay, and the conditional
store/readback port. Neither slice owns physical persistence, dispatch, retry
selection, patch execution, repository mutation, compatibility aliases,
migration, CLI wiring, publication, or GitHub state.

## Authority and identity

The initial factory receives the exact `CampaignPlanningInput`, the caller-accepted `CampaignWorkPlan`, and the current repository-relative `.sln`, `.slnx`, or `.csproj` input identity, reruns C1, and rejects any mismatch. The checkpoint separately binds:

- product/contract revision ID and content SHA;
- opaque snapshot binding, repository, explicit-input, and policy-authority commitments;
- a domain-separated commitment to the current input identity, without persisting the repository-relative path itself;
- target profile and C1 execution commitment;
- the complete typed campaign budget and Scribe limits;
- one persisted Scribe execution projection containing the concrete provider, model, Scribe protocol, and Tool Policy IDs, the exact C1 Agent Protocol, Tool Policy/Registry, and Provider/Model Request Profile content authorities from which those IDs were read, and a domain-separated binding commitment over the complete set;
- a target-independent, already validated style-configuration projection, represented only by a bounded ID and a domain-separated content commitment;
- one composite campaign-configuration commitment over all correctness-bearing C1 content authorities and ceilings.

`CampaignScribeExecutionAuthority` is serialized evidence only and cannot authorize current provider execution. A fresh process must call `CreateScribeExecutionCapability` with the exact current C1 execution policy and validated canonical Agent Protocol, Tool Policy/Registry, and Provider/Model Request Profile projections. The factory recomputes their content authorities, extracts the four concrete execution IDs, and returns a nonserialized, factory-owned `CampaignScribeExecutionCapability`. Current-context validation compares that capability's complete projection with the persisted projection. Admission, retry, invocation grant, completion, and trusted-proposal construction consume the capability; provider completion additionally requires the same capability object retained by invocation issuance. Parsing a checkpoint never mints a capability, and an internally coherent persisted provider/model/protocol/tool substitution therefore cannot authenticate itself. Concrete Style Profiles remain per-work C1 facts and are rebound when a trusted proposal is created. C2 never invents or reverses a global Style Profile DSL. `RepositoryContextRef` is process-local and is never persisted. The caller supplies it transiently when reconstructing an M2 request. The stable repository-relative input identity is committed when the checkpoint is created; current-context validation, trusted-proposal admission, and both active and accepted reconstruction reject a different input identity even when target, source, and evidence commitments collide across two inputs.

The artifact digest is lowercase SHA-256 over the exact canonical UTF-8 JSON bytes, including the one trailing LF. It is a wrapper property and is not serialized into its own preimage.

## Accepted-candidate origin

The current v1 root includes `acceptedCandidateOrigin` immediately after `candidateObservation`. It is null exactly when no accepted candidate exists. On first acceptance, or genuinely new cumulative accepted work, it is a self record with the current checkpoint revision and a null checkpoint SHA. It also records the immutable product revision, lineage, snapshot and campaign-configuration commitment plus the original bounded typed candidate observation. The effective self digest is the enclosing exact accepted artifact SHA; no self digest is serialized into its preimage.

At the first later C3 transition preserving that accepted membership, the final successor retains the original revision and captures the exact externally accepted predecessor SHA. A retained origin has a non-null SHA and a strictly older revision. Intermediate reservation-construction states never supply this digest. Every preserving transition, including provider progress, reservation, retry, settlement and stop, uses the same final-successor owner. Genuine new accepted work replaces the origin; snapshot supersession clears it. Missing origin or a missing retained hash is invalid and never means “use the latest checkpoint.”

The origin contains no GitHub data, candidate bytes, recursive artifact, history list or sidecar. Before M2 dispatch, reservation construction checks an encoded upper bound for both future candidate observations and bounded scope/scalar growth against the existing 4 MiB ceiling. This includes first/replacement acceptance and later self-to-retained growth. A settled reconstruction with different candidate facts remains representable and charged; it fails publication equivalence instead of discarding the executed invocation's accounting.

A nonserialized internal proof first validates the actual fresh request/result and exact current artifact. It then compares immutable scope, accepted membership/projection, complete changed-file observations and every typed validation-result field. Historical result framing substitutes only the retained original request SHA, after real fresh-request validation, and compares against the retained original result commitment. No public arbitrary-hash authority API is added. The proof relies on the existing protected-checkpoint model; it is not an independent historical signature or attestation. H1 consumes it only together with current live-session, candidate and payload validation.

## State model

The current v1 root requires `batch` and `targetProgress` immediately after `workItems`. The batch records its identity, complete-plan commitment, original creation quota and immutable ordered selected target keys. The progress array retains every complete-plan target's work association, `SymbolRef`, eligibility, dispatchability, semantic group and member-family commitments, and its visible status. Status is derived from the durable work/reservation/attempt state; contradictory serialized progress fails canonical readback. `acceptedCandidateOrigin` additionally binds `batchIdentity`. Every work row requires `attemptDisposition`, either `open` or `suppressed-at-attempt-limit`. Missing fields from the earlier draft v1 shape fail closed; no legacy reader or migration is provided.

Same-snapshot context validation replans using the saved creation quota. A lower current invocation limit cannot alter snapshot, complete-plan or batch identity; a higher limit cannot add membership. One process-local `CampaignInvocationTargetAllowance` fixes the deterministic remaining dispatchable subset for the entire invocation. Runner selection, proposal execution and Core admission all require selected-batch membership and this allowance. One durable authorized reservation is a target start. The allowance preserves saved batch order, includes already-started pending/retryable work independently of the new-target limit, and includes only the first limit-many never-started pending targets. A retry across invocations consumes no second distinct-target slot and remains possible at limit zero; request, attempt, token and time budgets still apply. Recovery of an already active operation and accepted-only reconstruction also remain possible at a new-target limit of zero. Accepted proposals remain proposal progress and do not claim that documentation was merged on main.

Target statuses distinguish `pending`, `deferred`, `active`, `retryable`, `proposal-complete`, `accepted-proposal`, `skipped`, `failed`, `suppressed`, `infrastructure-blocked`, `unsupported-current-executor` and `excluded`. Finite exhausted retryable attempts persist suppression per work item without silently renewing invocation quotas or suppressing the rest of the batch. Skipped/failed/unsupported targets remain unresolved. Infrastructure, credentials, cancellation and ambiguous operations retain their existing typed failure and recovery semantics.

A zero creation quota selects an empty fixed batch while retaining all otherwise eligible complete-plan targets as deferred, including declarations unsupported by the current executor. Deferred targets alone do not make the batch unresolved: its durable terminal is `complete/all-work-closed`, and the CLI reports `campaign.batch-complete` with exit 0. This terminal grants no provider or patch work; raising a later invocation limit cannot expand the saved empty membership. Excluded unresolved work still produces `unresolved`, and a genuinely empty complete work graph retains `no-work`.

The 16,384 complete-target and 4,096 work-row count ceilings are necessary bounds, not a guarantee that every combination of individually valid fields fits. Initial and changed-base checkpoint admission also validates the full canonical encoding against the existing 4 MiB byte ceiling, including excluded/deferred progress, work rows, binding fields and JSON escaping. A valid complete plan that exceeds it receives the typed `DocumentTooLarge` capacity rejection; the production consumer reports `campaign.checkpoint-too-large` before initial publication or predecessor replacement. Supersession validates the final successor including its predecessor summary even when the initial successor template fits; a byte-capacity rejection retains the exact predecessor artifact and its distinct `CheckpointCapacity` transition failure. Other authority, correlation and revision failures remain distinct. No target is truncated, quota does not shrink the complete manifest, and neither planner ceilings nor the artifact ceiling is expanded.

Initial admission additionally checks the exact first provider-reservation encoding when the selected batch and policy permit provider work. The probe shares the reservation producer, uses the fixed-width request digest, and grants no checkpoint acceptance or dispatch authority. Supersession checks this headroom on the final successor with carried charges and its predecessor summary; its empty-charge template cannot decide whether provider work remains affordable. Quota zero, terminal batches, exhausted budgets and policy stops do not require an unavailable provider reservation. A retained current-shape checkpoint whose admission or retry reservation exceeds the byte ceiling receives `CheckpointCapacity`, preserves the exact predecessor and unretired reservation, and reports `campaign.checkpoint-too-large` at the state layer before credential access or provider dispatch. The artifact format remains unchanged.

Provider admission and retry also reserve an encoded upper bound for a durable bounded completion: maximum-width settled observations, a closed outcome and result commitment, finite-attempt suppression, terminal fields and retained accepted-candidate origin. This private encoding probe is not a semantic checkpoint and creates no acceptance or dispatch authority. If a validated trusted proposal exceeds the complete checkpoint byte ceiling, completion persists the existing `CompletedOverBound` work outcome and budget terminal with its validated proposal commitment and exact settled usage, clears the active reservation, and consumes the completion lease once. It cannot enter M2 or publish a candidate. Authority and correlation failures remain distinct; no generic invalid proposal is converted to capacity. Missing provider observations retain the existing conservative accounting semantics, and neither budgets nor byte/count ceilings change.

Settlement exhaustion takes precedence over a derived fixed-batch `complete/unresolved`, including suppression of the last retryable outer attempt and X1 proposal-invalid completion. Explicit caller cancellation, shutdown/host failure, timeout and budget completion terminals retain their documented precedence. Exact equality with request, token, cost or elapsed ceilings retains the existing accepted-settlement semantics; finite and unlimited lifetime caps retain the same checked accounting.

Every C1 work item appears exactly once and in exact C1 order. Its closed status is:

- `planned`: no proposal or terminal outcome;
- `proposal-complete`: one fully validated trusted proposal;
- `accepted`: that proposal is part of the current known candidate;
- `closed`: no proposal and one planning, Scribe, or narrowly factory-derived
  Patch terminal outcome.

Current-context revalidation also preserves the C1 disposition matrix:
planning-terminal work must remain `closed` with the planning-terminal outcome,
while executable work may advance through the executable states or close only
with a correlated Scribe outcome or the exact factory-derived
`Patch/PatchRejected` outcome. Planning carries no Scribe/Patch correlation;
Scribe carries exact request/attempt correlation and no Patch correlation; Patch
carries exact Patch Request and Patch Result commitments and no Scribe
correlation. A Scribe proposal completion that is valid by itself but cannot join
the bounded active M2 projection closes as `completed-over-bound` and carries the
exact Scribe result/proposal commitment in addition to its request and attempt
correlation. A provider failure additionally carries exactly one provider-neutral
final disposition, `retryable` or `terminal`; all other outcomes carry no
provider disposition. Later retry policy consumes this durable fact and does not
reclassify the historical provider result. Scribe closures require `scribeCompletionSource`: `scribe-result` for ordinary X1/M3 closure, or `recovered-dispatch-failure` for an accepted exhausted transient-failure proof recovered before any new physical call. Ordinary `scribe-result` closures require `acceptedDispatchFailureCommitmentSha256` to be null. A `recovered-dispatch-failure` closure requires `code: provider-failure`, a non-null SHA-256 of exactly 64 lowercase hexadecimal characters without whitespace `acceptedDispatchFailureCommitmentSha256`, and a null `scribeResultCommitmentSha256`; non-Scribe closures require both source and accepted failure commitment fields to be null.

Per-work outer attempts and candidate attempts are persisted separately from lineage-wide charges. Charges distinguish observed values, conservative unobserved exposure, and total charged; `totalCharged = (observed ?? 0) + conservativeUnobserved` is checked. The exclusive provider reservation is one immutable target-attempt claim with work/request/attempt identity, execution-start revision, monotonic operation/dispatch ordinals, current-execution physical count, restored-failure scalar and retry progress. Its current operation is exactly one `host` phase with elapsed-only exposure or one `dispatch` with request commitment and finite physical-request/cache/miss/output/cost/elapsed exposure. A host phase never fabricates provider usage. A planned work item may instead retain `pausedProviderAttempt` with the same outer request/attempt, last dispatch ordinal and accepted failure/wait progress. Its attempt identity uses exactly 47 ASCII characters: `scribe-attempt.` followed by 32 lowercase hexadecimal characters, without whitespace. Paused and active ownership cannot coexist. Patch reservations retain their separate M2 owner.

Candidate observation records accepted work keys, one domain-separated commitment over the exact ordered accepted proposal/block projection, changed-file hashes/counts, and the historical request/result commitments of the accepted M2 execution. The stable projection commitment prevents a candidate from being combined with another valid proposal that retains the same C1 work key; the historical execution commitments remain mutually correlated evidence and are not reused as a fresh-process request identity. The observation contains no source or candidate bytes. Every cumulative M2 outcome carries the exact completed active-projection commitment. Cumulative M2 outcome, campaign terminal outcome, and an optional bounded predecessor summary are independent facts; a predecessor summary is not an embedded historical checkpoint.

## Trusted proposal and M2 closure

`CreateTrustedProposal` first revalidates current C1 authority and the request's input identity against the checkpoint commitment. It requires an active provider reservation whose work-item key, exact request SHA, and M3 attempt identity match the proposal being admitted, plus exact request/result/run-envelope correlation, exact request limits, the composition owner's typed provider, model, Scribe protocol, and Tool Policy authority, the current work target, source, applicable components, and exact Style Profile. The input-identity commitment, those execution identities, and the complete Scribe-limit projection enter the proposal commitment. An absent provider reservation, a patch reservation, or any substituted input, work, request, attempt, execution identity, or limit fails closed. It projects only stable, source-free evidence metadata and the typed M2 block, including the bounded validated proposed structured content needed by M2. Prompt text, evidence or existing-documentation text, provider payloads, raw responses, diffs, source bytes, credentials, and process-local repository handles are excluded.

Persisted evidence is validated by the same Core-internal stable projection validator used by M3. It shares M3's lowercase identifier and compilation-context grammar, supported XML documentation IDs, authority-to-kind mapping, repository/metadata/generated/synthetic locator identities, zero-length-or-positive evidence span rule, 4 MiB per-reference observation ceiling, and nonempty ordered 64-item claim-category bound. A target subject has null component kind and identity. A component subject is exactly `component.type-parameter` with `type-parameter/<canonical ordinal>`, `component.parameter` with `parameter/<canonical ordinal>`, `component.return` with `return`, or `component.value` with `value`, and must belong to the proposal's exact patch block. The runtime validator, canonical codec, and published schema accept this same stable M3 subset.

For every patch attempt, `ReconstructPatchRequest` selects exactly work in `proposal-complete` or `accepted` status while preserving exact C1 membership authority: `CampaignCheckpointState.WorkItems`, `CampaignCandidateObservation.AcceptedWorkItemKeys`, and the accepted-projection commitment remain in exact C1 order. The selected proposal blocks are then serialized in Documentation Patch v1 canonical block order, which may differ from C1 order, and every M2 result target traces that serialized request order. Cross-layer validation requires exact membership and key-to-block mapping across both order domains. Reconstruction also requires the fresh context's input-identity commitment to equal the checkpoint snapshot authority and the fresh process to supply every and only the current typed evidence row for the persisted projection set. Each row must use the new `RepositoryContextRef` and exactly match the persisted subject, kind, relation, authority, stable locator, content commitment, byte/truncation observations, and claim categories. It constructs the sorted distinct provenance catalog, writes the complete M2 request, and sends those exact bytes through `DocumentationPatchValidator.ParseRequest`. `ReconstructAcceptedPatchRequest` independently enforces the same input binding, selects only `accepted` work, and requires its exact stable proposal/block commitment to match the candidate. It does not require the fresh request SHA to equal the historical accepted request SHA because `RepositoryContextRef` changes after every successful production load. A later rejected, stale, host-failed, cancelled, or timed-out mixed request cannot invalidate the earlier accepted candidate. This distinction permits a known accepted candidate to coexist with newly proposal-complete work without pretending the unresolved next request already completed. Both projections prove the whole request against M2's 1 MiB, 512-block, 4,096-provenance, 64-reference-per-block, component, content, ordering, uniqueness, path, span, and vocabulary rules.

`CreatePatchReservation` first applies the same target-profile and input-identity gate as reconstruction, then derives the reservation request digest and expected revision from either the exact active projection or the exact accepted-only reconstruction and checkpoint; callers cannot supply those correlation facts. Typed and host completion factories apply that shared state/request-context gate again while consuming the exact validated checkpoint containing the active patch reservation. They reject an input identity, request, expected revision, or proposal projection not owned by the reservation/state. `CreatePatchCompletion` then accepts only a result that passes `DocumentationPatchValidator.ValidateResult` for the exact request and, for an accepted result, derives accepted membership, the stable proposal/block commitment, every changed-file observation, and the cumulative result together. Host completion derives the same historical request/revision authority from the reserved state. The direct constructors for reservation, candidate, and cumulative-result DTOs are not public producer APIs. The domain-separated result commitment covers request identity, outcome, ordered target traces, changed-file facts, changed-block count, invariants, and bounded diagnostics.

A cumulative `accepted`, `over-bound`, `rejected`, or `stale` outcome represents a completed typed M2 validation result and therefore requires that result's exact commitment. `over-bound` records a valid completed result whose candidate cannot fit the configured campaign ceilings; it preserves the exact projection/result pair while omitting the incompatible candidate. `host-failure`, `cancelled`, and `timeout` are host-level completion families without a typed M2 result and require a null result commitment. An accepted outcome additionally matches the complete candidate observation's request and result commitments.

### Patch rejection reduction

`CreatePatchRejectionReduction` is the sole authority for turning one M2
rejection into one closed Patch work row. It consumes the canonical predecessor
artifact, current C1/style/input authority, exact cumulative request, validated
rejected result, and selected work key. The selected row must be the only
`Invalid` target and must be `proposal-complete`; every other target is `Valid`,
every diagnostic is a supported block-local rejection attributed to the same
key, and every global invariant passes. The selected C1 work may share neither
physical-owner equivalence nor overlapping source/edit authority with another
active row. Accepted, planned, already closed, stale, root, path-only,
no-effective-change, candidate-state, multi-invalid, shared-owner, overlapping,
wrong-revision, wrong-request, or wrong-result cases return a bounded
non-removable decision without probing a subset.

The removable decision contains an opaque immutable capability retaining the
complete exact predecessor and internally derived closed outcome. The reducer
has no overload that accepts a caller-selected key plus a generic Patch outcome.
Each closed outcome also carries a non-serialized in-memory binding to its
enclosing work key; parsing restores that binding from the parent row, and the
generic validated-state materializer rejects reuse under any other key.
Applying the capability to its predecessor always derives the same canonical
successor; applying it to that exact successor is unchanged; applying it to any
other artifact is a conflict.

## Budget and transition semantics

The six campaign lifetime caps are explicit nullable non-negative integers in the current v1 checkpoint; `null` is unlimited and zero is finite. They do not participate in the campaign configuration correctness commitment. All finite Scribe, Patch, structural, attempt and candidate bounds remain active, and all arithmetic remains checked regardless of cap presence. `costRates` is a required nullable four-rate object bound to the existing currency/rate content authority; `hasUnpricedCostHistory` is a required lineage charge fact. There is one current draft shape and no cross-revision reader.

`RefreshLifetimeCaps` validates the exact accepted predecessor and all current correctness inputs before one conditional revision transition. The CLI revalidates every admitted configuration source immediately before this transition and any old lease retirement. A changed source stops before mutation. A changed cap conservatively settles any old active reservation once, retires its lease, preserves charges, attempts, proposal/candidate authority and accepted origin, and changes only spending policy. A cap below retained history yields `exhausted/lifetime-cap`; raising or clearing it permits retained progress to resume. Structural over-bound, explicit cancellation, timeout and host/per-run terminals retain their original authority and are not reopened. Identical caps are an exact no-op. Changed-base supersession carries the same history while accepting current caps.

A successful valid proposal or fitting accepted candidate that crosses a lifetime cap remains trusted or accepted; only subsequent work stops. Structural projection/candidate overflow remains `exhausted/budget` and cannot be repaired by clearing lifetime caps. Exact cap equality is admissible.

Cost measurement is independent of the lifetime cap. Each physical dispatch reserves the sum of independent remaining cached/uncached input exposure times their configured rates, plus one bounded output exposure times the greater output/reasoning rate, rounded upward per dispatch using checked wide arithmetic. There is no per-target or Action monetary business cap. Exact same-currency observations remain observed; missing priced cost retains that dispatch's conservative estimate from the greater of reserved and known token facts. Reasoning is an output subset and is never priced twice. Without rate/currency authority, dispatched or uncertain work retains `hasUnpricedCostHistory`; a finite lifetime cost cap cannot admit another dispatch. Missing observations from an earlier dispatch are never refunded by later known usage.

`CampaignBudgetAccounting` owns checked durable admission and settlement. A genuinely new target attempt increments one outer invocation and work ordinal, then installs a host-only claim. Its original execution-start capability may start Runtime once. Every actual provider send requires the exact request commitment, one serial permit, settlement of the preceding host interval, finite lifetime admission, conditional dispatch reservation, and canonical readback. Settlement charges that physical operation once, persists its disposition and bounded pending retry delay, and advances the same claim to a host phase before Runtime chooses another operation. Final M3 completion verifies current physical count/restored progress against the latest accepted claim and settles only the remaining host interval; it does not charge aggregate tokens, money or provider counts again.

Known per-dispatch dimensions remain observed. Each missing dimension consumes its own full reserved exposure, with known subset excess retained as a conservative parent lower bound. Fully known input/cache/miss triples must agree; partial known partitions cannot exceed a known input total. Reasoning is an output subset. Observed overruns remain charged and prevent further work without erasing independent missing-field history. Preparation or cancellation before the gated physical start is authoritative not-sent and charges no physical request or provider usage.

Action exhaustion pauses compatible unresolved work with the same outer request/attempt and accepted retry progress. A fresh invocation first retires only the old outstanding host or dispatch exposure once, then creates its shared allowance. Resume clears the paused marker, retains outer counts/charges/dispatch ordinal/failure count/pending wait, establishes a new execution-start revision, and resets only current-execution physical count. A fresh process reconstructs and validates its complete current request authority; its process-local request/context SHA may change while the derived semantic attempt remains the same. Admission checks the full reservation and completion headroom before persistence or dispatch; an old request SHA alone never authorizes the fresh context. No target quota or budget change resets semantic retry authority. An exhausted accepted retry-failure proof may close after takeover without a new provider call or fabricated M3 payload. A genuine closed retryable outcome admits the next finite outer attempt; old late callbacks and arbitrary intermediate Writers cannot restart Runtime or advance the new owner.

One monotonic invocation interval covers preparation, provider body/parse, tools, submissions, waits and postflight. Each current operation is independently bounded by existing operation safety (repository read 30000ms, semantic query 10000ms, otherwise request completion safety), shortened by remaining Action/lifetime time. The selected deadline owner is retained; finite lifetime wins an equal boundary. Action pause is recoverable, lifetime exhaustion records `lifetime-cap`, and actual provider/host/caller failures keep their own reason. Accepted host transitions charge exact elapsed once. Abrupt loss charges only the current outstanding finite exposure; unused Action duration never becomes unknown history. Mandatory settlement has an independent bounded token and cannot grant another side effect after exhaustion.

Patch admission still increments exactly one validation invocation and the candidate count of each block in its exact projection. It retains `PatchAttemptCount = 1`, its own finite elapsed deadline, and conditional reservation/readback before M2 dispatch. If a provider resource stop follows an already accepted candidate, the runner may perform the existing accepted-only reconstruction/publication handoff without another provider call. The existing structural projection/publication quotas remain binding. An active Patch at capacity rejects retry as `ProjectionCapacityUnavailable` with byte-identical state; no C4 budget setting bypasses that boundary.

Valid authoritative budget, timeout, or cancellation terminals may report
bounded observations above the reserved run maxima. Those exact observations
are charged and the reservation is cleared; they are not reclassified as an
ambiguous invocation. Provider cost telemetry is ignored by the currency-less
campaign ledger when rate/currency authority is absent; unpriced activity is retained explicitly. A valid successful proposal
that cannot join the bounded aggregate M2 request is closed with a context-independent
Scribe result commitment; a valid candidate that cannot fit is recorded as a
cumulative `over-bound` projection/result pair. Both become durable budget
exhaustion while prior authority is retained. Provider admission also checks the complete
active Patch projection capacity before dispatch; the completion boundary
repeats that check so an authoritative result always settles even if it reaches
an already-full projection.

`CampaignStateReducer` is pure and produces `Applied`, exact `Unchanged`, or a
bounded rejection. Each applied transition carries its exact predecessor and
intended successor, so `ApplyTransition` accepts only the predecessor, returns
the byte-identical successor as unchanged replay, and rejects every third state.
Every applied mutation advances `checked(revision + 1)` once;
the Campaign State observation ceiling is also the revision ceiling. Provider
admission requires revision headroom for both the durable reservation and its
authoritative completion. At the penultimate revision, fresh work becomes
durable exhaustion without creating a reservation; an already active retry
uses the final revision to settle its exposure conservatively and terminalize
without redispatch. A
correlated authoritative M3/M2 completion settles and clears its reservation.
An authoritative completion supplied with a simultaneous cancellation or
timeout wins. A generic stop cannot settle an active invocation; the active-stop
entry point requires the exact accepted checkpoint capability. Cancellation,
timeout, or exhaustion without an authoritative result conservatively settles
and clears the active exposure before recording the terminal. Accepted
M2 completion replaces the complete candidate observation; an over-ceiling
candidate is omitted and becomes durable budget exhaustion. Patch rejection
closes only the work proven by the opaque reduction capability. Campaign
completion is reached when no actionable selected-batch work remains;
accepted rows remain accepted and reconstructible while closed rows retain their
terminal evidence. `all-work-closed` requires every selected target to be accepted and no excluded unresolved work. `unresolved` records skips, failures, suppression or unsupported/excluded targets. Deferred targets outside the fixed batch remain visible and cannot dispatch. Neither reason authorizes rewriting accepted rows as closed or claiming main is compliant.
Rejected, stale, and host-failed cumulative Patch outcomes are durable stops
unless the exact Patch rejection capability proves a sole removable item;
cancelled and timed-out Patch host outcomes preserve their exact request and
reserved-revision correlation.
Patch over-bound completions are an independent canonical bounded
`knownCompletedOperations` set rather than the latest cumulative observation.
Each sorted unique entry binds the operation kind, exact request commitment,
context-independent commitment over the ordered proposal projection, exact result
commitment, and a domain-separated binding commitment over those fields. The set
survives later accepted, rejected, stale, host-failed, replayed, and restarted
transitions. A fresh process may change `RepositoryContextRef` and therefore the
reconstructed request SHA, but reserve, retry, and dispatch issuance all resolve
the supplied active or accepted-only projection and reject it when its projection
commitment is already known complete. A no-result cancellation or timeout of a
distinct retained Patch reservation conservatively settles and clears that
reservation, preserves the candidate, latest cumulative observation, and complete
known-operation set, and rederives `exhausted/budget` without fabricating an M2
result. Completing a distinct retained projection cannot reopen a Scribe
`completed-over-bound` work row.

Supersession revalidates a fresh revision-zero C2 template against current C1
authority. Product revision, lineage, correctness-bearing ceilings, policy, target profile,
and input identity remain equal, while opaque snapshot and execution commitments
must both change. Active exposure is conservatively settled, lineage charges and
revision continue, snapshot work/candidate facts reset from the template, and
one bounded immediate-predecessor summary is retained. Its bounded, sorted
completed-operation array retains every known Patch over-bound operation plus any
Scribe over-bound proposal, with operation kind and request/projection and result
commitments, so supersession does not erase or prefer one completion fact. When a correlated old
snapshot transition is observed at the same boundary, supersession wins and the
old transition is retained only as rejected late authority; it is not applied to
the successor snapshot.

## Provider completion authority and proposal-stage execution

Provider completion is authorized only by one process-local `CampaignProviderCompletionAuthority`. The exact accepted provider invocation may issue one registrar; that registrar may register one coherent X1-owned final fact; and the resulting authority may be consumed by the reducer once. The registrar type, creation entry point, completion-kind vocabulary, preparation authorization, and registration entry point are Core-internal and visible to exactly the X1-owning CLI production assembly through an explicit friend boundary; another Core consumer cannot mint a registrar or choose an X1-only final fact. Checked-in architecture coverage additionally confines the friend-side calls to the X1 binder. There is no executor-facing raw-M3 completion path. The registrar retains the invocation-owned request SHA, attempt, execution capability, and current request-validation authority. An X1-reparsed request is accepted only by exact artifact SHA plus complete provider-request validation; no-M3 facts use the invocation-owned request and cannot be repaired by a caller.

The first gated underlying model `SendAsync` starts the original execution's physical lifecycle. Each later physical send requires another accepted dispatch reservation under that same parent. Intermediate progress Writers can advance only the original bound parent; they cannot mint a second execution-start capability. Ordinary final completion verifies the exact bound M3 outcome and latest accepted claim. X1-only host/proposal-invalid completion settles the current host interval or current uncertain dispatch without recharging historical usage. Pure Action stop uses a paused attempt; explicit caller cancellation and genuine timeout retain their terminals. Abrupt loss requires exact retirement before resume, and accepted exhausted failure progress closes from its durable proof.

The authority-only reducer uses this closed final mapping; X1-only rows retain a null Scribe result commitment:

| Final fact | Durable work result | Root terminal / stage outcome |
| --- | --- | --- |
| admitted proposal | `proposal-complete` with trusted projection | none / proposal ready |
| proposal over a structural aggregate bound | `closed / scribe / completed-over-bound` with the existing proposal commitment | `exhausted / budget` / budget exhausted |
| structured skip | existing insufficient-evidence or unsupported-domain code | complete when resolved / terminal stop |
| retryable provider failure | `provider-failure / retryable` | none unless settlement exhausts / retryable stop |
| terminal provider, tool-protocol, validation, or internal M3 failure | existing matching Scribe code | complete when resolved, subject to settlement exhaustion / terminal stop |
| X1 proposal-invalid | `validation-failure` | complete when resolved, subject to settlement exhaustion / terminal stop |
| X1 host failure or shutdown cancellation | `internal-failure` or `cancelled-by-shutdown` | `failed / host` / terminal stop |
| caller cancellation | `cancelled-by-caller` | `cancelled / caller` / cancelled |
| timeout | `timeout` | `timeout / deadline` / timed out |
| budget terminal | `budget-exhausted` | `exhausted / budget` / budget exhausted |

The registered authoritative completion fact wins over a simultaneous generic stop; its explicit host, caller, timeout, or budget terminal also takes precedence over simultaneous settlement exhaustion. Proposal admission and replay require the trusted projection; a retained postflight-rejected M3 proposal is closed as validation failure and never becomes `proposal-complete`.

The in-process proposal executor validates exact live M1/C1/C2/C3 context and reconstructs the canonical current M1 audit authority before selection, including provider-free replay, request parsing, or dispatch. It copies caller-owned request bytes once into a bounded executor-owned snapshot, and parsing, reservation correlation, X1 reparse, and provider preparation all consume only that snapshot. It checks runtime provider/model/protocol identities before admission. Proposal-complete replay uses its already-started authority; new admission and retry use only the fixed invocation allowance in saved batch order. Suppressed, deferred, unsupported and excluded targets cannot dispatch. A root terminal otherwise rederives the bounded stage outcome. Active Patch or foreign/contradictory reservation state fails closed.

Execution cancellation is independent from settlement/readback cancellation. The reducer constructs and validates the complete successor artifact before consuming completion or lifecycle authority. A final fact becomes durable only through one exact-predecessor transition, conditional replacement, and exact readback. Deterministic authority or successor-construction rejection is a host contract error; store/current-state conflict is a state conflict; only uncertain dispatch, acknowledgement, or readback remains ambiguous. The outer host-active interval uses a monotonic clock and remains distinct from M3 envelope elapsed time. The returned proposal-stage outcome is rederived from the accepted artifact and exposes no raw request/result/provider/tool/source content or mutation authority.

These process-local additions do not alter Campaign State v1 properties, enum vocabulary, parser/writer, schema, known-answer bytes, result-commitment matrix, or `ICampaignCheckpointStore`.

## In-process cumulative Patch execution

The campaign Patch executor consumes the exact live classified and observed
repository sessions, accepted C1 planning input and plan, current execution and
Style authorities, and a current closed M2 projection. The projection contains
exactly `m2ProjectionVersion: 1` and a positive
`maximumPatchElapsedMilliseconds`. Its canonical
`configuration.m2-projection` authority must match C1 by ID and SHA. The same
maximum is used unchanged as both the conservative write-ahead Patch elapsed
reservation and the one-shot M2 deadline; it cannot exceed the accepted
Campaign State observation bound. The optional campaign lifetime elapsed cap may be zero, lower than the Patch deadline, or unlimited; admission compares accumulated charge plus the full finite reservation against a configured cap.

Before reconstruction, the executor independently re-establishes every active
proposal evidence row from the current M1/C1 catalog or current repository,
Roslyn, generated-output, metadata, and supported context facts. It recomputes
the stable evidence projection and rejects missing, stale, duplicated,
ambiguous, unsupported, or non-reconstructible rows before reservation. Merely
copying persisted evidence metadata and substituting a fresh
`RepositoryContextRef` is not current evidence. The C2 factory receives exactly
the rederived set. C1 work rows, accepted work keys, and accepted-projection
commitments preserve exact C1 order; the M2 request serializes the same exact
membership and key-to-block mapping in Documentation Patch v1 canonical block
order, and result targets trace that serialized order. Accepted-only
reconstruction is a separate action and cannot satisfy a mixed active
projection.

Every real M2 call, including accepted-candidate reconstruction, follows a
conditional Patch reservation and exact readback. An observer cannot dispatch.
Caller cancellation and the projection deadline register distinct causes in an
atomic first-cause coordinator; their linked token is execution-only, while
settlement/readback uses the independent settlement token. An authoritative
typed M2 result retains C3 precedence over a simultaneous stop. Without a typed
result, the first cause selects cancellation or timeout, and a bounded engine
failure remains host failure. Finite nonnegative monotonic elapsed is rounded up
and, when it fits the Campaign State observation bound, its exact value replaces
conservative exposure through the exact C3 settlement transition even when it
exceeds the reservation maximum. Invalid, negative, or unrepresentable elapsed
retains the complete conservative reservation.

Fresh reservation and active-reservation retry require a predecessor revision
at most `MaximumObservation - 2`. At the penultimate revision, a nonterminal
state uses `Stop(Exhausted)` and an active Patch reservation uses
`StopActiveInvocation(Exhausted)` with zero M2; durable terminals replay
unchanged. Accepted-only reconstruction without a settlement revision fails
closed, and a maximum-revision state requiring mutation is a state conflict.

The exact read-back state selects continuation. A locally retained candidate
may be returned only after its request, result, accepted projection, changed
files, hashes, and observations match the accepted checkpoint. Accepted state
without that process-local capability performs reservation-driven accepted-only
reconstruction. A sole-item reduction is never repeated; remaining active work
is recomposed as one full projection. Durable rejection, stale, host failure,
cancellation, timeout, and closed stops replay with zero M2. An accepted M2
result that C3 persists as `over-bound` returns exhaustion, discards the
transient candidate, preserves the prior candidate observation, and is not
rerun because its known-completed Patch projection is authoritative.

Candidate source bytes remain confined to the nonserialized internal accepted
capability after exact settlement readback. Checkpoint JSON, cumulative state,
diagnostics, exceptions, logs, printable outcomes, and every nonaccepted or
uncertain path contain no source/evidence/provider text, candidate bytes, diff,
credential, or absolute path. These rules add no Campaign State property,
schema branch, compatibility reader, migration, second Patch engine, physical
store, public command, or provider operation.

## Conditional store and readback

`ICampaignCheckpointStore` exposes only typed read, create-if-absent, and
replace-if-current operations. Reads distinguish not-found, found exact bytes,
invalid, and unreadable. Create never overwrites, and replace requires both the
exact predecessor revision and digest while distinguishing missing from current
mismatch. The port exposes no path, stream, filesystem, lease, Git, or process
capability.

`CampaignCheckpointAcceptance` reads before writing. Initial creation requires
an explicit validated revision-zero initial authority. An applied transition is
written only over the predecessor embedded in that transition; an independently
supplied predecessor is not accepted. An already exact successor is accepted as
lost-ack replay without a write. A conditional-write conflict is reread and is
accepted only when the concurrent winner is that exact intended successor.
Every accepted path performs an exact readback, recomputes SHA-256, parses and
canonical-validates the bytes, and compares bytes, digest, revision, and the
complete parsed artifact. A rejected reducer result performs zero port calls,
and no conflict falls back from replace to create or from create to overwrite.
Found reads enforce byte, revision, and lowercase digest bounds before copying.

## Canonical JSON and validation

The registry is `schemas/campaign-state/v1.schema.json`. Canonical JSON uses fixed writer order, no insignificant whitespace, JSON integers only, ordinal collection order, strict UTF-8 without BOM, and exactly one trailing LF. Parsing rejects malformed UTF-8/JSON, BOM, duplicate or unknown properties, unknown enum values, over-depth or over-byte artifacts, invalid bounds/references/correlation, and any byte sequence that is not the exact writer output.

The checkpoint is bounded to 4 MiB, depth 96, 4,096 work rows, 512 active M2 blocks, 4,096 evidence rows, 64 provenance references per block, 512 changed files, and 128 diagnostics. Enumerable collection happens through capped collectors, intrinsic validation proves the complete canonical encoding fits the 4 MiB bound, and the writer uses a capped stream that cannot allocate or emit past it. Runtime and schema share the same lexical and numeric primitive domains, including Scribe-limit, campaign-limit, attempt-ID, observation, revision, evidence, and canonical repository-path bounds. Documentation-comment IDs use the shared M3 prefix, XML 1.0 scalar, control-exclusion, and scalar-count domain in target and component evidence subjects, metadata locators, and the nested M2 patch block. Nested exception type IDs use their distinct `T:`-only M2 domain, including XML 1.0 scalar, Unicode whitespace, control, XML-markup punctuation, scalar-count, and absolute-end restrictions. Numeric observations are checked before arithmetic. Repository paths count Unicode scalars, permit ordinary non-drive colons and JSON-escaped line terminators, and reject machine-absolute or drive-root forms, traversal segments, backslashes, NUL, empty segments, and over-bound values. A predecessor must differ in both opaque snapshot binding and execution commitment, and its candidate summary uses a closed zero/absent versus positive/present matrix. Failure messages are fixed structural text and never echo caller content.

## Privacy boundary

The persisted artifact may contain stable symbol IDs, repository-relative paths, spans, enum IDs, opaque contract IDs, hashes, counts, truncation flags, source-free evidence locators, and the bounded validated proposed M2 structured content. It must not contain source, existing-documentation, or evidence text; prompts; provider requests or raw responses; candidate bytes; full diffs; secrets; credentials; absolute machine paths; `RepositoryContextRef`; transcripts; environment values; or GitHub metadata.

## Conformance

The normative implementation lives under
`src/ContractScribe.Core/Campaign/State/**`. Fixtures under
`tests/fixtures/campaign/state/**`, `CampaignStateContractTests`,
`CampaignStateTransitionTests`, and `CampaignCheckpointStorePortTests` cover
fixed known-answer bytes/digest, culture/process stability, canonical
round-trip, Patch reduction, budget/revision boundaries, conservative
settlement, exact/conflicting replay, conditional-write races, mandatory
readback, fail-closed privacy, current M2 projection correlation, exact 1 MiB and
+1-byte aggregate admission, context-independent over-bound restart behavior,
and coherent execution-authority substitution. Later M4
slices consume this contract but must not weaken or silently reinterpret it.
