# ADR 0006: Composite Action host and payload binding

Status: Accepted for pre-release validation; the Action is not a published or
supported distribution until M6-R1 promotes a first Release.

Date: 2026-09-20

Decision owner: Repository owner; this ADR becomes a repository decision
through human-reviewed PR merge.

## Context

Issue #185 (M6-A2) requires a GitHub Action that acquires the D2 payload
selected under ADR 0005 and invokes the production CLI, without reimplementing
product authority. The payload is a compiled artifact that cannot live inside
the Action ref itself (a `uses:` ref pins repository bytes, and the ~12 MiB
publish tree is deliberately not committed). The executable bytes therefore
arrive through a separate channel — Release assets — and the Action needs an
explicit trust anchor for them.

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

The pinned pair is bound to a main-reachable source revision
(`a960b29db78e41de4aa9df34c9141b5ec2e13fdf`, the A1 merge), never to the
wrapper's own HEAD — the archive does not contain the map, so the binding is
acyclic.

**Authoritative bytes:** the archive is not byte-reproducible across build
hosts (measured: a Windows build and the ubuntu CI build of the same source
differ in both gzip framing and tar payload). The pinned SHA-256 therefore
comes from an authoritative ubuntu build of the bound revision (CI
`payload_producer` artifact), not from a local reconstruction. The
`action_packaged` CI job rebuilds the bound source on ubuntu and asserts the
produced archive equals the map — exact-pair validation on every change.

## R1 handoff rule

R1 (#187) consumes the exact A2-mapped bytes. Permitted outcomes:

- promote the artifact bytes whose SHA-256 already equals the map value; or
- rebuild the bound source revision and promote only if byte-identical.

A digest mismatch on reconstruction is a **stop condition**: the changed pair
routes back through the A2-owned mapping update and exact-pair validation
boundary. R1 never edits `payload-map.json` inside its own promotion. The
mapped archive is preserved via the existing Actions artifact; if it expires
and a rebuild does not reproduce the digest, R1 stops rather than rebinding.

## Consequences

- A changed wrapper/payload pair requires re-running the Action verification
  (`action_packaged`); this is the "affected exact-pair validation" clause.
- Pre-release, no published Release exists, so production acquisition fails
  closed (`release-not-found`) until R1 publishes the mapped asset — expected
  and safe.
- The map file is the per-release update point: promotion to a new payload
  revision is a reviewed one-line-class change, not a contract amendment.
- Reconsideration path: GitHub artifact attestations or signed NuGet
  publication could later replace the per-release hash binding with an
  identity/provenance check; that is a release-grade decision outside this
  ADR.
