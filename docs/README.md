# ContractScribe documentation

This directory is the durable source for product, workflow, architecture, contract, roadmap, and agent-collaboration decisions. GitHub milestones and issues should link to the merged documents rather than replace them.

Shared engineering guidance comes from the fixed [Solus Book](shared/solus-book/README.md) submodule. Read [Book adoption](50_ai/book-adoption.md) for initialization, local exceptions, skill discovery, and update instructions. Product documents and local requirements remain owned here.

## Start here

- [Project context](00_project/project-context.md) — current product purpose and implementation status.
- [Origin and scope](00_project/origin-and-scope.md) — product boundaries, non-goals, and open decisions.
- [Roadmap](90_roadmap/roadmap.md) — completed M0–M6 engineering, adopted M7 planning with implementation pending, release gates, and deferred research.
- [M7 plan](90_roadmap/m7-plan.md) — accepted product decisions, defaults, exclusions and exit evidence for caller-owned scheduling and automatic bounded continuation.
- [M6 closeout](90_roadmap/m6-plan.md) — engineering delivery, exact internal qualification, limitations, and deferred first publication.
- [M1 plan](90_roadmap/m1-plan.md) — detailed deterministic-audit scope and planned tracker changes.
- [Architecture](20_architecture/architecture.md) — pipeline, component responsibilities, and authority matrix.

## Project and governance

- [Source of truth](00_project/source-of-truth.md)
- [Conventions](00_project/conventions.md)
- [Contract lifecycle](00_project/contract-lifecycle.md)
- [Issue workflow](10_workflow/issue-workflow.md)
- [Pull-request workflow](10_workflow/pr-workflow.md)
- [Release policy](10_workflow/release-policy.md)
- [Caller-owned Action usage](10_workflow/action-usage.md)
- [Release runbook](10_workflow/release-runbook.md)

The contract lifecycle is especially important before the first release: version numbers identify compatibility families, while a repository revision identifies exact draft semantics. Milestone closure records historical evidence for their exact revision; they do not create an active baseline that later changes must preserve or supersede.

## Architecture

- [Architecture overview](20_architecture/architecture.md)
- [Semantic foundation](20_architecture/semantic-foundation.md)
- [Project structure](20_architecture/project-structure.md)
- [Security boundary](20_architecture/security-boundary.md)
- [Distribution boundary](20_architecture/distribution.md)
- [Documentation Scribe](20_architecture/documentation-scribe.md)
- [Scribe context and prompt economics](20_architecture/scribe-context-and-prompt-economics.md)
- [Documentation patch boundary](20_architecture/documentation-patch-boundary.md)
- [Documentation Patch v1](20_architecture/contracts/documentation-patch-v1.md)
- [Documentation Scribe v1](20_architecture/contracts/documentation-scribe-v1.md)
- [Campaign Planning v1](20_architecture/contracts/campaign-planning-v1.md)
- [Campaign State v1](20_architecture/contracts/campaign-state-v1.md)
- [GitHub Publication v1](20_architecture/contracts/github-publication-v1.md)
- [Audit CLI v1 (M1)](20_architecture/audit-cli.md)
- [Campaign and GitHub workflow](20_architecture/campaign-and-github-workflow.md)
- [M5 publication protocol decision](20_architecture/validation/m5-publication-protocol-decision.md)
- [Architecture decisions](20_architecture/decisions/)

The primary separation is:

```text
audit decides what is missing
  -> Documentation Scribe proposes what to write
  -> patch engine decides what may change
  -> platform adapter decides how to publish
```

The long-lived product graph uses production projects only when their milestone needs a real boundary. TypeScript is not part of the product core and remains an optional thin GitHub Action host selected only by the payload-distribution decision. M3 selects the smallest provider transport and evaluation set that can validate the executable Scribe path; provider names, compatibility corpora, prompt-prefix identities, and economics protocols are not frozen by the pre-M3 roadmap.

## Machine contracts

- [Policy/Configuration v1](20_architecture/contracts/policy-configuration-v1.md)
- [Symbol and Evidence Taxonomy v1](20_architecture/contracts/symbol-evidence-taxonomy-v1.md)
- [Audit Result v1](20_architecture/contracts/audit-result-v1.md)
- [Documentation Patch v1](20_architecture/contracts/documentation-patch-v1.md)
- [Documentation Scribe v1](20_architecture/contracts/documentation-scribe-v1.md)
- [Campaign Planning v1](20_architecture/contracts/campaign-planning-v1.md)
- [Campaign State v1](20_architecture/contracts/campaign-state-v1.md)
- [GitHub Publication v1](20_architecture/contracts/github-publication-v1.md)

These are pre-release v1 drafts backed by schemas or registries where applicable, plus fixtures and conformance tests. M0 evidence remains pinned to the M0 revision. Before the first release, same-version semantics may still change in place under the contract lifecycle, with repository revisions identifying the exact draft semantics.

## Roadmap

- [Product roadmap](90_roadmap/roadmap.md)
- [M6 engineering closeout](90_roadmap/m6-plan.md)
- [M1 detailed plan](90_roadmap/m1-plan.md)
- [Completed M0 issue plan](90_roadmap/initial-issue-plan.md)

Repository docs are updated and merged before the corresponding GitHub milestone descriptions and issue graph are changed. Living planning guidance may link the current `main` path; immutable historical evidence uses the full SHA of a merged commit reachable from `main`. Live issue and milestone URLs remain mutable tracker references.

## Agent collaboration

- [Agent context](50_ai/agent-context.md)
- [Book adoption](50_ai/book-adoption.md)
- [Shared context model](shared/solus-book/agents/context-model.md)
- [Shared task routing and procedures](shared/solus-book/agents/task-routing.md)
- [Local test validation](50_ai/skills/test-validation.md)

`AGENTS.md` is the repository entrypoint for agents. It combines shared guidance with this project's context; the five thin adapters under `.agents/skills` route to Book without copying its procedures.
