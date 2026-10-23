# Caller-owned campaign workflows

The [workflow example](../../examples/github-actions/contract-scribe-campaign.yml) invokes the Action with identical stable inputs on each trigger. Copy it to `.github/workflows/contract-scribe-campaign.yml` and copy [caller.py](../../examples/github-actions/caller.py) to `.github/contract-scribe/caller.py`. Replace the Action reference placeholder with a reviewed release tag or immutable commit.

Configure these repository variables and secrets:

| Setting | Purpose |
| --- | --- |
| `vars.CONTRACTSCRIBE_CAMPAIGN_LINEAGE` | Stable campaign identifier and concurrency group. |
| `vars.CONTRACTSCRIBE_CAMPAIGN` | Stable JSON with `githubCampaignVersion: 1`, `campaignLineage`, and `github` containing `repositoryOwner`, `repositoryName`, `targetRef`. Keep its lineage equal to the concurrency setting. |
| `secrets.CONTRACTSCRIBE_PROVIDER_API_KEY` | Provider credential forwarded only to the Action. |
| `secrets.CONTRACTSCRIBE_ACQUISITION_TOKEN` | Optional release-asset acquisition credential, separate from publication. |

Set `CS_INPUT` and `CS_POLICY` to the repository input and policy paths. `CS_CONFIGURATION` may select an ordinary [consumer configuration](../20_architecture/consumer-configuration.md) layer, such as [the example layer](../../examples/github-actions/campaign-layer.example.json). Stable campaign identity and destination stay in the campaign input; the product derives publication authority.

The job grants `actions: read`, `contents: write`, and `pull-requests: write`. The helper authenticates the current default-branch dispatch/schedule and attempt against Actions metadata before the Action receives credentials. The product uses the job's `github.token`; a stored publication token does not substitute for the pinned Actions bot ownership policy. Checkout credentials are disabled and the checked-out HEAD must equal the selected workflow SHA.

## Repeated automatic invocation

Dispatch without choosing a lifecycle mode. The CLI authenticates remote state and current HEAD, then starts, restores, recovers, waits or pauses. The helper authenticates only the trusted trigger; it never generates a request or reads checkpoint/artifact state.

The coordination ref is durable recovery authority. Each invocation acquires a bounded owner/epoch claim. Reservations, complete-plan facts, fixed membership, accounting and accepted checkpoints survive runner replacement. A fresh runner needs no local checkpoint, cache, artifact ID or previous runner filesystem. After operator merge and no active PR, fresh Audit discovers actual remaining work on latest HEAD. Completed no-result batches may advance to their next deferred members without retrying closed skipped/failed work.

The copied workflow defaults to manual dispatch. Uncomment the optional schedule for best-effort GitHub cron, or have an external caller use GitHub's workflow_dispatch API with the same configuration. Delivery has no punctuality guarantee. Keep the recommended 30-minute job timeout and stable campaign concurrency group.

Concurrent invocations are serialized by the workflow concurrency group and independently fenced by the product's exact remote-ref CAS and lease checks. Leave `cancel-in-progress` false. After a hard termination, a later invocation may report a conflict until the old claim expires. Cancellation or expiration cannot establish that an already dispatched provider request was not sent; durable unknown exposure remains charged. An ambiguous PR create never authorizes a blind replacement POST.

A compatible invocation may reconstruct and validate accepted work, recording new checkpoint/accounting revisions while preserving original publication provenance. It must not duplicate an existing PR. Review the CLI outcome and diagnostic instead of treating every nonzero exit as permission to retry. The wrapper does not parse checkpoint bytes, derive predecessor authority, choose new targets, or perform product GitHub writes itself.

## Validation

`tests/action-workflows/verify-examples.sh` checks the workflow structure, automatic invocation, optional schedule, credential confinement, rejected legacy artifact commands, and authenticated run mismatches using a loopback Actions API. It installs the pinned PyYAML test dependency in an isolated temporary environment.

The required `action_examples_producer` and `action_examples_consumer` CI jobs exercise the real packaged Action across fresh runners with synthetic provider/GitHub services. Their diagnostic artifact transfers the fake remote service database as test infrastructure; the product restores exclusively through that service's coordination ref. This is offline transport coverage, not live GitHub lease, permission, trigger, or publication evidence. Existing historical live-proof records retain their original scope.

## Read invocation usage

Consume `steps.<action-id>.outputs['usage-json']` as JSON; the same object appears at `result.invocationUsage`. Its version is `invocationUsageVersion: 1`. Use reported `tokens.*.observed` together with `missingDispatches`, not a missing-as-zero sum; `history` is separately labeled campaign accounting. The step summary displays bounded current work, calls, token coverage and stop information, plus the nullable first `checkpointFailure` with closed operation/stage/reason, numeric revision/status and independently observed cancellation-token scope. It contains no raw errors and grants no retry or checkpoint authority. `awaiting-review` or a lifecycle pause has zero new provider use, even when historical totals are nonzero. `invoke-elapsed-milliseconds` is the Action's child invocation duration, separate from the CLI and allowance clocks. See [the CLI contract](../20_architecture/github-proposal-cli.md#invocation-usage-v1) for exact count scopes and [failure handling](../20_architecture/action-interface.md#envelope-validation) when no valid envelope is available. Hard kills may prevent final outputs; they do not replace durable recovery authority.
