# Distribution

No downstream-consumable payload channel is selected.

ADR 0001 selected a framework-dependent semantic execution baseline using observed Roslyn, SDK-resolution, trimming/reflection, MSBuild-host, and AOT evidence. [ADR 0002](decisions/0002-process-topology.md) selected one in-process ContractScribe runtime per M1 audit. Native AOT, self-contained publication, split-runtime profiles, and child-process topologies remain deferred and must not be reintroduced through packaging.

The first intended consumer experience is a GitHub Action that invokes the exact selected payload. The Action wrapper is not itself the payload-channel decision: wrapper provenance and payload provenance remain separate and are bound explicitly.

The production GitHub adapter and its Issue, branch, commit, and pull-request reconciliation remain in C#. Action-host language does not own or alter those rules.

## Payload decision

The payload-channel decision compares only candidates compatible with the framework-dependent, in-process baseline. Candidate definitions, install modes, artifact layout, SDK/MSBuild discovery, exact-version pinning, update, rollback, uninstall, offline post-acquisition execution, and supported matrices must be frozen before evidence is produced.

Distribution evidence must execute the full production semantic path and compare canonical results. A `--version` smoke or source-only build is insufficient channel evidence.

No candidate is selected because it is convenient in theory. Selection requires packed-artifact evidence against the same contract and oracle inputs. A no-selection outcome is legal and blocks public Action release without blocking source-based M2 through M5 development.

## Development before channel selection

M1 through M5 may build and validate from pinned source and toolchain inputs. A source-based validation bundle is not a release, installation contract, update channel, or support promise.

The GitHub adapter and workflow may be exercised in synthetic test repositories before a public payload is selected, provided the workflow does not advertise a consumable release and records exact source provenance.

## Action host decision

TypeScript is not required for M1 through M5 and is not required to create GitHub branches, commits, Issues, or pull requests. Those operations are implemented by `ContractScribe.GitHub` and invoked through the production CLI.

The payload-distribution gate compares two initial host candidates:

1. a composite action that acquires or configures the selected framework-dependent payload and invokes the CLI;
2. a JavaScript action built from a small TypeScript source package when payload download, verification, caching, cancellation, or cross-platform invocation cannot remain maintainable in a composite action.

The comparison must freeze:

- supported runner operating systems;
- payload acquisition and integrity verification;
- required preinstalled runtimes and setup steps;
- input, environment, output, annotation, summary, cancellation, and exit-code mapping;
- wrapper and payload identities;
- package and bundled-dependency inventory;
- runner behavior when acquisition is offline, partial, stale, or corrupted;
- exact permissions and secret pass-through behavior.

A TypeScript wrapper, if selected, is an Action host only. It may acquire the payload, invoke the CLI, mask secrets, and translate the stable CLI run envelope into Action outputs. It must not call the provider directly, interpret audit targets, implement campaign transitions, reconcile the ledger, or publish GitHub changes.

The Action host choice requires an ADR under the Release Gate — Payload Distribution. Source development does not select a host by convention.

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
