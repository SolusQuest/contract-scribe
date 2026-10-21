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

The inert process-observation seam records actual SDK discovery, deferred provider construction and physical exchange entry in confined tests. Wait/pause regressions assert all three are zero, alongside zero provider requests. Lifecycle success uses the existing `awaiting-review` outcome and authenticated PR linkage; stopped paths use a specific sanitized reason. The richer per-invocation usage envelope remains A3 work.

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
checkpointRevision, pullRequestUrl, publicationDiagnostic
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
