# ADR 0006: Composite Action host and payload binding

Status: Accepted for pre-release validation; the Action is not a published or
supported distribution until M6-R1 promotes a first Release.

Date: 2026-09-20

Decision owner: Repository owner; this ADR becomes a repository decision
through human-reviewed PR merge.

## Context

Issue #185 (M6-A2) requires a GitHub Action that acquires the D2 payload
selected under ADR 0005 and invokes the production CLI, without reimplementing
product authority. Production payload bytes reach the Action only through a
separate channel — Release assets — and the Action needs an explicit trust
anchor for them. (The ~12 MiB publish tree is deliberately not executed from
the checkout.)

## Decision

**Host:** a composite Action at the repository root (`action.yml`), with all
logic in `scripts/action/*.py` (Python 3 stdlib only — present on every
GitHub-hosted runner). A JavaScript action was the alternative candidate; the
composite form remains maintainable because the wrapper is genuinely thin —
acquire, verify, install, invoke — and every semantic decision stays in the
CLI. The `distribution.md` "composite vs JavaScript" gate is resolved in favor
of composite.

**Binding:** `scripts/action/payload-map.json` is the wrapper-side
authorization of the executable bytes. It pins `{repository, releaseTag,
toolVersion, sourceRevision, runtimeIdentifier, assetName, sha256}` for one
payload. This is deliberately stronger than the common Action pattern
(TLS + trust the upstream release channel): the payload executes with a
provider credential and a GitHub write token in its environment, and a Release
asset can be replaced by any `contents:write` token without review — the
checked-in map anchors authorization to reviewed Git history.

A recorded pair is bound to a main-reachable source revision, never to the
wrapper's own HEAD — the archive does not contain the map, so the binding is
acyclic.

**Authoritative bytes:** the archive *is* byte-reproducible —
`build-payload.sh` publishes with `ContinuousIntegrationBuild=true`
(PathMap), and two builds of the same revision from different checkout paths
were shown byte-identical. The earlier byte drift came from embedded
absolute paths (the PDB path in the PE debug directory and Regex
source-generator `<RegexGenerator_g>HASH__` name hashes); MVIDs are
deterministic content hashes, not random. The property must be present for a
clean build — incremental publish does not invalidate on it, and CI builds
are clean. The pinned `sha256` is therefore a rebuild expectation that anyone
can verify on ubuntu.

Pre-release the map carries `payload: null` — the production path fails
closed with `no-authorized-payload`. `action_packaged` proves
reproducibility every run (the `payload_producer` artifact vs. an
independent clean rebuild on a second runner, byte-equal) and exercises the
Action through a strictly gated test map. Once a pair is recorded, the same
job rebuilds the mapped `sourceRevision` and requires byte equality with the
map.

## R1 handoff rule

R1 (#187) records the first authorized pair as a reviewed change to
`payload-map.json` during candidate preparation: `sourceRevision` must be
main-reachable and `sha256` taken from a clean ubuntu build of that
revision. `releaseTag` follows the `payload-<toolVersion>` convention the
gated test map already uses. `action_packaged` must pass its byte-equality
rebuild on that change; a digest mismatch is a **stop condition** — no
promotion, and R1 never edits `payload-map.json` inside the promotion
itself.

## Consequences

- A changed wrapper/payload pair requires re-running the Action verification
  (`action_packaged`); this is the "affected exact-pair validation" clause.
- Pre-release the map carries `payload: null`, so production acquisition
  fails closed (`no-authorized-payload`) until R1 records a pair — expected
  and safe.
- The map file is the per-release update point: promotion to a new payload
  revision is a reviewed one-line-class change, not a contract amendment.
- Reconsideration path: GitHub artifact attestations or signed NuGet
  publication could later replace the per-release hash binding with an
  identity/provenance check; that is a release-grade decision outside this
  ADR.
