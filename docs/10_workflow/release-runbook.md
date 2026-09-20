# Release runbook (M6-R1)

Operator procedure for the manually initiated release candidate pipeline in
`.github/workflows/release.yml`. Everything here is invoked by a maintainer
via `workflow_dispatch` on `refs/heads/main`; nothing is automatic, and
ordinary push/PR CI cannot publish.

## Provisioning prerequisites (maintainer, once)

Before `stage-draft` or `promote` can run, a maintainer must provision:

1. Environment `release-candidate` with required reviewers and a
   deployment branch restriction to `main`; add secret
   `RELEASE_PUBLICATION_TOKEN` (the separate release-publication
   credential, not the product token) to that environment.
2. Environment `release-publication` with the same protections; add the
   same `RELEASE_PUBLICATION_TOKEN` secret to it.
3. Repository variables `RELEASE_DRAFT_ENABLED=true` and
   `RELEASE_PROMOTION_ENABLED=true`. Remove or set false to freeze the
   control plane instantly.

The workflow never creates or assumes these: without them the jobs either
do not run or fail closed without a credential. To revoke publication
capability, delete the secret or disable the environment.

## Operation 1: prepare — build and freeze a candidate

Dispatch `release.yml` with:

- `operation=prepare`
- `source_revision` — 40-hex commit that will be tagged `vX.Y.Z` (the
  public wrapper revision; for the first release it must equal the wrapper
  revision claim).
- `payload_source_revision` — the A1 build revision; becomes
  `payload.sourceRevision` in the map.
- `wrapper` — `contract-scribe-action` (the map's `wrapper` identity; the
  action display name `contract-scribe` in `action.yml` is a separate
  identity and is checked independently).
- `release_version` — `vX.Y.Z` only.

Both revisions must be ancestors of `origin/main`. The job builds the
payload from a detached worktree at `payload_source_revision`, computes the
frozen `candidate.json` identity (its sha256 is the `candidateDigest`),
verifies wrapper/map state at `source_revision`, and uploads one
`release-candidate` artifact. `candidateDigest` is repeatable: re-running
prepare with the same identity inputs yields the same digest.

The artifact bundle contains `candidate.json`, `candidate.sha256`, the
payload archive + sidecar + manifest, `payload-map.json` (the proposed
map content), and `summary.md`.

## Recording the authorized pair (required before staging)

`prepare` emits the proposed `payload-map.json`. A maintainer reviews it,
records it as a normal reviewed change to `scripts/action/payload-map.json`
on main, and CI's `action_packaged` job must pass its byte-equality rebuild
on that change. The map is the sole payload authority: if its pair changes,
the candidate is a new candidate — re-run `prepare` against the revision
that lands the map and use the new digest.

For the first release this means the real sequence is:

1. `prepare` at `(source=payload_source=<candidate rev>)` → proposed map.
2. Reviewed PR lands the map pair → new main revision `M`.
3. `prepare` again at `(source=M, payload_source=<original payload rev>)`
   so the candidate's `sourceRevision` is the revision whose own map
   authorizes the pair. The published `vX.Y.Z` tag will point at `M`.

## Operation 2: stage-draft — unpublished draft staging (R2 vehicle)

Dispatch with `operation=stage-draft` plus:

- `candidate_run_id` — the `prepare` run id (from its summary).
- `candidate_digest` — the digest from the run summary /
  `candidate.sha256`. This binds the exact approved candidate.
- `source_revision`, `payload_source_revision`, `wrapper`,
  `release_version` — the same identity claims.

Requires the `release-candidate` environment (maintainer approval prompt
fires) and `RELEASE_DRAFT_ENABLED=true`. The job:

1. Requires a fresh dispatch (`run_attempt==1`, `actor==triggering_actor`).
2. Authenticates `candidate_run_id` as a successful `workflow_dispatch`
   `release.yml` run on `main`, resolves exactly one unexpired
   `release-candidate` artifact, downloads it by exact artifact id +
   server digest (`digest-mismatch: error`).
3. `verify-candidate`: recomputes the digest, re-checks every identity
   field, reads back repository/run/artifact identity, requires the map at
   `source_revision` to authorize the exact pair, requires main ancestry,
   and requires the current workflow sha to equal the producer's.
4. `stage-draft`: creates the unpublished draft payload Release
   (`payload-<toolVersion>`, `draft`, `prerelease:false`) or adopts the
   exact existing draft, then converges assets to exactly `{assetName}` —
   any extra, duplicate, starter (size-0), or wrong-digest asset stops it.
   No public tag is created.

Result: a `release-staged` artifact with `staged.json` (releaseId,
assetId). Nothing is publicly visible — drafts are not exposed by the
by-tag route.

## R2 qualification (#188)

A consumer repo pins `SolusQuest/contract-scribe@<version_tag>`... — during
qualification, pin the **payload tag** (`payload-<toolVersion>`) or the
exact `source_revision` sha since `vX.Y.Z` does not exist yet. Qualification
records `qualified_release_id` and `qualified_asset_id` from
`staged.json` plus evidence per #188.

## Operation 3: promote — guarded publication (R3, #189)

Dispatch with `operation=promote` plus the identity inputs above and:

- `qualified_release_id`, `qualified_asset_id` — the exact objects
  qualified in R2.
- `approval_reference` — a recorded reference for the current explicit
  maintainer approval (e.g. the review/issue reference).
- `governance_reference` — the recorded #29 governance disposition
  reference (license/contribution-policy preflight).

Requires the `release-publication` environment (second approval prompt)
and `RELEASE_PROMOTION_ENABLED=true`. The job re-verifies everything from
staging, requires a completed-success `ci.yml` run at `source_revision`
with a green `action_packaged`, requires the qualified release/asset ids
to still match the exact frozen metadata, then:

1. Creates `refs/tags/payload-<toolVersion>` at `payload_source_revision`.
2. Publishes the draft (`draft: false`; `prerelease` stays `false`).
3. Creates `refs/tags/vX.Y.Z` at `source_revision` — last, so a failure
   never leaves a resolvable consumer version without its payload.

Writes `release-promotion` evidence (`promotion.json`) recording the
release/asset ids, both tag targets, the CI gate run, the approval and
governance references, and the promoting run id/attempt.

## Recovery — partial states

Every mutation follows pre-read → one write → exact readback. A lost or
ambiguous response is never retried inside a run: the operation re-reads
and adopts only the exact desired state, otherwise stops. Recovery is
always a fresh dispatch (a rerun is a new attempt and is rejected as
stale-authorization).

| State found | Meaning | Recovery |
|---|---|---|
| No draft, no tags | staging never ran | dispatch `stage-draft` |
| Exact draft only | staged, not promoted | qualify (R2), then `promote` |
| Payload tag + draft | publish never ran | dispatch `promote` again |
| Payload tag + published, no `v` tag | version tag never ran | dispatch `promote` again |
| Both tags + published | complete | none |
| Conflicting draft/tag/asset | foreign or tampered state | stop; investigate manually |

"Conflicting" includes: a `vX.Y.Z` tag at any other commit, a published
release for the tag with different metadata, a duplicate/extra/starter/
wrong-digest asset, or more than one release row for the tag. The tooling
never deletes, moves, or replaces release objects — conflicts are resolved
by a maintainer after investigation, never by automation.

## Rollback, withdrawal, retirement

- **rollback**: consumers pin the older `vX.Y.Z`; no state is rewritten.
  Re-release under a *new* version if needed.
- **withdrawal/retirement**: retiring a published release is a maintainer
  action (edit the release to mark it, or draft-hide per policy) — the
  tooling deliberately offers no delete/replace path.
- **Same-version replacement is prohibited**: `vX.Y.Z` once published is
  immutable; a different candidate gets a new version.

## Diagnostics

Each stage emits `release-prepare stage=<name> result=ok|fail
reason=<short>` / `release stage=<op>.<step> result=...` marker lines —
the closed vocabulary for logs and dashboards. A `result=fail` line is
always followed by a nonzero exit. The fake-API request log written by
`tests/release/fake_release_github.py` is the offline audit trail; no
credential values ever appear in it.
