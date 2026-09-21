# Action Interface v1

> **Status:** Pre-release contract under M6-A2 (#185). The Action is not yet a
> published or supported distribution; this document freezes the external
> contract so M6-A3 examples and M6-R1 promotion build against it.

## What the Action is

`action.yml` at the repository root declares a **composite** Action that
acquires the authorized D2 payload, verifies it, installs it under a
job-scoped root, and invokes the production CLI. The Action contains
no product logic: every semantic decision stays inside the CLI; the wrapper
owns only host/transport/security concerns.

Callers pin the Action by Git ref exactly like any other Action
(`uses: SolusQuest/contract-scribe@<ref>`). The pinned ref carries the checked-in
`scripts/action/payload-map.json` — the wrapper-side authorization of the
executable payload bytes. Callers **cannot** supply a payload URL, hash, or
map; no input or environment route substitutes the checked-in authority.

## Inputs

| input | required | notes |
|---|---|---|
| `operation` | yes | `github-proposal-start` or `github-proposal-resume` |
| `repository-root` | yes | absolute or workspace-relative path to the target repo |
| `input` | yes | project/solution path relative to `repository-root` |
| `policy` | yes | policy file path relative to `repository-root` |
| `request` | yes | **inline** github-proposal request JSON (materialized into a private 0600 file) |
| `configuration` | no | consumer layer JSON file path |
| `configuration-override` | no | **inline** layer-override JSON (materialized like `request`) |
| `provider-api-key` | yes* | reaches only the CLI invocation step env |
| `github-token` | yes* | reaches only the CLI invocation step env |
| `acquisition-token` | no | authenticated release enumeration (required for drafts); recommended for published releases too — without it the resolve call is anonymous (60 req/h per egress IP, shared on hosted runners); must differ from `github-token` when both set |

`*` required by the operation's own contract, not by the wrapper.

Empty strings violate `required: true` semantics: `prepare` rejects missing or
empty required inputs. Newline-bearing caller data (`request`,
`configuration-override`) is materialized verbatim into private files; it never
crosses `$GITHUB_ENV`, `$GITHUB_OUTPUT`, annotations, or logs. Only
wrapper-generated identifiers travel through `$GITHUB_ENV`.

## Outputs

`result` is the complete product envelope verbatim (a bounded JSON string).
Convenience fields are lossless projections of it; `action-status` is the
wrapper's own closed vocabulary and never replaces the product outcome.

| output | source |
|---|---|
| `result` | complete CLI envelope JSON (unmodified) |
| `outcome` | envelope `outcome` |
| `exit-code` | CLI exit code |
| `terminal-layer` | envelope `terminalLayer` |
| `diagnostic-codes` | envelope `diagnosticCodes` (comma-joined) |
| `checkpoint-revision` | envelope `checkpointRevision` |
| `pull-request-url` | envelope `pullRequestUrl` |
| `tool-version` | envelope `toolVersion` |
| `campaign-operation` / `publication-operation-id` / `generation-id` / `publication-diagnostic` | envelope projections |
| `payload-version` / `payload-sha256` | the authorized map pair actually executed |
| `install-dir` | versioned payload directory (`contract-scribe-<toolVersion>-linux-x64`) this invocation published or re-verified, under the job-scoped install root `RUNNER_TEMP/contract-scribe-action/payloads`; an existing tree is reused only after digest + inventory re-verification |
| `action-status` | `ok` or `action.<stage>-<reason>` (closed vocabulary) |

Outputs are always emitted where definable: a wrapper failure yields
`action-status` plus empty product fields.

### Envelope validation

The wrapper validates only the physical envelope shape it depends on to
project outputs: a single compact JSON object plus LF within the byte bound,
`githubProposalEnvelopeVersion` 1, the twelve documented keys present, the
projected fields typed as string/int/null/list-of-codes, and stderr empty on
exit 0 and at most one bounded control-free line otherwise. It does **not**
check outcome/exit-code/layer/diagnostic consistency, does not interpret
stderr text, and tolerates additional keys — product semantics are the CLI's
contract alone, so CLI contract evolution (new outcomes, enum members,
fields, or messages) requires no Action change.

The `action-status` vocabulary is closed: `<stage>` is one of `guard`,
`prepare`, `acquire`, `install`, `invoke`, `envelope`, `emit`, and HTTP
transport failures surface only as the fixed classes `http-auth`,
`http-not-found`, `http-conflict`, `http-rate-limit`, `http-server`,
`http-client`, `http-error`, and `http-unreachable` — raw numeric status codes
never enter the public output. Runner cancellation reports
`action.<stage>-cancelled` (stages `prepare`, `acquire`, `install`, `invoke`)
with exit 130.

## Annotations

Exactly one `::error::` annotation per failed run; none on success.

- Wrapper failure (before or around CLI invocation): the annotation carries
  only the closed `action.<stage>-<reason>` code.
- Product-controlled failure (a valid envelope with nonzero exit): the
  annotation carries `<outcome> (exit <code>)`.

CLI stderr is never promoted into annotations; it is preserved in the step log
by the runner.

## Exit semantics

The Action step exits with the CLI's exit code unchanged — a valid
envelope's exit code is returned verbatim, even when the step was signalled
(cancellation maps to 130 only when no valid envelope exists). CLI exit
codes are product semantics (0 published/replayed/no-op/awaiting-review/
merged, 2 usage, 3 bounded-resumable, 4 invalid-state/authority, 5 host, 6
cancelled, 7 timeout); the wrapper never re-judges which code an outcome
permits. A wrapper failure exits 1 with `action-status` set.

## Credentials

- **Interface precondition:** every credential input must be supplied from a
  GitHub masked context — `${{ secrets.* }}`, the built-in masked `${{
  github.token }}`, or an equivalent runner-registered secret. The runner's
  secret masker is what prevents an input from being rendered in the
  workflow log; the composite steps' `::add-mask::` is defense in depth only
  and cannot protect a value that was never registered. A caller that passes
  a literal token as an input has already leaked it before the Action runs —
  the contract therefore requires masked contexts. `provider-api-key` and
  `acquisition-token` always come from `${{ secrets.* }}`; `github-token`
  takes the automatically created job `GITHUB_TOKEN` because the product
  pins the `github-actions[bot]` publisher, which no stored secret can
  authenticate as.
- `provider-api-key` and `github-token` are visible only to the `invoke` step's
  process environment. They never appear in argv, files, `GITHUB_ENV`,
  `GITHUB_OUTPUT`, annotations, or request/plan JSON.
- `acquisition-token` is a separate trust channel used only by `acquire` for
  authenticated release enumeration; it is masked and never reaches the CLI or
  the install tree. When both are supplied, `acquisition-token` must differ
  from `github-token` (enforced by a runner expression before any script sees
  either value). Any read-scoped token suffices — the payload repository is
  public — and supplying one moves resolution off the anonymous rate limit,
  which shared runner egress IPs exhaust easily.
- Acquisition redirects are validated hop-by-hop; `Authorization` is forwarded
  only to the API origin itself, never across origins.

## Payload authority and acquisition

`scripts/action/payload-map.json` (`mapVersion: 1`) is the sole payload
authority. The pinned entry names `repository`, `releaseTag`, `toolVersion`,
`sourceRevision`, `runtimeIdentifier`, `assetName`, `sha256`, and the closed
`assets` set. Acquisition resolves the release by tag (public) or by bounded
authenticated enumeration (draft), requires exactly one tag match and exactly
one matching asset, rejects additional payload-shaped assets, verifies the
downloaded bytes against the pinned SHA-256, and retains the verified archive
under the install root.

The payload archive is byte-reproducible: `build-payload.sh` publishes with
`ContinuousIntegrationBuild=true`, so a clean ubuntu build of a given
revision always produces the same bytes and the pinned `sha256` is a rebuild
expectation. Pre-release the map carries `payload: null` and acquisition
fails closed with `no-authorized-payload`; `action_packaged` proves
reproducibility every run (producer artifact vs. independent clean rebuild)
and exercises the Action through a strictly gated test map. Once a pair is
recorded, the same job rebuilds the mapped `sourceRevision` and requires
byte equality — a mismatch halts release work until a separately reviewed
map change; CI never edits the map.

Install follows the frozen A1 semantics (`payload-install` stage markers):
bounded copy → digest → member-policy scan → extraction of validated regular
files only → exact inventory check against the archive's own `payload.json` →
dotnet host/SDK prerequisite → atomic publish → `current` selector repoint.
Cache reuse re-verifies the retained archive digest and the installed tree
against the archive manifest; poisoned or foreign state fails rather than
self-repairing. See [ADR 0006](../decisions/0006-composite-action-host.md).

The install root is shared by every invocation in the same job (hosted
runners wipe `RUNNER_TEMP` per job, so nothing survives the job). `invoke`
resolves the entrypoint through the installer-owned `current` symlink, not
through `install-dir`: the two are equal for a normal invocation, but a
later invocation in the same job may repoint `current` to a newer versioned
directory. Callers that need the exact tree a run verified should read
`install-dir`/`payload-version`/`payload-sha256`, not resolve `current`
themselves.

## Cancellation and timeouts

Each long-running step enters Python via `exec` so the runner's cancellation
signal reaches the handler directly. Signal policy: first INT/TERM forwards to
the owned child process group; after a bounded grace the group is escalated to
KILL; no ContractScribe process survives the step's termination. Acquisition
and install clean up partial staging on cancellation.

## Runner requirements

GitHub-hosted `ubuntu-latest` x64 only; `prepare` fails closed on other
OS/arch. `dotnet` on `PATH` with the `Microsoft.NETCore.App 10.x` shared
runtime is required before publish; `github-proposal` operations additionally
require an SDK (the CLI performs semantic MSBuild work).

## Test seam

`CONTRACTSCRIBE_ACTION_TEST=1` + `CONTRACTSCRIBE_ACTION_TEST_API_ROOT=<http
loopback URL>` redirect acquisition to a test server. The seam requires
`GITHUB_ACTIONS=true` and `GITHUB_REPOSITORY=SolusQuest/contract-scribe` (both
runner-controlled), a loopback HTTP URL, and a synthetic credential when a
token is supplied. Production runs leave these unset; any partial or
violating combination fails closed.

`CONTRACTSCRIBE_ACTION_TEST_MAP=<absolute path>` substitutes the checked-in
map for acquisition. It is honored only together with the API-root seam
under the same runner-identity gate — a test map can never aim at the
production API — and must name an absolute path to a regular file (no
symlink). The substituted map still passes through the full `load_map`
validation; `payload: null` and malformed maps fail closed exactly as the
committed map would.
