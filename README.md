# ContractScribe

ContractScribe is a public source repository for a policy-driven, evidence-grounded C# XML documentation audit and safe proposal system.

The intended pipeline is:

```text
deterministic Roslyn audit
  -> bounded Documentation Scribe
  -> structured documentation proposal
  -> deterministic XML-documentation patch validation
  -> optional GitHub draft pull request
```

The Documentation Scribe is the project's narrow model-assisted role rather than a general coding agent. Its Scribe Runtime can inspect bounded repository evidence and submit structured documentation content, but it cannot edit arbitrary files or mutate GitHub. A deterministic patch engine owns source changes and rejects anything outside selected XML-documentation blocks.

M0 through M5 are complete. M6 delivered the layered consumer configuration, verified DLL payload, thin composite Action, caller workflow examples, release controls, and exact internal-candidate qualification. M6 closes under this engineering and internal-testing scope; no public release or released support is claimed. The adopted [M7 plan](docs/90_roadmap/m7-plan.md) defines caller-owned scheduling and automatic bounded continuation, with implementation and exit evidence still pending. Licensing (#29) and first public publication (#189) remain separate gates deferred until after M7. See the [M6 closeout](docs/90_roadmap/m6-plan.md), [documentation index](docs/README.md), and [roadmap](docs/90_roadmap/roadmap.md).

Machine contracts are still pre-release. Their exact draft meaning is identified by repository commit, and compatible-family version numbers are not incremented for every design correction. The first downstream-consumable release establishes the external compatibility freeze. See [Contract lifecycle](docs/00_project/contract-lifecycle.md).

## Pre-release runner boundary

GitHub-hosted Ubuntu x64 (`ubuntu-latest`) is the sole required M1-M5 source-validation runner and the planned initial M6 GitHub Action target. This is not yet a released or release-qualified support claim. WSL2 is a suggested local Linux route, not independently qualified native-Windows evidence. Native Windows, macOS, ARM64, other Linux environments, containers, and self-hosted runners are unsupported and non-gating.

Initial target repositories must expose an explicit C# solution or project whose caller-prepared prerequisites and design-time MSBuild/Roslyn load succeed on the selected Ubuntu runner. Native-Windows-only workloads, targets, tools, filesystem behavior, process behavior, and host assumptions are outside that boundary. See [ADR 0004](docs/20_architecture/decisions/0004-initial-runner-platform-support.md).

A caller-owned GitHub Actions example for manually and periodically running the Action with verified campaign-state handoff lives under `examples/github-actions/`; see [Caller-owned Action usage](docs/10_workflow/action-usage.md).

The current example requires explicit per-hop activation and replays an existing publication on its default resume path. M6 qualification proved checkpoint recovery and idempotent replay, not automatic consumption of all remaining work or punctual GitHub cron delivery. The M7 plan adopts automatic bounded continuation while retaining best-effort caller scheduling without a timing guarantee; those new behaviors await implementation.

Licensing and contribution policy are to be decided before the first downstream-consumable release, NuGet package or GitHub Action publication, or merge of external code contributions.

Until that decision is made, this repository does not invite external contributions or third-party adoption.
