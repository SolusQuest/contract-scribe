# Pull request workflow

Apply Solus Book's [PR workflow](../shared/solus-book/standards/pr-workflow.md) and [PR publishing](../shared/solus-book/skills/pr-publishing/SKILL.md). Use the branch/title convention in [Conventions](../00_project/conventions.md), a draft PR by default, and the [repository PR template](../../.github/pull_request_template.md), unless the task selects another authorized status.

## No-issue exception

The shared [no-issue path](../shared/solus-book/standards/pr-workflow.md#no-issue-path) additionally requires no required follow-up. A runtime change may qualify when its complete intended behavior is stated and tested without a separate product decision or coordination record. Diff size alone does not determine tracking value.

Write `None` in the tracking field and briefly explain why separate tracking adds no value. If scope exceeds those conditions, create and link an issue before Ready or merge.

## Local review requirements

Apply the local [pre-release budget and exception checkpoint](../00_project/pre-release-engineering.md), and the stricter repository information/fixture exclusions in [Conventions](../00_project/conventions.md) before merging.

A contract PR states draft/released status, behavioral impact, affected producers/consumers/artifacts, and actual validation. Explain required version or compatibility handling under [Contract lifecycle](../00_project/contract-lifecycle.md).

Bulk milestone/issue-graph follow-through uses the documentation-first reviewed migration in [Issue workflow](issue-workflow.md#roadmap-and-milestone-changes).

A PR adding or moving a C# project shows the reference graph, absence of forbidden edges, package-dependency classification, and exclusion of M0 experiment assemblies from production references. A TypeScript Action PR proves it invokes the stable CLI payload without duplicating GitHub-adapter or campaign behavior.
