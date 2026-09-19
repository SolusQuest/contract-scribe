# ADR 0005: D2 framework-dependent DLL payload archive

Status: Accepted for pre-release validation; test-only build output, not a released distribution or installable support claim

Date: 2026-09-19

Decision owner: Repository owner; this ADR becomes a repository decision through human-reviewed PR merge.

## Context

Issue #18 (M6-A1) requires one payload distribution channel selected on packed-candidate executable evidence before the M6-A2 GitHub Action (#185) can acquire, verify, and invoke it. The candidate set was frozen by the issue amendment:

- **D1** — a NuGet-delivered .NET tool.
- **D2** — a framework-dependent portable-DLL archive invoked as `dotnet <dll>`, with no apphost.
- **D3** — a framework-dependent RID-specific apphost archive.

ADR 0001 fixed the framework-dependent, in-process semantic baseline: the CLI discovers MSBuild from the installed .NET SDK at runtime (its `Microsoft.Build.*` package references carry `ExcludeAssets="runtime"`). ADR 0004 fixed GitHub-hosted Ubuntu x64 (`ubuntu-latest`) as the sole required runner. Any selected channel must preserve both boundaries: the payload must not bundle the runtime or SDK, must not be trimmed, AOT-compiled, self-contained, or single-file, and must not silently change the SDK/MSBuild discovery surface.

A distribution decision is meaningless without the install path. The evidence boundary therefore covers archive construction, integrity verification, safe extraction, prerequisite gating, execution equivalence against the same source revision, pinning, update, rollback, and cleanup — not `dotnet publish` alone.

## Decision

D2 is selected as the payload channel for pre-release validation.

### Publish contract

`scripts/release/build-payload.sh` produces the archive from the checked-out revision:

- `dotnet publish src/ContractScribe.Cli -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false` after a `linux-x64` restore.
- The builder refuses product-input changes (`src/`, `config/`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `ContractScribe.slnx`, `NuGet.Config`) and records the exact `HEAD` revision.
- The complete publish tree is preserved: `ContractScribe.Cli.dll`, `.deps.json`, `.runtimeconfig.json`, all product and dependency assemblies, Roslyn `BuildHost-netcore/` and `BuildHost-net472/` hosts, satellite resource directories, runtime assets, and `config/defaults.json`. `.pdb` files are excluded.
- The archive name and internal manifest bind the actual informational version of the produced DLL: the builder executes `dotnet <staged>/ContractScribe.Cli.dll --version`, requires the embedded `+<sha>` suffix to equal `HEAD`, and fails on mismatch rather than repairing metadata.
- Archive format: a single gzip-compressed tar with one top-level directory `contract-scribe-<toolVersion>-linux-x64/` containing `payload.json` plus the publish tree. GNU tar deterministic flags (`--sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner`, `gzip -n`) make archives reproducible for identical content.
- `payload.json` (`payloadFormat: 1`) is the archive-internal manifest: tool identity, channel, `toolVersion`, `sourceRevision`, clean-checkout flag, RID, target framework, entrypoint name, `config/defaults.json` digest, the consumer-owned resource bounds, and a complete file inventory (`path`, `length`, `sha256`, permitted `mode`). It contains no self-hash; the outer `.sha256` sidecar covers the whole archive including the manifest. The manifest never enlarges the installer-enforced bounds.
- Loose `schemas/` files are excluded: no tested execution path reads them; the relevant resources are embedded.

### Install contract

`tests/packaging/install-payload.sh` is the reference implementation of the frozen install semantics (the M6-A2 wrapper reimplements them for real acquisition):

- Stages run in order and emit markers: `acquire` (bounded copy into owned private storage) → `digest` (independently supplied expected SHA-256 must match) → `scan` (member policy) → `extract` → `inventory` → `prereq` → `publish`.
- Member inspection is metadata-aware — `tests/packaging/payload_extract.py` (Python 3 stdlib `tarfile`), not a verbose `tar -t` listing. It rejects path traversal, absolute paths, unsafe `.`/`..`/empty components, backslashes and drive names, links, devices, FIFOs and every other non-regular/non-directory member type, GNU and PAX sparse members, duplicate members, paths beneath a file member, members outside the single top directory, and over-bound names, depths, counts, and sizes.
- Extraction writes only validated regular files and directories into a fresh private staging directory with normalized modes; ownership, special modes, and link semantics are never restored from the archive.
- The installed tree is then verified against `payload.json`: exact set equality (no missing, extra, or duplicate files), and per-file length and SHA-256. `ContractScribe.Cli.dll`, `.deps.json`, `.runtimeconfig.json`, and `config/defaults.json` must exist.
- Prerequisites gate product execution: a `dotnet` host resolvable on `PATH` consistent with invocation and a `Microsoft.NETCore.App 10.x` shared runtime are required before publish; SDK presence is recorded separately because semantic commands need it while `--version`/`doctor` do not. Prerequisite failure is a bounded rejection before any ContractScribe code runs.
- Publication is an atomic same-filesystem rename to `<root>/contract-scribe-<toolVersion>-linux-x64/`; an existing destination rejects the install.
- `<root>/current` is an installer-owned symlink selector repointed atomically for pin/update/rollback. Versions install side-by-side; files are never merged across versions and checkpoint state is never mutated by install operations. Cleanup removes only owned `contract-scribe-*` directories carrying a manifest plus the owned selector, and removes the root only when empty; caller-owned neighbouring content survives.

### Execution and oracle contract

The installed entry point is `dotnet <installation>/ContractScribe.Cli.dll`. Packed evidence is produced by two CI jobs:

- `payload_producer` (exact-head checkout): builds the archive, runs the packed-path scenarios against the source-built CLI in `record` mode, and uploads the payload artifacts (`tar.gz`, `.sha256`, `.payload.json`) plus a packaging test kit containing the packaging scripts, the static fixture, recorded expected outputs, the IntegrationTests assembly, and the startup hook.
- `packed_payload` (no checkout): downloads the artifacts, installs through the digest-verified safe path, and runs `verify` mode plus the scripted matrix — identity (`--version`, help, invalid usage, `doctor` from an arbitrary cwd), defaults-file byte equality, deterministic audit canonical bytes, campaign lifecycle against the provider loopback, layered-resolution boundaries, `github-proposal` publish/replay and credential-boundary behaviour against the GitHub loopback, extraction hazards, prerequisite layouts, inventory tampering, pinning, update, rollback, and owned cleanup.

Record and verify execute the same scenario definitions; verify compares projections against the recorded expectations. Canonical audit output is compared as raw bytes — no normalization. Verify mode requires its inputs and the verify script inspects the TRX for the required executed test names, so a skipped or silently empty packed suite fails the gate.

Deterministic legs run inside a real network namespace (`unshare --user --net --map-root-user`): an outbound-connect control must fail, and the campaign/GitHub legs run in the same namespace with only loopback up. After acquisition and caller-side fixture preparation, the packed product performs no further payload or network acquisition; provider transport remains a distinct operation exercised only through loopback substitutes with a synthetic placeholder token.

## Evidence

Measured on this branch (`ubuntu-latest`, .NET SDK per `global.json` `10.0.102` roll-forward):

- Publish produces a portable linux-x64 layout with no apphost: 151 inventory files, ~33.6 MB expanded, ~11.8 MB compressed — far inside the recorded bounds.
- `--version` on the staged DLL reports `0.1.0-dev+<40-hex HEAD>`; the builder fails when the suffix differs from `HEAD`.
- The verify matrix passes locally for every leg runnable off-Linux; `packed_payload` is the authoritative evidence leg and must be green for this selection to stand. Harness failures are not product feasibility failures.

## Bounds and prerequisites

- Supported runner: GitHub-hosted Ubuntu x64 only. Native Windows remains non-gating and unsupported; macOS, ARM64, containers, and self-hosted runners are unvalidated.
- The consumer machine must provide a `dotnet` host with a compatible `Microsoft.NETCore.App 10.x` runtime for all commands, and the .NET SDK for semantic commands (the payload discovers MSBuild from the installed SDK; it is not bundled).
- Enforcement limits are consumer-owned constants: 256 MiB compressed, 512 MiB expanded, 128 MiB per member, 4096 members, 1024-byte member paths, depth 16.
- This is test-only output: no public release, no tag, no NuGet publication, no installable support claim. Released support still requires the M6-A2 wrapper gates and release controls.

## Alternatives and reconsideration

D1 and D3 are documented reconsideration alternatives only and are not implemented or shipped as fallbacks. D1 may be revisited if a NuGet-delivered tool becomes a genuine consumer requirement; D3 if a RID-specific apphost is needed for hosts that cannot name `dotnet`. Reopening requires a new decision with a concrete current consumer need — the same bar ADR 0004 sets for runner re-entry.

If the `packed_payload` evidence fails after harness defects are corrected — that is, the packed candidate itself cannot satisfy the contract — the disposition reverts to a bounded no-selection: the exact source revision, toolchain, profile, archive, and failing evidence are recorded here and in `distribution.md`, no payload is qualified, and dependent Action work stays blocked.

## Consequences

- `ContractScribe.Cli.csproj` needs no packaging-specific changes; the standard publish profile produces the complete closure. No production project, loader, campaign-engine, or GitHub-adapter change is introduced.
- `validate` now aggregates `payload_producer` and `packed_payload`; a failed or skipped packaging job cannot leave CI green.
- M6-A2 (#185) consumes this contract: archive + `.sha256` + manifest, the staged install semantics, the `current` selector, and the bounded rejection policy. Wrapper provenance and Action-host composition remain separate later decisions.
