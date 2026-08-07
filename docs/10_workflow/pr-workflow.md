# Pull request workflow

Create substantive changes on a branch and open a draft pull request unless the task explicitly requires ready-for-review status.

PR bodies must explain the change, identify the tracking issue or state `None` with a no-issue justification, record validation actually performed, and list remaining risks. A PR must not claim validation that did not run.

For pre-release planning, implementation, review, publishing, validation, and closure, apply [Pre-release engineering proportionality](../00_project/pre-release-engineering.md). Record a concise exception only when the plan actually exceeds the default PR, merge, review, mutation, compatibility, or validation budget, and select validation from the changed failure surface.

## No-issue exception

A tracking issue is useful when work benefits from separate planning, scheduling, coordination, acceptance decisions, dependency tracking, or durable history. A pull request may proceed without one when all of the following are true:

- the change is bounded, self-contained, and complete in one review cycle;
- it does not need independent scheduling or coordination;
- it introduces no unresolved product or architecture decision, external compatibility commitment, release authority, security or privacy authority change, destructive external mutation, dependency-graph change, or multi-PR outcome;
- it contains no unresolved design choice and needs no follow-up work to be complete.

Examples include focused bug fixes, bounded internal refactors, test additions, maintenance cleanup, an explicitly approved administrative change, and documentation corrections. Runtime behavior may change when the pull request itself states and tests the complete intended behavior and no separate product decision or coordination record is needed. Diff size alone does not determine whether an issue is useful.

The PR body must state `None` for the tracking issue and give a concise justification against this boundary. If implementation or review expands the scope or shows that any condition is false, create and link an issue before marking the PR ready for review or merging it.

Before merging, review sensitive-data and publication boundaries. No private downstream details, secrets, credentials, private or live-run prompts, raw provider responses, complete transcripts, raw logs, machine-local paths, or unpinned private-only references may enter this repository. Reviewed synthetic protocol templates and executable request, tool-call, injection, and minimized normalization fixtures are allowed only under the public-safe fixture rules in [Conventions](../00_project/conventions.md).

For a contract change, the PR body states the draft or released status, behavioral impact, actually affected producers/consumers and contract artifacts, and validation performed. Explain artifact-version or compatibility handling only when a real coexistence or released-consumer boundary makes it relevant.

For roadmap or architecture work that changes a bulk GitHub milestone or issue graph, merge the governing repository documentation first, then apply the reviewed structural migration and read back the changed graph. Ordinary tracker-body follow-through uses the simpler update and field-level readback path in [Issue workflow](issue-workflow.md) and does not require a bulk synchronization manifest.

A PR that adds or moves a project must show the resulting reference graph, demonstrate that forbidden edges remain absent, classify every added package dependency, and keep M0 experiment assemblies out of production references. A TypeScript Action PR must additionally prove that the wrapper invokes the stable CLI payload and does not duplicate GitHub-adapter or campaign behavior.
