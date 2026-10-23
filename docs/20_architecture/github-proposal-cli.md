# Automatic GitHub proposal CLI

`github-proposal` composes the campaign runner, live H1 admission and GitHub publication adapter through one ordinal, case-sensitive command:

```text
contract-scribe github-proposal --repository-root <path> --input <path> --policy <path> --campaign <path> [--configuration <path>] [--configuration-override <path>]
```

Each required option appears exactly once, in any order. Both `--name value` and `--name=value` are accepted. There are no defaults, short option aliases, response files, extra operands or unknown options. Standalone `--help` or `-h` is supported; combining help with work options is invalid. The exact UTF-8/LF help is [the fixture](../../tests/fixtures/github-proposal/cli/help.txt).

`--configuration` and `--configuration-override` carry ordinary [consumer configuration](consumer-configuration.md) layers, resolved as payload defaults < configuration < override. Repository/input/policy resolution retains the [campaign contract](campaign-cli.md). GitHub checkpoints persist through the coordination ref; the standalone offline `campaign` command retains its local-file interface.

## Stable campaign configuration

Every trigger supplies the same bounded, non-secret configuration:

```json
{
  "githubCampaignVersion": 1,
  "campaignLineage": "campaign.example",
  "github": {
    "repositoryOwner": "Owner",
    "repositoryName": "repo",
    "targetRef": "refs/heads/main"
  }
}
```

All illustrated fields are required exactly once. The version is integer `1`; lineage uses the existing opaque-identifier grammar, and targetRef names a branch under `refs/heads/`. The reader accepts at most 262,144 UTF-8 bytes, with no BOM, duplicate or unknown properties, comments, invalid Unicode, null required values or credentials. It rejects the retired manual request, snapshot/base/operation/generation/transition/predecessor claims and local state fields. There is no start/resume operand, `--request` alias or alternate runtime reader. This replaces the unreleased integer-v1 draft in place; no released consumer compatibility is promised.

The CLI authenticates current HEAD, checkpoint and exhaustive PR/history facts. It selects initial or resumed execution, compatible fixed-batch continuation, original publication recovery, wait, pause or no-op. Internal operation/generation identifiers derive from the accepted campaign/snapshot/batch; predecessors derive from authenticated terminal generations. Caller run IDs, artifacts and clocks supply none of that authority.

## Admission and replay

### Lifecycle admission before Audit

The CLI authenticates the campaign's complete coordination/publication history, current target ref, exhaustively enumerated PRs and their ownership before entering SDK/Roslyn discovery or constructing a provider. It reads the GitHub credential once, after local campaign/configuration admission. A missing ledger is a first-run possibility only after the managed-PR search proves absence; corrupt or incomplete established recovery authority stops. Observation conveys no publication or mutation entitlement.

An owned, durably recorded open draft or ready PR on its immutable original base returns `awaiting-review` without Audit, provider construction, new targets, checkpoint replacement or publication writes. An open PR whose target HEAD moved returns `stale` with `github-proposal.changed-base`, including for README-only commits and when GitHub's mutable PR `base.sha` advances. Ownership changes, missing active refs, conflicting or multiple active matches and unstable observations stop conservatively. Current terminal facts are always rechecked; a cached terminal cannot hide a reopened PR. A verified terminal PR may retain its head identity after its proposal branch is deleted; an existing branch must still match.

If a successful original PR-create lost its acknowledgement, the owned checkpoint claim re-authenticates that exact existing operation/PR and records its publication or terminal stage before the wait or eligible fresh Audit. This path creates no PR or publication authority and enters neither SDK nor provider work. A complete published wait retains zero writes.

Checkpoint reads, recovery-plan binding and known undispatched write steps preserve actual caller cancellation. Possibly delivered mutations still require independent bounded readback; absent or mismatched effects fail closed. Cancellation never supplies permission to repeat a mutation or replace recovery authority.

Every retained closed-unmerged generation blocks its immutable original-operation base with `closed-unmerged` and `github-proposal.closed-same-base`, before quota or configuration reconciliation. The gate applies again if HEAD returns to an older closed base. The live PR `base.sha` is never the closure identity. With no active PR, merge or closure on a different HEAD permits fresh exact-current Audit without a generic successor approval. This permission does not supply a new publication operation or bypass the existing downstream publication contract.

After a permitted observation the checkpoint client pins the authenticated current target and acquires its existing owner/epoch claim; a second lifecycle observation checks the pre-discovery read window. Before planning/provider work, the loaded repository's existing safe baseline is compared to the authenticated target Git tree in both directions: missing remote-only and extra local-only analysis inputs both fail with `github-proposal.checkout-mismatch`. Relevant project/semantic/build inputs must have matching content and supported regular-file Git modes. SDK-generated outputs are reconstructed from bound projects; explicitly repository-declared inputs still participate under output directories, so a local-only semantic input cannot hide behind that exclusion. Tracked semantic inputs masquerading as build outputs are rejected. Separately admitted policy/configuration/style inputs and unrelated repository files are not a whole-checkout cleanliness gate. The binding uses the loader's sealed repository input provenance, including actual imports of any extension, compiler metadata references and analyzer/source-generator assemblies. The existing MSBuild evaluation records active item/glob provenance; default and explicit globs retain their evaluated project base, exclusions and removals, including empty globs. Static declarations in that actual repository import closure additionally bind literal import, compiler and reference paths and file-presence conditions. Inactive conditional items and unrelated None/Content files do not become compiler inputs. Unresolved dynamic input paths fail closed rather than omit possible remote members. Resolved external SDK/framework/package inputs retain the existing toolchain boundary and are not compared to Git blobs. This does not claim hermetic observation of arbitrary task or generator file I/O. Existing native confinement and baseline rebind checks remain mandatory. Failed authenticated object reads retain permission, rate-limit, cancellation, timeout or host-failure outcomes instead of being presented as ownership/source mismatches.

Relative `Import Project` paths and Import/ImportGroup/PropertyGroup file-presence conditions use the containing declaration file; ordinary imported item paths retain the evaluated project base. Active import wildcards also bind additional remote members.

GitHub admission binds a directly resolved repository-owned `CodeAnalysisRuleSet` carrier to its pre-load bytes and authenticated target content. Nested ruleset `Include` files, unrepresented search-directory resolution and generated or mutated carriers stop with `github-proposal.compiler-input-unverifiable`; direct matched rulesets remain eligible. These checks use captured bytes and do not run a ruleset resolver or change offline/local loading.

GitHub admission binds each evaluated `CompilerResponseFile` carrier to its load-time bytes, repository path and authenticated target content. Response-file lines use Roslyn's lexer, including its quotes, escapes and hash comments. The supported boundary accepts explicit scalar compiler options for definitions, language/nullable settings, warning policy, checked/unsafe/optimization, deterministic/target/platform/debug settings and reporting. Unknown options, nested `@` responses, file-bearing arguments such as `/ruleset` or `/reference`, generated/unrepresented carriers and build-time mutations of the response-file property stop with `github-proposal.compiler-input-unverifiable` before planning/provider work. Ordinary evaluated project references/documents retain their existing admission. This response-file support boundary applies only to GitHub source admission; offline/local loading and campaign behavior are unchanged. It does not execute response files or add a general compiler/build-input interpreter.

The effective remote snapshot binds actual target HEAD and correctness-bearing inputs and materialized applicable instruction/context commitments, independently of an Audit digest alone. A terminal publication identity distinguishes fresh post-terminal Audit from compatible later unpublished recovery. Compatible unpublished recovery retains the original complete plan, creation quota, selected membership and accepted progress. Invocation quota or lifetime-cap changes do not expand membership. Changed base/policy/profile/style, retry authority or finite attempt/candidate controls create fresh work authority without migrating proposals or subtracting unmerged accepted counts, preserving predecessor, archive and known/unknown usage history.

The inert process-observation seam records actual SDK discovery, deferred provider construction and physical exchange entry in confined tests. Wait/pause regressions assert all three are zero, alongside zero provider requests. Lifecycle success uses the existing `awaiting-review` outcome and authenticated PR linkage; stopped paths use a specific sanitized reason. The per-invocation usage below retains zero new provider work on these gates.

### Accepted-candidate publication

One optional accepted-candidate continuation uses the existing M4 executor. It receives only a real `Accepted`/`Reconstructed` candidate while the repository session is alive, plus an exact fresh checkpoint-store acceptance. A validated accepted candidate can be eligible under selected-batch Complete/AllWorkClosed or Complete/Unresolved; its proposal does not resolve remaining documentation. No-work, exhausted/stopped, failed, stale, candidate-less, and mismatched states cannot publish. Ordinary `campaign` behavior is unchanged when the continuation is absent.

For admitted unpublished initial/successor resume, accepted work is reconstructed before advancing remaining work. Progress is checked again against exact checkpoint readback before publication. Deferred future work stays outside this batch and produces batch-complete/no-op; unresolved selected work remains unresolved/host-failure. A persisted failed, rejected, or stale reconstruction retains its authoritative failure and current revision even when an older accepted candidate remains. Accepted-only retry reuses the existing retry reservation/settlement owner and validates the accepted projection under the fresh process context. Durable publication intent freezes the selected manifest and exact validated result subset. Same-batch reconstruction cannot start more provider work or replace that subset. The first authenticated PR seals its actual published head/tree permanently.

The [campaign origin](contracts/campaign-state-v1.md) retains the first accepted checkpoint and historical M2 identity. Every fresh reconstruction still executes, validates current evidence and candidate bytes, settles its budget, and conditionally accepts/readbacks a new checkpoint. H1 proves full correspondence with the retained origin before using that original checkpoint/request/result identity in the result-set-bound R1 authority. This makes a real fresh-process resume the same publication operation without reusing stale current-candidate authority. The displayed revision is always the current checkpoint, not the origin revision.

The campaign configuration and layers are validated before one read of `CONTRACTSCRIBE_GITHUB_TOKEN`. That credential initially grants only the exact campaign checkpoint object/ref scope, without candidate or PR rights. The invocation acquires a bounded remote owner/epoch claim and uses the same client/store for checkpoint execution and publication. H1 remains credential-free and independently validates the fresh accepted checkpoint and reconstructed candidate before publication authority is bound. The provider credential channel remains separate. Help, usage and local campaign/configuration failures do not read the GitHub credential; remote-state admission, no-work and resume validation require it.

The source-free saved complete-plan projection must exactly equal freshly validated planning facts before execution. The checkpoint and authenticated plan pointer identify the current fixed batch separately. A later trigger can advance a terminal batch with no accepted candidate or active reservation to exactly its first deferred target on the same snapshot. It retains prior closed outcomes, attempts, completed operations, dispatch/archive history and charges; closed skipped/failed members are not retried. One trigger advances at most one batch. A zero quota remains zero; a later positive quota can create a distinct initial nonempty batch. Current publication intent never uses this transition: its manifest and POST fence remain frozen. Historical terminal publication pointers are retained and independently checked against exhaustive lifecycle admission. Durable reservations precede provider dispatch. Archive witnesses retain original known and unknown charges without repricing, while ordinary restore reads the directly referenced archive transition and actual predecessor rather than replaying the lifetime. Local files and Action artifacts are not recovery authority.

## Caller contract

A downstream caller — the composite Action or a shell step — invokes the complete path with file locations and options only. It must not merge JSON, derive product or publication policy, choose predecessors, inspect GitHub state, manage provider behavior, or implement retries:

```bash
dotnet <payload>/ContractScribe.Cli.dll github-proposal \
  --repository-root <path> \
  --input <path> \
  --policy <path> \
  --campaign <path> \
  [--configuration <path>] \
  [--configuration-override <path>]
```

The caller materializes stable campaign configuration and any override to bounded files and passes their locations. Product policy, campaign resolution, runtime authority and publication admission all stay inside the production CLI.

The GitHub facade pins the trusted Actions publisher independently of inspected PRs: `github-actions[bot]`, numeric ID `41898282`, node ID `MDM6Qm90NDE4OTgyODI=`, type `Bot`. One facade may make two bounded calls when the first stops at the original proposal ref. Each call freshly validates the next missing original-operation step. A process restart may recover deterministic Git objects/ref and a not-yet-fenced PR operation through fresh narrow capabilities. Only the acknowledged winning durable send-fence CAS issues one POST right; existing fences and ambiguous acknowledgement allow exact discovery only. Absence after the fence remains unresolved.

One finite five-minute host finalization deadline starts before accepted-only reconstruction or candidate-free publication recovery, independent of the model allowance. It is shared across publication, independent uncertain-effect readback and checkpoint cleanup; it never renews per stage. It bounds unfinished work without replacing an already selected exact terminal during teardown. Actual caller or claim cancellation retains its distinct outcome.

## Output

Every recognized non-help return is exactly one compact JSON object followed by LF. Properties are ordered as follows:

```text
githubProposalEnvelopeVersion, terminalLayer, cliContractBaseline, toolVersion,
campaignOperation, publicationOperationId, generationId, outcome, diagnosticCodes,
checkpointRevision, pullRequestUrl, publicationDiagnostic, invocationUsage
```

A successful publication with unresolved selected members reports `published-partial` (exit 0), retaining the PR URL and operation/generation. It does not claim those members are resolved or authorize another PR for the batch. A complete selected result reports `published`.

Envelope version is `1`. Layers are `usage`, `preflight`, `campaign`, `publication`, or `presentation`. Campaign operation is `start`, `resume`, or null when unavailable. Unavailable operation ID, generation, revision, and PR URL are explicitly null. Successful results have no stderr; controlled failures have exactly one closed, sanitized stderr line. No raw HTTP/header/exception, credential, source, candidate, private path or provider data is emitted.

`publicationDiagnostic` is null outside a failed publication, on success, or when no internal observation is available. Otherwise its ordered fields are `boundary`, `owner`, `coordinationFailure`, `proposalFailure`, `pullRequestOutcome`, `transportCode`, `transportHttpStatus`, `delivery`, `recoveryCode`, `recoveryHttpStatus`, `objectKind`, and `predicate`. Boundary and owner are required closed names; all other fields may be null. HTTP statuses are integers from 100 through 599. Undefined enum values or out-of-range statuses suppress the complete diagnostic to null without replacing the original outcome, exit or stderr. This is an in-place correction of the current draft envelope; no alternate version is supported.

| Field | Closed values |
| --- | --- |
| boundary | Reconcile, Repository, CoordinationRead, CoordinationClaim, CoordinationRecord, CoordinationAdvanceStale, CoordinationAdvanceContent, CoordinationAdvanceRef, GitInspect, GitInspectPredecessor, GitPrepare, GitCreateContent, GitAdvanceRef, PullRequestPreflight, PullRequestObserve, PullRequestCreate, PullRequestRecover |
| owner | Reconciler, Transport, Coordination, GitData, PullRequests |
| coordinationFailure | InvalidInput, MissingPredecessor, DifferentOperation, StageConflict, TargetMoved, HumanChange, Conflict, ObjectMismatch, Bounds, Unresolved, Transport |
| proposalFailure | InvalidInput, Integrity, Bounds, Conflict, Unresolved, Transport |
| pullRequestOutcome | Absent, OpenDraft, HeldDraft, Ready, Merged, ClosedUnmerged, StaleDraft, Conflict, Unresolved, Failed |
| transportCode, recoveryCode | InvalidRequest, Authentication, Permission, NotFound, Conflict, Validation, RateLimit, Cancelled, Timeout, ResponseLost, InvalidResponse, HostFailure |
| delivery | NotDispatched, Read, NeedsReadback, Ambiguous |
| objectKind | Blob, Tree, Commit |
| predicate | InvalidCorrelation, Cancelled, UnhandledException, RepositoryUnavailable, DifferentOperation, TargetMoved, SuccessorMismatch, TransitionMismatch, UnexpectedProposalRef, ClaimMismatch, CurrentMismatch, ClaimedRefPresent, StaleStage, UnexpectedStage, CompletionHeadChanged, ObservationLimit, LifecycleOutcome, MissingOrUnexpectedComponent |

The adapter pairs this observation with one invocation's unchanged Core result. Original transport and recovery observations remain separate even when the existing result mapping gives one precedence. Owners that retain only one error do not invent a second; missing owner components use the reconciler's closed predicate without inventing an owner failure. The facade emits only the selected call's pair, including its existing second call after `RecoveredRefPartial`. No diagnostic affects request sequencing, recovery, permissions or capabilities. These fields are observations, not retry instructions or proof of a historical failure's cause. They contain no context identity, OID, body, permission header, exception or retry metadata.

### Invocation usage v1

`invocationUsage` is a compact, source-free object on every recognized non-help result, including usage errors and controllable failures. Its `invocationUsageVersion` is integer `1`, amended with this unreleased draft. Unsupported versions are rejected by the matching Action; there is no retired-envelope reader. The Action's `usage-json` output contains this same object. The root outcome/exit remains authoritative even when a usage measurement is unavailable.

| Field | Scope and definition |
| --- | --- |
| `invocationId` | Fresh process-local UUID for this command, not a durable recovery key or publication operation. |
| `campaignLineage`, `snapshotId`, `batchId` | Validated campaign lineage, snapshot execution commitment and fixed-batch identity when observed; otherwise null. |
| `checkpointRevision`, `publicationOperationId`, `generationId`, `pullRequestUrl` | Exact copies of the selected envelope's linkage, including nulls; no extra lookup. |
| `work` | Nullable saved-batch progress plus this invocation's contributions, defined below. |
| `calls` | Actual current-invocation dispatch/tool counts and correlation coverage. |
| `providerDiagnostics` | Nullable source-free physical-send observations and explicit missing-send count; reporting only, never acceptance, recovery, usage or billing authority. |
| `checkpointFailure` | Nullable first closed failure observation from this command's checkpoint store; diagnostics only, never acknowledgement, grant, recovery or billing authority. |
| `tokens` | Independent reported subtotals and missing-send counts, never budget charges. |
| `accounting` | Nullable settled allowance charges and current active exposure, separate from reported measurements. |
| `history` | Nullable accepted campaign cumulative settled charges; includes restored history and archive prefixes, excludes active reservations. Never added to current usage. |
| `timing` | Observable command/allowance clock scopes, in milliseconds. |
| `termination` | Closed stop classification, original selected source, checked allowance stop and typed Scribe finish reason. |
| `completeness` | `complete`, `partial` or `unknown` for this invocation's observations. |

`checkpointFailure` is null when no observation was retained or reporting is unavailable. Otherwise it contains exactly `operationOrdinal`, `operation`, `attemptedRevision`, `stage`, `reason`, `transportCode`, `httpStatus` and `cancellation`. The positive command-local store operation ordinal and optional nonnegative attempted checkpoint revision correlate this single immutable first failure; neither is a remotely acknowledged revision. The command captures its own store before disposal; later observer reads, renewal, cleanup and reporting callbacks cannot replace the original observation. No other store or invocation supplies its failure. A later snapshot cannot alter a frozen report.

Operation is `Acquire`, `Read`, `Write`, `Renew` or `Publication`. Stage is `Gate`, `PredecessorRead`, `CreateObjects`, `RefMutation`, `FinalRef`, `CanonicalDocument`, `CheckpointReadback`, `OwnerConfirmation` or `LeaseConfirmation`. Reason is `Transport`, `InvalidData`, `RefMismatch`, `DocumentMismatch`, `CheckpointMismatch`, `OwnerMismatch`, `LeaseUnverifiable`, `Cancelled` or `Unknown`. `transportCode` is null or an existing GitHub failure name: `InvalidRequest`, `Authentication`, `Permission`, `NotFound`, `Conflict`, `Validation`, `RateLimit`, `Cancelled`, `Timeout`, `ResponseLost`, `InvalidResponse`, `HostFailure`. The nullable status is the retained numeric HTTP100-599 observation, never inferred from a predicate; it requires a transport code. The cancellation field is present exactly for reason `Cancelled`, and is `OperationToken`, `RecoveryToken` or `Unknown`. These identify only an independently observed requested token, not an inferred caller, Host, checkpoint or finalization timeout. Unknown data stays unknown. A successful stable lost-ACK recovery has no failure observation. Failed post-CAS confirmation can coexist with a newer durable checkpoint; it does not advance the CLI's acknowledged revision or grant Writer authority. Invalid enum/status observations are suppressed; diagnostic failure does not replace execution outcome, first acceptance rejection or accounting.

This is an in-place correction of the current unreleased usage-v1 producer and consumers. It adds no checkpoint artifact schema/version, migration, retry policy, timeout, telemetry framework or raw exception text. Whole command output retains its existing16KiB bound, including the provider rows and this single finite object. The Action validates exact keys, types and closed vocabulary and renders only checked names/numbers. It cannot retrospectively identify an unreported historical failure.

`providerDiagnostics` is null only with unavailable calls. Otherwise it contains exactly `observations` and `missingDispatches`. Each row contains exactly `physicalDispatchOrdinal`, `attemptId`, `attemptNumber`, `providerRequestNumber`, `failureCode`, `failureOrigin`, `httpStatusCode` and `codecDisposition`. The physical ordinal is assigned by the existing allowance only when its actual permit starts, is unique within this invocation and strictly increases in the report. Attempt identity and numbers come from that same scoped product request, never a later callback or another conversation; `attemptId` remains null when the scope has no host-bound campaign attempt. Unsent preparation failures have no physical row. `attemptNumber` is the logical model attempt, bounded independently by the existing `DocumentationScribeContract.MaximumAttempts` (1,000,000), including restored retryable failures. `providerRequestNumber` and `physicalDispatchOrdinal` remain current-invocation physical counts bounded by128; a logical attempt129 with physical request/ordinal1 is valid. Failure code/origin reuse the closed `model.failure.*` / `model.failure-origin.*` vocabulary of the provider evaluation protocol; status is numeric100–599 and present if and only if origin is `model.failure-origin.http-status`. No row uses request-preparation origin. Codec disposition reuses the existing closed `codec.*` vocabulary, including accepted tool/terminal and specific rejections. A null failure or codec field means unavailable, not proof of success. In particular cancellation or an observation failure must not be converted into a provider error.

Rows are frozen when the command projects its envelope. The report retains at most the latest16 rows and never exceeds the original admitted physical request cap (at most128); this protects the existing16KiB whole-envelope Action limit. `missingDispatches` equals the actual physical count minus retained row count and includes unavailable or omitted observations. No provider sends yields an empty array and zero missing sends. Late, duplicate or foreign-allowance callbacks cannot replace a retained row or mutate a frozen result. The original numeric `completeness` calculation remains separate; provider diagnostic coverage is explicitly `missingDispatches`. These observations neither alter the original failure/retry classification, settlement or accounting, nor restore diagnostic details absent from old checkpoints. They never include arbitrary exception text/inner exceptions, headers, endpoint, prompt, tool payload, provider response, assistant content or reasoning text. The matching Action validates the closed vocabulary and correlation bounds and renders only fixed labels, numbers and closed classifications; it does not render attempt identities.

`work.selectedTargets` is the observed fixed batch's selected membership. `startedTargets` counts distinct targets with an accepted first durable start in this invocation; `resumedTargets` counts previously started, still unresolved Scribe targets with an accepted current execution/resumption claim or new accepted member progress. An already generated/accepted session anchor is not counted merely because its claim journals continuation for other members. Resuming the same attempt does not require an increased outer ordinal. Neither count means a provider send occurred. `acceptedTargets` counts new deterministic patch acceptance, not merely a Scribe submission. `skippedTargets` counts new skipped classifications; `failedTargets` counts new failed, infrastructure-blocked or attempt-suppressed classifications. Retryable work remains unresolved. `reconstructedTargets` counts entry-accepted targets actually reused by a successful accepted-only reconstruction, without claiming new generation/acceptance. Counts deduplicate target identities across the shared session. `remainingTargets` is selected membership not currently accepted; skipped, failed, retryable and unsupported members remain in it. `deferredTargets` counts future eligible targets outside this batch; excluded targets do not become selected successes. Early gates can have `work: null` when no accepted state is available; null does not mean zero remaining work.

`calls.physicalProviderDispatches` increments at the existing one-shot physical permit immediately before the application transport send, excluding unstarted reservations and all historical sends. It does not measure TCP retries. `logicalProviderCalls` counts distinct SDK `ProviderRequest.Attempt.LogicalCallId` values with at least one actual send in this invocation; it excludes admitted but never-sent logical calls and is not the SDK admission counter. `retryDispatches` counts actual sends with SDK `AttemptNumber > 1`; a normal later tool turn has a new logical identity and is not a retry. Restored product retry epochs are not new physical attempts. If a send lacks this optional correlation, both correlated totals are null and `uncorrelatedDispatches` records coverage loss. `toolCalls` counts operations charged by the shared allowance, including repository/semantic/registered tools and terminal submissions. `activeSent` and `activeReserved` distinguish the current sent-but-unsettled permit from an unstarted reservation; each is zero or one. They are current-process facts, not a reinterpretation of an earlier runner's uncertain reservation.

Each `tokens` member (`input`, `cachedInput`, `uncachedInput`, `output`, `reasoning`) has `observed` and `missingDispatches`. `observed` is the sum of known values: zero when no request was sent, null when every sent request omitted that dimension, and a subtotal when only some reported it. `missingDispatches` counts sent requests without that dimension, including active sends. Total input can be derived when both partitions are known; a missing partition is not inferred from a cache hit/miss label. Complete partitions sum to total input. Reasoning is independently nullable and included in output, never added twice.

`accounting.input/cachedInput/uncachedInput/output` reuse `{observed, conservativeUnobserved, totalCharged}` from the existing allowance authority. `totalCharged = (observed ?? 0) + conservativeUnobserved`; this is a budget quantity, not provider billing. `unsettledDispatches` counts actual sends whose allowance charge has not committed after durable settlement. Valid returned measurements can remain observable when that settlement fails: their charge stays uncommitted and active exposure remains separately visible. `activeExposure` contains the current permit's input/cache/output/time bounds or null. Observing any of these fields grants no budget, dispatch, retry or recovery permission. `history` exposes the same charge triples for campaign `providerRequests`, `input`, `cachedInput`, `uncachedInput`, `output` and `reasoning`, strictly as historical settled totals.

`timing.cliElapsedMilliseconds` measures the recognized non-help command through final snapshot, before physical output writing; `allowanceElapsedMilliseconds` measures from allowance construction (after preflight/Host setup/recovery) through that snapshot, including subsequent finalization. It is the existing agent allowance's clock, not summed provider latency or job wall time. Unavailable clock measurements are null. The Action separately measures child invocation wall time in `invoke-elapsed-milliseconds`; it does not edit the CLI usage object or measure preparation/acquisition/install/job time.

`termination.stopReason` is one of `completed`, `no-op`, `target-limit`, `resource-limit`, `context-limit`, `awaiting-review`, `closed-same-base`, `changed-base`, `cancelled`, `timeout`, `failure`, `unknown`. `source` retains the original closed campaign/preflight/publication source before public outcome normalization. `allowanceStopReason` is the checked allowance enum or null; `scribeFinishReason` is `Completed`, `Unresolved`, `Context` or null, captured before campaign reduction discards it. A recorded Context remains visible even if a later authoritative failure wins. Publication IO retains its first typed failure through renewal, absence and mutation guards; host timeout and caller cancellation are classified when produced, rather than by later teardown flags. Verified partial content/ref observations do not suppress that failure. Cancellation, timeout, later failure and checked resource/target stops retain precedence; a context-limited Scribe stop is distinguishable even when the existing campaign result is conflict/host failure or a successful partial publication.

`complete` requires available work/clock observations, complete token/correlation coverage and settled sends. `partial` retains known observations alongside missing fields, unavailable work/clock or incomplete settlement/correlation. `unknown` means all token dimensions are missing for sent requests, or reporting itself was unavailable. The latter uses explicit null sections rather than fabricated zeros. No-provider no-op/wait/pause and controlled preflight returns retain zero new sends/tools/provider tokens even when work/linkage is unavailable. A sent timeout/lost response retains its send and missing measurements. Optional cost estimates are not emitted.

The object contains no source, prompt, provider response, continuation, raw SDK IDs, credentials or reasoning content. Observations create no persistence authority. Hard kills and physical stream failures cannot guarantee a final envelope/summary; durable campaign facts remain the recovery authority.

| Outcome suffix (`github-proposal.` prefix) | Exit |
| --- | --- |
| published, published-partial, replayed, no-op, awaiting-review, merged | 0 |
| usage (local-invalid with existing `cli.usage.*` diagnostic) | 2 |
| stale-base-after-create, rate-limit, conflict | 3 |
| local-invalid, stale, human-change, permission, closed-unmerged | 4 |
| host-failure | 5 |
| cancelled | 6 |
| timeout | 7 |

Remaining `Admitted`, `RecoveredContentPartial`, and `RecoveredRefPartial` results are conflicts with distinct `github-proposal.admitted`, `github-proposal.recovered-content-partial`, and `github-proposal.recovered-ref-partial` diagnostics. Other publication failure diagnostics use their exact outcome. Publication stderr is `github proposal publication stopped: <diagnostic-code>` plus LF.

Campaign terminal normalization is exhaustive:

| Campaign source suffix (`campaign.` prefix) | GitHub outcome suffix | Exit |
| --- | --- | --- |
| complete, batch-complete, no-work | no-op | 0 |
| provider-retryable, budget-exhausted, attempt-ambiguous, target-limit | conflict | 3 |
| checkpoint-too-large, invalid-configuration, state-missing, state-present, state-corrupt, state-unsafe, state-conflict, lease-conflict, lease-unverifiable, unsupported-revision | local-invalid | 4 |
| incompatible-snapshot, patch-stale | stale | 4 |
| load-failure, target-terminal, provider-terminal, proposal-invalid, patch-rejected, patch-host-failure, state-publication-failure, host-contract-error, unresolved | host-failure | 5 |
| cancelled | cancelled | 6 |
| timeout | timeout | 7 |

All these rows use layer `campaign`, retain the exact authoritative terminal revision when available, and leave publication IDs/URL null. Failures use the original `campaign.*` diagnostic and stderr `github proposal stopped before publication: <campaign-source-outcome>` plus LF. `campaign.invalid-command` after the outer parser, an unknown source, or an impossible candidate combination instead uses layer `presentation`, host-failure/5, diagnostic `github-proposal.campaign-contract-error`, and the same stderr prefix followed by that code.

A verified PR observation supplies its actual generation. The attempted raw operation ID appears only when its commitment matches the observed operation. Predecessor holds/terminals never borrow the attempted operation's raw ID. Stale-draft residuals use their verified raw operation and generation. Claim/partial results use request IDs only after exact operation-commitment correlation. The facade derives `https://github.com/<authenticated-owner>/<authenticated-repository>/pull/<verified-number>` from an authenticated repository and a verified R6 PR observation; it performs no CLI URL lookup. Claim-only replay has no PR URL.

The selected exact campaign/publication terminal wins later host teardown or stop signals. A pre-emission presentation fault without a selected publication result produces one contract-error fallback. Once a publication result is selected it remains authoritative. Physical stream failure cannot retract already emitted bytes; the process preserves the selected exit without appending a second envelope or emitting an unhandled exception.

## Test boundary

The existing test startup assembly can register R2's private, default-inert transport hook by reflection for a canonical numeric-loopback HTTP root with explicit nondefault port, redirects disabled, and the exact synthetic placeholder. Child build hosts do not register it. There is no production argument, configuration field, or ordinary environment endpoint selector. The integration fake implements only required GitHub endpoints and records bounded semantic requests. It never fabricates H1/R6 capabilities. Linux process tests exercise real campaign acceptance/reconstruction, credential admission, HTTP publication/recovery and checkout preservation. Live GitHub Actions proof and scheduled runner policy remain H3.
