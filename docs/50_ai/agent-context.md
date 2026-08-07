# Agent context

Read this document after `AGENTS.md`. Then read `collaboration-layers.md`, the relevant task procedure, and the linked project rules before acting.

Route long-lived rules to `docs/00_project`, `docs/10_workflow`, `docs/20_architecture`, or `docs/90_roadmap`. Route tool-neutral agent procedures to `docs/50_ai/skills`. Keep platform entrypoints thin.

Before planning, refining, implementing, reviewing, publishing, validating, or closing pre-release architecture, issues, contracts, validation infrastructure, artifact identities, or pull requests, read `docs/00_project/pre-release-engineering.md`. Use its default one-change budget and record a concise exception only for an actual extra PR, merge, review, mutation, compatibility path, or maintained validation mechanism. Do not turn a missing process template into a blocker or treat historical experiment machinery as a current requirement.

For immutable historical evidence in an issue or PR, use a full-commit-SHA repository-file link only after verifying that the commit is reachable from `main`. Living planning guidance may link the current `main` path. Treat live issue, pull-request, and milestone URLs as mutable tracker references. Do not use private downstream material as repository implementation context.

Before changing a machine contract or claiming that a new artifact version is required, read `docs/00_project/contract-lifecycle.md`. Before changing milestone or issue scope, read `docs/90_roadmap/roadmap.md`, the milestone plan, and `docs/10_workflow/issue-workflow.md`.

For proposal or GitHub-workflow work, preserve the component boundaries in `docs/20_architecture/architecture.md`: the Documentation Scribe is read-only and structured-output-only; the deterministic patch engine owns source changes; the GitHub adapter owns platform mutations.

Before changing the Scribe Runtime, repository-read tools, project-context routing, provider transport, prompt construction, or token/cost accounting, read both `docs/20_architecture/documentation-scribe.md` and `docs/20_architecture/scribe-context-and-prompt-economics.md`. Until M3 refinement accepts an executable design, treat their detailed component, identity, provider, and cache shapes as candidates; preserve only the roadmap's current product and safety boundaries as fixed requirements.

Before creating, renaming, or moving a C# project, or adding a TypeScript Action package, read `docs/20_architecture/project-structure.md`. Do not create empty projects for future milestones or move GitHub publication rules into an Action wrapper.
