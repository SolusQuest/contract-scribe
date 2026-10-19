# Caller-owned campaign workflows

The [workflow example](../../examples/github-actions/contract-scribe-campaign.yml) invokes the Action with an explicitly selected `start` or `resume`. Copy it to `.github/workflows/contract-scribe-campaign.yml` and copy [handoff.py](../../examples/github-actions/handoff.py) to `.github/contract-scribe/handoff.py`. Replace the Action reference placeholder with a reviewed release tag or immutable commit.

Configure these repository variables and secrets:

| Setting | Purpose |
| --- | --- |
| `vars.CONTRACTSCRIBE_CAMPAIGN_LINEAGE` | Stable campaign identifier and concurrency group. |
| `vars.CONTRACTSCRIBE_CAMPAIGN` | JSON with `snapshot`, `operationId`, `generationId`, `targetRef`, `repositoryOwner`, `repositoryName`, and `policyCeilings`. |
| `secrets.CONTRACTSCRIBE_PROVIDER_API_KEY` | Provider credential forwarded only to the Action. |
| `secrets.CONTRACTSCRIBE_ACQUISITION_TOKEN` | Optional release-asset acquisition credential, separate from publication. |

`policyCeilings` contains positive integer `maximumDocumentationBlocks`, `maximumDistinctChangedFiles`, and `maximumCumulativePatchBytes`. Set `CS_INPUT` and `CS_POLICY` to the repository input and policy paths. `CS_CONFIGURATION` may select an ordinary [consumer configuration](../20_architecture/consumer-configuration.md) layer, such as [the example layer](../../examples/github-actions/campaign-layer.example.json). Invocation identity and GitHub claims stay in the request, never in that layer.

The job grants `actions: read`, `contents: write`, and `pull-requests: write`. The helper authenticates the current default-branch workflow dispatch and attempt against Actions metadata before the Action receives credentials. The product uses the job's `github.token`; a stored publication token does not substitute for the pinned Actions bot ownership policy. Checkout credentials are disabled and the checked-out HEAD must equal the selected workflow SHA.

## Explicit start and resume

Dispatch the workflow and choose `start` for a campaign without a checkpoint, or `resume` for its existing checkpoint. Keep the same lineage and compatible snapshot/configuration for a resume. The helper renders the current [GitHub proposal request](../20_architecture/github-proposal-cli.md), including `checkpointStore: "coordination-ref"` and the exact checked-out base OID. The CLI validates all product and publication authority.

The coordination ref is the durable persistence authority. Each invocation acquires a bounded owner/epoch claim before campaign execution. Reservations, accounting, plan facts and accepted checkpoints survive runner replacement. A fresh runner reads them from authenticated Git objects; it needs no checkpoint file, cache, artifact download, activation JSON, sealed run-number slot or access to the previous runner's filesystem. The example has no schedule and does not automatically decide whether start or resume is appropriate. Lifecycle-driven automatic selection remains separate work.

Concurrent invocations are serialized by the workflow concurrency group and independently fenced by the product's exact remote-ref CAS and lease checks. Leave `cancel-in-progress` false. After a hard termination, a later invocation may report a conflict until the old claim expires. Cancellation or expiration cannot establish that an already dispatched provider request was not sent; durable unknown exposure remains charged. An ambiguous PR create never authorizes a blind replacement POST.

A resume may reconstruct and validate accepted work, recording new checkpoint/accounting revisions while preserving original publication provenance. It must not duplicate an existing PR. Review the CLI outcome and diagnostic instead of treating every nonzero exit as permission to retry. The wrapper does not parse checkpoint bytes, derive predecessor authority, choose new targets, or perform product GitHub writes itself.

## Validation

`tests/action-workflows/verify-examples.sh` checks the workflow structure, explicit selection, credential confinement, request shape, rejected legacy artifact commands, and authenticated run mismatches using a loopback Actions API. It installs the pinned PyYAML test dependency in an isolated temporary environment.

The required `action_examples_producer` and `action_examples_consumer` CI jobs exercise the real packaged Action across fresh runners with synthetic provider/GitHub services. Their diagnostic artifact transfers the fake remote service database as test infrastructure; the product restores exclusively through that service's coordination ref. This is offline transport coverage, not live GitHub lease, permission, trigger, or publication evidence. Existing historical live-proof records retain their original scope.
