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

M0 architecture and contract validation is complete. M1 has implemented the production audit Host and CLI; one independent read-only smoke remains before M1 closure. The patch engine, Documentation Scribe, campaign state, GitHub workflow, and consumable GitHub Action are planned later milestones. See the [documentation index](docs/README.md) and [roadmap](docs/90_roadmap/roadmap.md).

Machine contracts are still pre-release. Their exact draft meaning is identified by repository commit, and compatible-family version numbers are not incremented for every design correction. The first downstream-consumable release establishes the external compatibility freeze. See [Contract lifecycle](docs/00_project/contract-lifecycle.md).

## Pre-release runner boundary

GitHub-hosted Ubuntu x64 (`ubuntu-latest`) is the sole required M1-M5 source-validation runner and the planned initial M6 GitHub Action target. This is not yet a released or release-qualified support claim. WSL2 is a suggested local Linux route, not independently qualified native-Windows evidence. Native Windows, macOS, ARM64, other Linux environments, containers, and self-hosted runners are unsupported and non-gating.

Initial target repositories must expose an explicit C# solution or project whose caller-prepared prerequisites and design-time MSBuild/Roslyn load succeed on the selected Ubuntu runner. Native-Windows-only workloads, targets, tools, filesystem behavior, process behavior, and host assumptions are outside that boundary. See [ADR 0004](docs/20_architecture/decisions/0004-initial-runner-platform-support.md).

Licensing and contribution policy are to be decided before the first downstream-consumable release, NuGet package or GitHub Action publication, or merge of external code contributions.

Until that decision is made, this repository does not invite external contributions or third-party adoption.
