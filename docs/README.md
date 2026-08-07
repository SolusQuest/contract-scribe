# ContractScribe documentation

This directory is the durable source for product, workflow, architecture, contract, roadmap, and agent-collaboration decisions. GitHub milestones and issues should link to the merged documents rather than replace them.

## Start here

- [Project context](00_project/project-context.md) — current product purpose and implementation status.
- [Origin and scope](00_project/origin-and-scope.md) — product boundaries, non-goals, and open decisions.
- [Roadmap](90_roadmap/roadmap.md) — M0 through M6, release gates, and deferred research.
- [M1 plan](90_roadmap/m1-plan.md) — detailed deterministic-audit scope and planned tracker changes.
- [Architecture](20_architecture/architecture.md) — pipeline, component responsibilities, and authority matrix.

## Project and governance

- [Source of truth](00_project/source-of-truth.md)
- [Conventions](00_project/conventions.md)
- [Contract lifecycle](00_project/contract-lifecycle.md)
- [Issue workflow](10_workflow/issue-workflow.md)
- [Pull-request workflow](10_workflow/pr-workflow.md)
- [Release policy](10_workflow/release-policy.md)

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
- [Audit CLI v1 (M1)](20_architecture/audit-cli.md)
- [Campaign and GitHub workflow](20_architecture/campaign-and-github-workflow.md)
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

These are pre-release v1 drafts backed by schemas, registries, fixtures, and conformance tests. M0 evidence remains pinned to the M0 revision. M1 may coordinate an in-place v1 amendment and revalidation to complete target-profile and documentation-observation semantics.

## Roadmap

- [Product roadmap](90_roadmap/roadmap.md)
- [M1 detailed plan](90_roadmap/m1-plan.md)
- [Completed M0 issue plan](90_roadmap/initial-issue-plan.md)

Repository docs are updated and merged before the corresponding GitHub milestone descriptions and issue graph are changed. Tracker repository-file links use the full SHA of a merged commit reachable from `main`; live issue and milestone URLs remain mutable tracker references.

## Agent collaboration

- [Agent context](50_ai/agent-context.md)
- [Collaboration layers](50_ai/collaboration-layers.md)
- [Task procedures](50_ai/skills/)

`AGENTS.md` is the shared entrypoint for repository-changing agents. It intentionally delegates durable rules to this documentation tree.
