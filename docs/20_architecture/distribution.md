# Distribution

D2 — a framework-dependent portable-DLL archive invoked as `dotnet <dll>` — is selected for pre-release validation as the payload channel. [ADR 0005](decisions/0005-d2-dll-payload-archive.md) records the publish, archive, install, oracle, and bound contract and its packed-candidate evidence. This is test-only build output: no public release, tag, NuGet publication, or installable support claim is created.

ADR 0001 selected a framework-dependent semantic execution baseline using observed Roslyn, SDK-resolution, trimming/reflection, MSBuild-host, and AOT evidence. [ADR 0002](decisions/0002-process-topology.md) selected one in-process ContractScribe runtime per M1 audit. Native AOT, self-contained publication, split-runtime profiles, and child-process topologies remain deferred and must not be reintroduced through packaging.

The first intended consumer experience is a GitHub Action that invokes the exact selected payload. The Action wrapper is not itself the payload-channel decision: wrapper provenance and payload provenance remain separate and are bound explicitly.

[ADR 0004](decisions/0004-initial-runner-platform-support.md) selects GitHub-hosted Ubuntu x64 as the planned initial Action target and the sole required M1-M5 pre-release source-validation runner. This does not select a payload or establish released support. Native Windows and other unvalidated runners are unsupported and non-gating.

The production GitHub adapter and its Issue, branch, commit, and pull-request reconciliation remain in C#. Action-host language does not own or alter those rules.

Consumer-facing campaign configuration is a declared-field JSON contract resolved inside the same C# CLI as payload defaults, an optional downstream file, and an optional invocation override ([consumer-configuration.md](consumer-configuration.md)). The Action wrapper exposes invocation inputs and paths to such files; it does not reimplement merge semantics, environment precedence, or one input per configuration field.

## Payload decision

The payload-channel decision compares only candidates compatible with the framework-dependent, in-process baseline. Candidate definitions, install modes, artifact layout, SDK/MSBuild discovery, exact-version pinning, update, rollback, uninstall, offline post-acquisition execution, and supported matrices were frozen before evidence was produced.

Distribution evidence must execute the full production semantic path and compare canonical results. A `--version` smoke or source-only build is insufficient channel evidence.

No candidate is selected because it is convenient in theory. Selection requires packed-artifact evidence against the same contract and oracle inputs. A no-selection outcome is legal and blocks public Action release without blocking source-based M2 through M5 development.

D2 is the selected candidate pending its packed-evidence gate: `scripts/release/build-payload.sh` builds the deterministic `contract-scribe-<toolVersion>-linux-x64.tar.gz` archive (manifest `payload.json` + complete publish tree + `.sha256` sidecar), `tests/packaging/install-payload.sh` and `payload_extract.py` implement the digest-verified safe install contract, and the `payload_producer`/`packed_payload` CI jobs prove install-and-execute without the source checkout. If that evidence fails for product reasons, the disposition reverts to a bounded no-selection recorded in ADR 0005. D1 and D3 remain reconsideration alternatives only.

## Development before channel selection

M1 through M5 may build and validate from pinned source and toolchain inputs. A source-based validation bundle is not a release, installation contract, update channel, or support promise.

The GitHub adapter and workflow may be exercised in synthetic test repositories before a public payload is selected, provided the workflow does not advertise a consumable release and records exact source provenance.

## Action host decision

[ADR 0006](decisions/0006-composite-action-host.md) selects the **composite**
host: `action.yml` plus `scripts/action/` acquire the authorized D2 payload,
verify its pinned SHA-256, install it under the frozen A1 semantics, and invoke
the production CLI — the product GitHub adapter stays in C# and the wrapper
owns only host/transport/security concerns. The interface contract is frozen
in [action-interface.md](action-interface.md); the passed matrix is
`tests/action/verify-action.sh` plus the `action_packaged` CI job, which runs
the real `uses:` path end-to-end against loopback fakes.

The TypeScript fallback was not needed: acquisition, bounded download and
redirect validation, digest verification, cache handling, cancellation
escalation, and unsupported-runner rejection all remain maintainable as small
Python steps inside the composite Action. No JavaScript/TypeScript product
host exists.

## Release composition

The first Action release composes:

```text
Action wrapper identity
  + selected payload identity
  + compatibility mapping
  + permission and secret model
  + release provenance
```

The release is blocked until the governance and payload-distribution gates pass. Requirements are defined in [Release policy](../10_workflow/release-policy.md).

Released Ubuntu support is established only for an exact candidate that passes payload acquisition and integrity checks, wrapper/payload composition, a supported target-repository consumer smoke, release controls, and maintainer approval. Source CI and packed-candidate evidence alone are insufficient.
