# GitHub proposal CLI (M5-H2)

`github-proposal` composes the existing campaign runner, live H1 admission, and the GitHub publication adapter. The two ordinal, case-sensitive command forms are:

```text
contract-scribe github-proposal start --repository-root <path> --input <path> --policy <path> --request <path> [--configuration <path>] [--configuration-override <path>]
contract-scribe github-proposal resume --repository-root <path> --input <path> --policy <path> --request <path> [--configuration <path>] [--configuration-override <path>]
```

The four required options must appear exactly once, in any order after the operation; the two configuration layers are optional. Both `--name value` and `--name=value` are accepted. There are no defaults, short option aliases, response files, extra operands, or unknown options. Standalone family/operation `--help` or `-h` is supported; combining help with work options is invalid. The exact UTF-8/LF help is [the fixture](../../tests/fixtures/github-proposal/cli/help.txt).

`--configuration` and `--configuration-override` carry the ordinary [consumer configuration](consumer-configuration.md) layers, resolved as payload defaults < `--configuration` < `--configuration-override`. Repository/input/policy resolution retains the [campaign contract](campaign-cli.md). The campaign lineage, snapshot binding, checkpoint-store selection, and GitHub publication claims are caller-owned invocation authority carried by the separately validated `--request` document — never by option values or configuration layers. GitHub invocations persist through the campaign coordination ref; the standalone offline `campaign` command retains its local-file store. The request is bounded to 262,144 UTF-8 bytes, has no BOM, comments, duplicate or unknown properties, and uses exact camel-case property names and lowercase kebab-case enum values. Missing required fields, null required values, invalid types and non-object authorization fail locally. Neither the request nor a configuration layer contains a GitHub token, source/candidate bytes, or an alternate GitHub endpoint.

## Invocation request

`github-proposal-request-v1` is one bounded JSON object:

```json
{
  "githubProposalRequestVersion": 1,
  "campaignLineage": "campaign.example",
  "snapshot": "snapshot.example",
  "checkpointStore": "coordination-ref",
  "github": {
    "repositoryOwner": "Owner",
    "repositoryName": "repo",
    "targetRef": "refs/heads/main",
    "expectedBaseCommitOid": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "operationId": "operation.initial",
    "generationId": "generation.initial",
    "policy": {
      "maximumDocumentationBlocks": 128,
      "maximumDistinctChangedFiles": 128,
      "maximumCumulativePatchBytes": 4194304
    },
    "transition": "initial"
  }
}
```

All illustrated fields are required exactly once.

- `githubProposalRequestVersion` — the integer `1`.
- `campaignLineage` — the caller-attested campaign lineage under the frozen opaque-identifier grammar. This is the single lineage source; no `--campaign-lineage` option exists on this command.
- `snapshot` — the caller-attested immutable snapshot binding under the grammar the retired `--snapshot` option enforced (1–128 opaque-identifier characters).
- `checkpointStore` — exactly `"coordination-ref"`. Local checkpoint paths and the retired `state` property are rejected by the current draft reader.
- `github` — the GitHub publication configuration below.

## GitHub publication claims

An initial publication supplies the `github` shape illustrated above. All illustrated `github` fields are required; the three optional predecessor/authorization fields below may be omitted or null.

```json
{
  "repositoryOwner": "Owner",
  "repositoryName": "repo",
  "targetRef": "refs/heads/main",
  "expectedBaseCommitOid": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "operationId": "operation.initial",
  "generationId": "generation.initial",
  "policy": {
    "maximumDocumentationBlocks": 128,
    "maximumDistinctChangedFiles": 128,
    "maximumCumulativePatchBytes": 4194304
  },
  "transition": "initial"
}
```

The expected base must be the actual immutable Git commit OID. Operation and generation IDs are explicit caller claims; H1 and R1 validate their complete correlation. GitHub policy must fit the accepted M4 ceilings and cumulative candidate. The closed transitions are `initial`, `same-snapshot-append`, `successor-after-merge`, and `successor-after-closed-unmerged`.

`same-snapshot-append` requires `appendPredecessor` with `operationId`, `authorityCommitmentSha256`, `candidateCommitmentSha256`, `generationId`, `snapshotCommitmentSha256`, `policyCommitmentSha256`, and `changedFiles`. Each preceding file is exactly `{ "path": "App/App.cs", "candidateFileSha256": "<64 lowercase hex>" }`. These are claims authenticated by R3–R6, never ownership granted by configuration.

Successor transitions require `terminalPredecessor`, exactly `logicalPredecessorId`, positive `pullRequestNumber`, `generationId`, `headOid`, and `disposition` (`merged` or `closed-unmerged`). A closed-unmerged successor additionally requires the complete `closedUnmergedSuccessorAuthorization` object: `authorizationId`, `logicalPredecessorId`, `closedPullRequestNumber`, `closedGenerationId`, `closedHeadOid`, `freshSnapshotCommitmentSha256`, `freshWorkPlanCommitmentSha256`, `freshCandidateCommitmentSha256`, `newGenerationId`, and `operationId`. H1 validates this against the exact accepted candidate and proposed successor; a boolean or partial object cannot authorize a retry. The scheduled H3 host must reject a non-null authorization before secret access and cannot synthesize one.

## Admission and replay

### Lifecycle admission before Audit

The CLI authenticates the campaign's complete coordination/publication history, current target ref, exhaustively enumerated PRs and their ownership before entering SDK/Roslyn discovery or constructing a provider. It reads the GitHub credential once, after local request/configuration admission. A missing ledger is a first-run possibility only after the managed-PR search proves absence; corrupt or incomplete established recovery authority stops. Observation conveys no publication or mutation entitlement.

An owned open draft or ready PR on its immutable original base returns `awaiting-review` without Audit, provider construction, new targets, checkpoint replacement or publication writes. An open PR whose target HEAD moved returns `stale` with `github-proposal.changed-base`, including for README-only commits and when GitHub's mutable PR `base.sha` advances. Ownership changes, missing active refs, conflicting or multiple active matches and unstable observations stop conservatively. Current terminal facts are always rechecked; a cached terminal cannot hide a reopened PR. A verified terminal PR may retain its head identity after its proposal branch is deleted; an existing branch must still match.

Every retained closed-unmerged generation blocks its immutable original-operation base with `closed-unmerged` and `github-proposal.closed-same-base`, before quota or configuration reconciliation. The gate applies again if HEAD returns to an older closed base. The live PR `base.sha` is never the closure identity. With no active PR, merge or closure on a different HEAD permits fresh exact-current Audit without a generic successor approval. This permission does not supply a new publication operation or bypass the existing downstream publication contract.

After a permitted observation the checkpoint client pins the authenticated current target and acquires its existing owner/epoch claim; a second lifecycle observation checks the pre-discovery read window. Caller publication base claims remain separate. Before planning/provider work, the loaded repository's existing safe baseline is compared to the authenticated target Git tree in both directions: missing remote-only and extra local-only analysis inputs both fail with `github-proposal.checkout-mismatch`. Relevant project/semantic/build inputs must have matching content and supported regular-file Git modes. SDK-generated outputs are reconstructed from bound projects; a tracked semantic input masquerading as a build output is rejected. Separately admitted policy/configuration/style inputs and unrelated repository files are not a whole-checkout cleanliness gate. Existing native confinement and baseline rebind checks remain mandatory.

The effective remote snapshot binds actual target HEAD and correctness-bearing inputs independently of the caller's opaque label or Audit digest. A terminal publication identity distinguishes fresh post-terminal Audit from compatible later unpublished recovery. Compatible unpublished recovery retains the original complete plan, creation quota, selected membership and accepted progress. Invocation quota or lifetime-cap changes do not expand membership. Changed base/policy/profile/style creates fresh work authority without migrating proposals or subtracting unmerged accepted counts, preserving predecessor, archive and known/unknown usage history.

The inert process-observation seam records actual SDK discovery, deferred provider construction and physical exchange entry in confined tests. Wait/pause regressions assert all three are zero, alongside zero provider requests. Lifecycle success uses the existing `awaiting-review` outcome and authenticated PR linkage; stopped paths use a specific sanitized reason. The richer per-invocation usage envelope remains A3 work.

### Accepted-candidate publication

One optional accepted-candidate continuation uses the existing M4 executor. It receives only a real `Accepted`/`Reconstructed` candidate while the repository session is alive, plus an exact fresh checkpoint-store acceptance. A validated accepted candidate can be eligible under selected-batch Complete/AllWorkClosed or Complete/Unresolved; its proposal does not resolve remaining documentation. No-work, exhausted/stopped, failed, stale, candidate-less, and mismatched states cannot publish. Ordinary `campaign` behavior is unchanged when the continuation is absent.

For admitted unpublished initial/successor resume, accepted work is reconstructed before advancing remaining work. Progress is checked again against exact checkpoint readback before publication. Deferred future work stays outside this batch and produces batch-complete/no-op; unresolved selected work remains unresolved/host-failure. A persisted failed, rejected, or stale reconstruction retains its authoritative failure and current revision even when an older accepted candidate remains. Accepted-only retry reuses the existing retry reservation/settlement owner and validates the accepted projection under the fresh process context. The current lower-level append admission remains for G4's publication-contract cutover; a caller's append request cannot pass the CLI's open-PR lifecycle gate or authorize more targets in a published PR.

The [campaign origin](contracts/campaign-state-v1.md) retains the first accepted checkpoint and historical M2 identity. Every fresh reconstruction still executes, validates current evidence and candidate bytes, settles its budget, and conditionally accepts/readbacks a new checkpoint. H1 proves full correspondence with the retained origin before using that original checkpoint/request/result identity in unchanged R1 commitment framing. This makes a real fresh-process resume the same publication operation without reusing stale current-candidate authority. The displayed revision is always the current checkpoint, not the origin revision.

The request and configuration are validated before one read of `CONTRACTSCRIBE_GITHUB_TOKEN`. That credential initially grants only the exact campaign checkpoint object/ref scope, without candidate or PR rights. The invocation acquires a bounded remote owner/epoch claim and uses the same client/store for checkpoint execution and publication. H1 remains credential-free and independently validates the fresh accepted checkpoint and reconstructed candidate before publication authority is bound. The provider credential channel remains separate. Help, usage and local request/configuration failures do not read the GitHub credential; remote-state admission, no-work and resume validation require it.

The source-free saved plan projection must exactly equal freshly validated planning facts before execution. Durable reservations precede provider dispatch. Archive witnesses retain original known and unknown charges without repricing, while ordinary restore reads the directly referenced archive transition and actual predecessor rather than replaying the lifetime. Local files and Action artifacts are not recovery authority.

## Caller contract

A downstream caller — the composite Action or a shell step — invokes the complete path with file locations and options only. It must not merge JSON, derive product or publication policy, choose predecessors, inspect GitHub state, manage provider behavior, or implement retries:

```bash
dotnet <payload>/ContractScribe.Cli.dll github-proposal <start|resume> \
  --repository-root <path> \
  --input <path> \
  --policy <path> \
  --request <path> \
  [--configuration <path>] \
  [--configuration-override <path>]
```

The caller materializes the invocation request and any override to bounded files and passes their locations. Product policy, campaign resolution, runtime authority and publication admission all stay inside the production CLI.

The GitHub facade pins the trusted publisher independently of inspected PRs: `github-actions[bot]`, numeric ID `41898282`, node ID `MDM6Qm90NDE4OTgyODI=`, type `Bot`. This is expected ownership identity, not token-kind proof. One lifetime retains the same authority, payload, HTTP client and reconciler for at most two calls. A first `RecoveredRefPartial` may spend its retained private PR-create entitlement in the second call. A new process reconstructs observations and never reconstructs a lost remote write entitlement.

## Output

Every recognized non-help return is exactly one compact JSON object followed by LF. Properties are ordered as follows:

```text
githubProposalEnvelopeVersion, terminalLayer, cliContractBaseline, toolVersion,
campaignOperation, publicationOperationId, generationId, outcome, diagnosticCodes,
checkpointRevision, pullRequestUrl, publicationDiagnostic
```

Envelope version is `1`. Layers are `usage`, `preflight`, `campaign`, `publication`, or `presentation`. Campaign operation is `start`, `resume`, or null when unavailable. Unavailable operation ID, generation, revision, and PR URL are explicitly null. Successful results have no stderr; controlled failures have exactly one closed, sanitized stderr line. No raw HTTP/header/exception, credential, source, candidate, private path or provider data is emitted.

`publicationDiagnostic` is null outside a failed publication, on success, or when no internal observation is available. Otherwise its ordered fields are `boundary`, `owner`, `coordinationFailure`, `proposalFailure`, `pullRequestOutcome`, `transportCode`, `transportHttpStatus`, `delivery`, `recoveryCode`, `recoveryHttpStatus`, `objectKind`, and `predicate`. Boundary and owner are required closed names; all other fields may be null. HTTP statuses are integers from 100 through 599. Undefined enum values or out-of-range statuses suppress the complete diagnostic to null without replacing the original outcome, exit or stderr. This is an in-place correction of the current draft envelope; no alternate version is supported.

| Field | Closed values |
| --- | --- |
| boundary | Reconcile, Repository, CoordinationRead, CoordinationClaim, CoordinationRecord, CoordinationAdvanceStale, CoordinationAdvanceContent, CoordinationAdvanceRef, GitInspect, GitInspectPredecessor, GitPrepare, GitCreateContent, GitAdvanceRef, PullRequestPreflight, PullRequestObserve, PullRequestCreate, PullRequestRecover |
| owner | Reconciler, Transport, Coordination, GitData, PullRequests |
| coordinationFailure | InvalidInput, MissingPredecessor, DifferentOperation, StageConflict, TargetMoved, HumanChange, Conflict, ObjectMismatch, Bounds, Unresolved, Transport |
| proposalFailure | InvalidInput, Integrity, Bounds, Conflict, Unresolved, Transport |
| pullRequestOutcome | Absent, Appendable, HeldDraft, Ready, Merged, ClosedUnmerged, StaleDraft, Conflict, Unresolved, Failed |
| transportCode, recoveryCode | InvalidRequest, Authentication, Permission, NotFound, Conflict, Validation, RateLimit, Cancelled, Timeout, ResponseLost, InvalidResponse, HostFailure |
| delivery | NotDispatched, Read, NeedsReadback, Ambiguous |
| objectKind | Blob, Tree, Commit |
| predicate | InvalidCorrelation, Cancelled, UnhandledException, RepositoryUnavailable, DifferentOperation, TargetMoved, SuccessorMismatch, AppendMismatch, TransitionMismatch, UnexpectedProposalRef, ClaimMismatch, CurrentMismatch, ClaimedRefPresent, StaleStage, UnexpectedStage, AppendCreateForbidden, CompletionHeadChanged, ObservationLimit, LifecycleOutcome, MissingOrUnexpectedComponent |

The adapter pairs this observation with one invocation's unchanged Core result. Original transport and recovery observations remain separate even when the existing result mapping gives one precedence. Owners that retain only one error do not invent a second; missing owner components use the reconciler's closed predicate without inventing an owner failure. The facade emits only the selected call's pair, including its existing second call after `RecoveredRefPartial`. No diagnostic affects request sequencing, recovery, permissions or capabilities. These fields are observations, not retry instructions or proof of a historical failure's cause. They contain no context identity, OID, body, permission header, exception or retry metadata.

| Outcome suffix (`github-proposal.` prefix) | Exit |
| --- | --- |
| published, replayed, no-op, awaiting-review, merged | 0 |
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
