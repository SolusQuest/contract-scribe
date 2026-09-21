# Caller-owned campaign workflows (M6-A3)

This guide explains how a downstream repository runs the ContractScribe
Action manually and on a schedule with verified campaign-state handoff. It
ships as a copy-and-edit example:

- `examples/github-actions/contract-scribe-campaign.yml` — the workflow.
- `examples/github-actions/handoff.py` — the caller-side transport/activation
  helper the workflow invokes (stdlib-only Python 3).

Copy the workflow to `.github/workflows/contract-scribe-campaign.yml` and
the helper to `.github/contract-scribe/handoff.py` in the repository whose
documentation you want ContractScribe to propose.

The example intentionally requires explicit advancement between hops rather
than unattended campaign scheduling: after every successful run, a
maintainer installs the freshly printed activation before the next
scheduled consumer may proceed.

## Preconditions and the trusted-build assumption

The example assumes the Actions runtime's trusted build inputs: GitHub
Runner, `actions/checkout`, `actions/setup-*` toolchains, the
`upload-artifact`/`download-artifact` transport pair, and the workflow's own
pinned Action revision. The `input`/`policy`/configuration paths inside the
target checkout are caller-controlled build inputs; ContractScribe's MSBuild
audit treats them as code, so the workflow always checks out the exact
`github.sha` of the run and asserts `git rev-parse HEAD` matches before any
credentials are materialized.

## Caller authority surface

The workflow stays inactive until you supply its configuration and
authority. Nothing runs without the caller-provided values:

| Name | Kind | Purpose |
| --- | --- | --- |
| `vars.CONTRACTSCRIBE_CAMPAIGN_LINEAGE` | repository variable | Plain lineage identifier (e.g. `campaign.docs`). Arms both jobs and feeds the per-campaign concurrency group. |
| `vars.CONTRACTSCRIBE_CAMPAIGN` | repository variable | JSON caller claims rendered into `github-proposal-request-v1`: `snapshot`, `operationId`, `generationId`, `targetRef`, `repositoryOwner`, `repositoryName`, `policyCeilings` (`maximumDocumentationBlocks`, `maximumDistinctChangedFiles`, `maximumCumulativePatchBytes`). `targetRef` must equal the run's checked-out ref — the workflow audits the checkout and supplies its `HEAD` as `expectedBaseCommitOid`, so a `targetRef` that differs from the checked-out ref binds the request to the wrong base (for `schedule` that is the repository default branch; to campaign another ref, dispatch the workflow on it). Do not put `campaignLineage` inside this JSON — lineage is the separate variable above and the helper inserts it into the request. |
| `vars.CONTRACTSCRIBE_ACTIVATION` | repository variable | The explicit consumer activation — JSON naming the producer `runId`/`runNumber`/`runAttempt`, the post-upload `artifactId`/`artifactDigest`, and the asserted consumer slot `runNumber`/`event`. Rewritten by you between hops (see below). |
| `secrets.CONTRACTSCRIBE_GITHUB_TOKEN` | secret | Product publication credential — the token ContractScribe uses for the proposal PR it publishes. |
| `secrets.CONTRACTSCRIBE_PROVIDER_API_KEY` | secret | Provider credential for the proposal model endpoint. |
| `secrets.CONTRACTSCRIBE_ACQUISITION_TOKEN` | secret (optional) | Release-asset acquisition channel used only if your payload pin requires it. |

The ordinary file edits in the workflow — `CS_INPUT`, `CS_POLICY`,
`CS_CONFIGURATION` — name the audited target surface and an optional
consumer configuration layer (provider endpoint, budgets). `CS_CONFIGURATION`
may stay empty to use the payload defaults; to supply a layer, copy
`examples/github-actions/campaign-layer.example.json` to a path of your
choice inside the checkout and set `CS_CONFIGURATION` to that path. See
[Consumer configuration](../20_architecture/consumer-configuration.md) for
the layer format.

### Credential topology

Three distinct authorities are in play and are never conflated:

- The **job `GITHUB_TOKEN`** (`contents: read` + `actions: read` on both
  jobs) is the ambient, read-only
  channel the helper uses to authenticate run records and artifact metadata
  and that `download-artifact` uses for acquisition. It cannot publish.
- `secrets.CONTRACTSCRIBE_GITHUB_TOKEN` is the **product publication
  credential** — passed only to the Action step's `with:` and used only
  inside the product for the proposal PR.
- `secrets.CONTRACTSCRIBE_PROVIDER_API_KEY` is the provider credential,
  confined to the same `with:`.
- `secrets.CONTRACTSCRIBE_ACQUISITION_TOKEN` is the optional release-asset
  channel — distinct from both, confined to the same `with:`.

The workflow never places secrets in `env:`, `if:`, or step outputs, and
the handoff artifact contains only `handoff.json` transport/activation
facts plus the opaque checkpoint bytes — never tokens, provider traffic,
logs, or source trees.

## The sealed-slot + explicit-activation protocol

One workflow file owns one campaign lineage and therefore one run-number
domain. A `workflow_dispatch` run performs the expected-absence `start`; a
`schedule` run performs the expected-presence `resume`. Both end the same
way after a successful (exit-0) Action invocation:

1. (start only) The workflow creates `$CS_STATE_DIR` as an owner-private
   `0700` directory under `runner.temp` — expected-absence means the
   checkpoint file is absent, not its parent. The Action writes the initial
   Campaign State there; the CLI enforces the 4 MiB bound and file shape.
2. `handoff.py emit` builds `handoff.json` + `checkpoint.json`.
   `handoff.json` records the **producer** run's authenticated facts
   (repository, workflow id/path, run id/number/attempt, event, head
   ref/sha) and the checkpoint's sha256/size, and a **consumer seal** —
   this repository/workflow, `run_number + 1`, event `schedule`.
3. `actions/upload-artifact` publishes it as the immutable artifact
   `contract-scribe-handoff` (`overwrite: false`, bounded retention).
4. `handoff.py activation` renders the next-hop activation JSON — the
   producer run/attempt plus the **post-upload** artifact id and digest —
   into the job summary and step outputs.

5. You then decide whether the chain advances: copy the printed JSON into
   `vars.CONTRACTSCRIBE_ACTIVATION`. The next scheduled run (which must be
   exactly the sealed `run_number + 1`) presents that activation; the
   helper requires it to match the authenticated producer record, the
   unique named artifact, its digest/expiry/bounds, and the embedded seal —
   any disagreement stops before credentials reach the Action.

Only an exit-0 invocation produces a handoff: a nonzero Action exit fails
the step and the job, consumes the slot, and halts the chain. There is no
in-workflow retry, rearm, or recovery machinery; "consumed" is implicit in
the run-number domain (a rerun sees `run_attempt > 1` and stops; a later
run sees a seal mismatch and stops).

## Recovery and the failure table

Missed or intervening slots are terminal for the chain — the artifact
sealed for slot N is unconsumable at any other run number, so an unrelated
run burning slot N ends the line. Recovery is manual and documented:

1. Disable the schedule (see below).
2. Download the last verified handoff artifact and inspect it.
3. Either resume locally with the CLI (`github-proposal resume` against the
   extracted checkpoint), or arm a fresh `start` run (a new lineage —
   checkpoints are never reused across lineages; the CLI enforces this).
4. The stale artifact expires on its own (bounded retention); you may also
   delete it from the Actions UI — an explicit, manual act. The workflow
   performs no automatic destructive cleanup.

| Producer/consumer fact | Result |
| --- | --- |
| activation absent | resume job skipped entirely |
| wrong repository / workflow / ref | `producer-repo` / `producer-workflow` / `producer-ref` |
| wrong run number or attempt | `producer-run-number` / `producer-rerun` / `own-run-*` |
| wrong artifact id / association | `artifact-id-mismatch` / `artifact-wrong-run` / `artifact-fork` |
| missing or duplicate artifact | `artifact-not-unique` / `artifact-absent` |
| expired artifact | `artifact-expired` / `artifact-expiry` |
| digest mismatch (metadata or bytes) | `artifact-digest-mismatch` / `archive-digest` |
| tampered checkpoint bytes | `checkpoint-digest` |
| failed/cancelled producer run | `producer-conclusion` |
| non-first-attempt run | `own-run-attempt-one` / `producer-rerun` |
| seal mismatch (stale/intervening slot) | `handoff-seal` |
| invalid Campaign State bytes | CLI exit 4 before any credential use |

An important operational detail: a scheduled run that fires **before** you
install the newly printed activation lands on the sealed slot with a stale
activation and burns it — an intended consequence of the missed-slot rule.
If the next cron could fire before you can update the variable, disable the
schedule first (`on.schedule` block commented out or the whole file
disabled under Actions settings), then re-enable it after arming.

## Enabling and disabling the schedule

The checked-in `schedule` carries an inert placeholder cron
(`0 0 30 2 *` — February 30 never fires). To activate periodic operation,
replace it with your reviewed cron and ensure the variables above are set.
To deactivate, restore the inert placeholder or remove the `schedule` entry;
the resume job's `if:` also skips cleanly whenever
`CONTRACTSCRIBE_ACTIVATION` is empty.

## Action reference pin

`uses: SolusQuest/contract-scribe@<RELEASE_TAG_OR_SHA>` is a caller
replacement point. The pre-release Action intentionally ships
`payload-map.json` with `payload: null`, so no runnable production ref
exists until the first authorized release pair (M6-R1). Once released, pin
the release tag or commit SHA — never `@main`. Third-party actions are
pinned to full commit SHAs in the example already.

## Scope boundaries

- One campaign lineage per workflow file. Distinct campaigns get distinct
  workflow files (distinct run-number domains).
- Actions artifacts are the only state transport; Actions caches are never
  state authority.
- `github-proposal` runs with `transition: "initial"` replayed against the
  transferred checkpoint. Append/successor campaign transitions are
  caller-owned scope beyond this example; the CLI owns all product
  transitions, budgets, and publication semantics.
- The helper never parses campaign state semantically — sha256, size and
  archive structure only. Checkpoint acceptance remains entirely in the
  product.

## What CI exercises

`tests/action-workflows/` runs the real helper, the real Action, real
`upload-artifact`/`download-artifact` transport, and the real CLI resume
against deterministic substitutes. Synthetic (test-provided) facts: run
numbers/attempts/events, run conclusions, artifact listing metadata, the
GitHub publication surface. Native (real) facts: payload build and
acquisition, artifact upload/download/digest enforcement, CLI execution,
checkpoint parsing and admission, publication replay. Genuine cross-run
artifact acquisition on real infrastructure remains release-gate evidence,
not something A3's synthetic jobs can certify.
