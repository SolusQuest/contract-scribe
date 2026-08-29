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

## State model

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
reclassify the historical provider result.

Per-work outer attempts and candidate attempts are persisted separately from lineage-wide charges. Charges distinguish observed values, conservative unobserved exposure, and total charged; `totalCharged = (observed ?? 0) + conservativeUnobserved` is checked. The single active reservation is separate from charged history and is either a provider reservation for one planned item or a patch reservation for the exact active M2 request.

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

`CampaignBudgetAccounting` is the only arithmetic authority for admission and
settlement. Provider admission increments one durable outer invocation and one
per-work outer ordinal, then reserves the complete persisted Scribe-run maxima
for provider requests, input/uncached/output tokens, enabled same-currency cost,
and active elapsed time. Patch admission increments exactly one validation
invocation and the candidate count of every accepted or proposal-complete block
in the exact request projection. The Patch reservation
always has `PatchAttemptCount = 1` and records the resulting checkpoint revision;
there is no caller-supplied batch count. M2 dispatch/completion requires an
opaque provider/Patch invocation authority derived only from an accepted
checkpoint capability produced after exact persisted readback. The complete
artifact, attempt, request, and revision bindings prevent pre-persistence
dispatch and prevent an earlier result from being attached to a later retry.

Settlement adds exact observations when present. A missing observation moves
the entire reserved dimension to `conservativeUnobserved`; it never becomes
zero. A typed retry first settles the old reservation once, then performs one
new admission in the same revision transition. A retryable closed provider
outcome is also a durable retry source. Both retry families validate a freshly
reconstructed current request; its process-local request SHA may stay equal or
change. Fresh provider attempt identity
is derived from immutable execution authority, work key, and the checked new
outer ordinal. Every Patch retry receives a later reserved revision. Late
results for retired attempt/revision authority fail correlation.
A retryable closed provider outcome does not own unresolved reservation exposure.
If the retained active Patch projection is already at capacity, retry is rejected
as `ProjectionCapacityUnavailable` with byte-identical state; only a still-active
provider reservation may be conservatively settled and cleared at that boundary.

Valid authoritative budget, timeout, or cancellation terminals may report
bounded observations above the reserved run maxima. Those exact observations
are charged and the reservation is cleared; they are not reclassified as an
ambiguous invocation. Provider cost telemetry is ignored by the currency-less
campaign ledger when cost enforcement is disabled. A valid successful proposal
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
completion is reached when no `planned` or `proposal-complete` work remains;
accepted rows remain accepted and reconstructible while closed rows retain their
terminal evidence. The historical `all-work-closed` reason names this resolved
terminal set and does not authorize rewriting accepted rows as closed.
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
authority. Product revision, lineage, complete ceilings, policy, target profile,
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

The first underlying model `SendAsync` is the dispatch boundary. Before dispatch, an ordinary completion is coherent only for a zero-provider-request cancellation or validation, internal, timeout, or budget failure; an X1-only completion is coherent only for proposal-invalid or host failure with no M3 outcome. Provider/tool failures, proposals, and skips require a dispatched lifecycle. After dispatch, an ordinary completion requires the exact bound outcome; an X1-only completion may carry no outcome or the exact retained proposal outcome. Outcome and host elapsed are either both present or both absent. An available X1 proposal-invalid or host completion retires and settles the complete reservation conservatively. A dispatched X1-only completion settles exact retained M3 observations when present and otherwise settles conservatively. Pre-send cancellation, timeout, or exhaustion without a bound M3 work result uses `StopActiveInvocation`, records the root terminal, and leaves work planned. Only abrupt loss or incoherent authority leaves an active reservation for later conservative `RetryProviderInvocation` recovery.

The authority-only reducer uses this closed final mapping; X1-only rows retain a null Scribe result commitment:

| Final fact | Durable work result | Root terminal / stage outcome |
| --- | --- | --- |
| admitted proposal | `proposal-complete` with trusted projection | none / proposal ready |
| proposal over a settled or aggregate bound | `closed / scribe / completed-over-bound` with the existing proposal commitment | `exhausted / budget` / budget exhausted |
| structured skip | existing insufficient-evidence or unsupported-domain code | complete when resolved / terminal stop |
| retryable provider failure | `provider-failure / retryable` | none unless settlement exhausts / retryable stop |
| terminal provider, tool-protocol, validation, or internal M3 failure | existing matching Scribe code | complete when resolved, subject to settlement exhaustion / terminal stop |
| X1 proposal-invalid | `validation-failure` | complete when resolved, subject to settlement exhaustion / terminal stop |
| X1 host failure or shutdown cancellation | `internal-failure` or `cancelled-by-shutdown` | `failed / host` / terminal stop |
| caller cancellation | `cancelled-by-caller` | `cancelled / caller` / cancelled |
| timeout | `timeout` | `timeout / deadline` / timed out |
| budget terminal | `budget-exhausted` | `exhausted / budget` / budget exhausted |

The registered authoritative completion fact wins over a simultaneous generic stop; its explicit host, caller, timeout, or budget terminal also takes precedence over simultaneous settlement exhaustion. Proposal admission and replay require the trusted projection; a retained postflight-rejected M3 proposal is closed as validation failure and never becomes `proposal-complete`.

The in-process proposal executor validates exact live M1/C1/C2/C3 context and reconstructs the canonical current M1 audit authority before the ordered scan, including provider-free replay, request parsing, or dispatch. It copies caller-owned request bytes once into a bounded executor-owned snapshot, and parsing, reservation correlation, X1 reparse, and provider preparation all consume only that snapshot. It checks runtime provider/model/protocol identities before admission and performs one paired plan/state scan in C1 order. Planning-terminal, accepted, and nonretryable closed rows are resolved; only the first encountered `proposal-complete` row replays; active provider recovery, retryable closed work, and planned admission are actionable only when the root terminal is null. A root terminal otherwise rederives the bounded stage outcome. Active Patch or foreign/contradictory reservation state fails closed.

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
campaign elapsed ceiling or the Campaign State observation bound.

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
