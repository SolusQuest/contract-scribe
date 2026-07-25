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

M0 architecture and contract validation is complete. M1 production audit work is next; the patch engine, Documentation Scribe, campaign state, GitHub workflow, and consumable GitHub Action are planned later milestones. See the [documentation index](docs/README.md) and [roadmap](docs/90_roadmap/roadmap.md).

Machine contracts are still pre-release. Their exact draft meaning is identified by repository commit, and compatible-family version numbers are not incremented for every design correction. The first downstream-consumable release establishes the external compatibility freeze. See [Contract lifecycle](docs/00_project/contract-lifecycle.md).

Licensing and contribution policy are to be decided before the first downstream-consumable release, NuGet package or GitHub Action publication, or merge of external code contributions.

Until that decision is made, this repository does not invite external contributions or third-party adoption.
