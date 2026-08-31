# Campaign CLI

## Decision

ContractScribe exposes one pre-release durable campaign command through the production CLI:

```text
contract-scribe campaign start --repository-root <path> --input <path> --policy <path> --snapshot <binding> --state <path> --configuration <path>
contract-scribe campaign resume --repository-root <path> --input <path> --policy <path> --snapshot <binding> --state <path> --configuration <path>
```

`start` is an expected-absence create. It never adopts or overwrites an existing checkpoint. `resume` is an expected-presence read and exact-predecessor continuation. It never creates an absent checkpoint. All six options are required exactly once; names, subcommands, and values follow the ordinal, bounded grammar frozen by the CLI fixture tests. The caller supplies the immutable opaque snapshot binding and owns a pre-existing secure state directory outside the repository.

The exact help bytes are stored in `tests/fixtures/campaign/cli/help-campaign.txt`. The top-level CLI advertises both forms while the existing audit, doctor, help, and version behavior remains unchanged.

## Composition and authority

Audit and campaign use `ProductionRepositorySessionHost` for one production M1 lifecycle. Campaign receives the accepted policy, loaded and observed session, evidence, classifications, canonical Audit Result, and host facts while that session is live; it does not reload or reaggregate them. `DocumentationDeclarationAuthorityProjector` derives planning source and owner authority through the same Roslyn declaration-resolution primitive as M2 without constructing a synthetic Patch Request.

The shared host's `total-audit-timeout` governs M1 only. After M1 result validation succeeds, the host retires that deadline before entering the campaign consumer. Campaign execution receives only caller cancellation and enforces its configured persisted campaign, Scribe, and Patch elapsed ceilings; session shutdown remains independently bounded by the host's graceful-shutdown rule. A campaign may therefore validly exceed the M1 audit ceiling without being reclassified as caller cancellation or host timeout.

The runner then composes the existing boundaries in this order:

1. Parse argv and validate repository, input, policy, configuration, snapshot, and state location.
2. Use the accepted X2A file adapter for one authoritative safety/lease/presence read.
3. Validate the current product revision before M1. On resume, retain the accepted checkpoint and classify the caller's opaque snapshot as same-snapshot or changed-base without weakening state, lease, policy, configuration, input, target-profile, or product authority.
4. Run one live M1 session and derive the complete current C1 authority. Same-snapshot resume revalidates the exact C2 context. Changed-base resume constructs a clean revision-zero C2 template, applies only the C3 `Supersede` transition, and conditionally replaces and exactly reads back the predecessor through X2A.
5. Build and parse one current M3 request for the selected work item and preview the actual C3 reservation transition only from the accepted current checkpoint.
6. Only after that preview, read `CONTRACTSCRIBE_PROVIDER_API_KEY` when authenticated HTTPS requires it, construct the existing selected transport, and persist the same reservation with exact readback before dispatch.
7. Persist the proposal or closed result as one transition, rebind any trusted projection to the fresh session, and run cumulative M2 under its own persisted reservation.

The checkpoint remains the only durable execution authority. It contains bounded trusted projections and commitments, not credentials, absolute paths, prompts, provider prose, source files, candidates, or diffs. A changed opaque snapshot is admissible only after the complete live M1/C1 path succeeds and the fresh template matches the predecessor's lineage, configured policy, product revision, input identity, policy authority, and target profile. Same-token context drift and every non-base incompatibility remain fail closed.

## State and interruption behavior

The X2A adapter classifies safety before lease and lease before checkpoint bytes. Read is `NotFound`, `Found`, `Unsafe`, `LeaseConflict`, `LeaseUnverifiable`, `Invalid`, or `Unreadable`. Conditional write is `Written`, `AlreadyPresent`, `PredecessorMissing`, `CurrentMismatch`, `Unsafe`, `LeaseConflict`, `LeaseUnverifiable`, or `PublicationFailure`. The runner maps these typed results and never re-inspects, repairs, or interprets physical state.

Changed-base supersession preserves only campaign lineage, configured lineage policy, monotonic consumptive totals, and the bounded immediate-predecessor summary. The Core reducer conservatively settles old active provider or Patch exposure, discards old current work, proposals, attempts, cumulative candidate authority, and completion state, and installs only the fresh plan's work and ceilings. Distinct opaque bindings always produce distinct execution and work-item identities, including when canonical Audit and consumed content commitments are byte-identical.

Caller cancellation remains effective through X2A's normal pre-publication work. If acceptance reports cancellation, the composition owner performs one non-caller-cancellable authoritative state read: the exact predecessor reports cancellation against the old revision, the exact intended successor reports cancellation against the new revision, a different current checkpoint is a conflict, and invalid or unsafe state retains its existing bounded failure. A pre-replacement crash leaves the predecessor complete; a post-replacement crash leaves the successor complete. No request construction, provider dispatch, or M2 execution uses the successor until exact readback.

`CampaignProcessBoundaryHooks` is an internal default-no-op seam. Its closed names cover reservation, dispatch/result, checkpoint replacement/readback, cumulative patch, and accepted/non-accepted settlement boundaries. Ordinary production execution registers no observer. Process tests may load the test startup-hook assembly through the .NET runtime; that assembly acknowledges an allowlisted boundary and blocks until its parent terminates it. No public option or campaign configuration field exposes this control.

An abrupt process stop emits no application envelope. The last complete validated checkpoint determines recovery: an unresolved provider reservation becomes a bounded ambiguous consumed attempt; a proposal-complete item continues at M2 without provider replay; a patch reservation reruns M2; accepted projections reconstruct the cumulative candidate; and a committed closed result is not rerun.

## Terminal contract

Every recognized non-help campaign return writes one compact LF-terminated JSON object to stdout with fields in this order: `campaignEnvelopeVersion`, `terminalLayer`, `cliContractBaseline`, `toolVersion`, `operation`, `outcome`, `diagnosticCodes`, and `checkpointRevision`. Success writes no stderr. Usage writes the selected existing `cli.usage.*` diagnostic. Every other controlled failure writes exactly one diagnostic whose code is the outcome and whose message is `campaign stopped: <outcome>`.

Campaign terminal selection owns the public precedence through host teardown. In particular, a durable C3 transition accepted by exact X2A readback remains authoritative over later host shutdown failure, cancellation, timeout, presentation failure, or process status; teardown is still attempted and bounded, but it cannot rewrite that durable campaign result.

The closed exit groups are: success `0`, usage `2`, bounded resumable stop `3`, configuration/state/compatibility `4`, terminal execution/publication `5`, cancellation `6`, and timeout `7`. Tests freeze the complete outcome vocabulary and mapping. No raw argument, path, credential, proposal, provider value, exception, or downstream diagnostic crosses this surface.

## Security and non-goals

The configuration is non-secret. `CONTRACTSCRIBE_PROVIDER_API_KEY` is the only credential name, is read at most when a provider-backed reservation is admissible, and is passed only to the selected existing transport. The command does not enumerate or forward the environment. Help, usage, preflight, unsafe/missing/corrupt/conflicting state, incompatibility, M1/C1 failure, no-work, unsupported-work, and pre-provider budget exhaustion do not read it.

The command does not own automatic rebase, GitHub mutation, scheduling, provider conversation restoration, physical checkpoint-store semantics, distributed locks, target parallelism, or release packaging. Its changed-base policy is caller-attested local checkpoint supersession only; it does not discover Git identity or reconcile branches, pull requests, generations, or remote operations.

## Validation

Fast tests freeze parsing, configuration, presentation, state-port mapping, declaration projection, shared-host behavior, and executor ordering. Linux process tests exercise the production CLI, secure X2A adapter, loopback provider, startup-hook interruption matrix, and fresh-process resume. Exact-head GitHub-hosted Ubuntu x64 CI is the required platform conclusion; no live provider or GitHub write is part of ordinary validation.
