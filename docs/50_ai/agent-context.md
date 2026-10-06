# Agent context

Read this document after [AGENTS.md](../../AGENTS.md). Shared rules and procedures come from the fixed Solus Book submodule under `docs/shared/solus-book`; [Book adoption](book-adoption.md) owns initialization, updates, and discovery. Use its [context model](../shared/solus-book/agents/context-model.md) and [task routing](../shared/solus-book/agents/task-routing.md), together with the local requirements below. Book's own `AGENTS.md` governs maintenance of Book, not ContractScribe's root context.

Always read [Project context](../00_project/project-context.md), [Conventions](../00_project/conventions.md), and [Source of truth](../00_project/source-of-truth.md). ContractScribe's exclusion of private downstream material applies even where shared defaults allow private project context. Do not use private downstream material as repository implementation context.

| Task | Shared procedure | Required local context |
| --- | --- | --- |
| Resolve an architecture decision | [Design refinement](../shared/solus-book/skills/design-refinement/SKILL.md) | Relevant architecture and roadmap; [Pre-release engineering](../00_project/pre-release-engineering.md); accepted durable architecture decisions belong in the relevant ADR. |
| Refine an issue or milestone | [Issue refinement](../shared/solus-book/skills/issue-refinement/SKILL.md) | [Issue workflow](../10_workflow/issue-workflow.md), [roadmap](../90_roadmap/roadmap.md), relevant milestone plan, and pre-release engineering. |
| Publish or update an issue | [Issue publishing](../shared/solus-book/skills/issue-publishing/SKILL.md) | Issue workflow, intended writes, enabled native types where required, and established publication authority. |
| Prepare or publish a PR | [PR publishing](../shared/solus-book/skills/pr-publishing/SKILL.md) | [PR workflow](../10_workflow/pr-workflow.md), conventions, pre-release engineering, and actual validation. |
| Select or run validation | [Test validation](../shared/solus-book/skills/test-validation/SKILL.md) | [Local test validation](skills/test-validation.md), before choosing an environment, test scope, or command. |

Before planning, refining, implementing, reviewing, publishing, validating, or closing pre-release architecture, issues, contracts, validation infrastructure, artifact identities, or PRs, read the local pre-release engineering rule. Its explicit process budget, exception-only checkpoint, and historical-evidence constraints remain applicable.

Before changing a machine contract or claiming a new artifact version is required, read [Contract lifecycle](../00_project/contract-lifecycle.md). Before changing milestone or issue scope, read the roadmap, relevant milestone plan, and issue workflow.

For proposal or GitHub-workflow work, read [Architecture](../20_architecture/architecture.md): the Documentation Scribe is read-only and structured-output-only, the deterministic patch engine owns source changes, and the GitHub adapter owns platform mutations.

Before changing the Scribe Runtime, repository-read tools, project-context routing, provider transport, prompt construction, or token/cost accounting, read [Documentation Scribe](../20_architecture/documentation-scribe.md) and [Scribe context and prompt economics](../20_architecture/scribe-context-and-prompt-economics.md). Distinguish accepted contracts and current implementation from candidate designs and later planning.

Before creating, removing, renaming, moving, or changing references between C# projects, or adding a TypeScript Action package, read [Project structure](../20_architecture/project-structure.md). Do not create empty projects for future milestones or move GitHub-publication rules into an Action wrapper.

Project rules remain in `docs/00_project`, `docs/10_workflow`, `docs/20_architecture`, and `docs/90_roadmap`. Shared procedures have one maintained body in Book; local validation details remain in `docs/50_ai/skills/test-validation.md`. The discoverable adapters in `.agents/skills` only route to these sources.
