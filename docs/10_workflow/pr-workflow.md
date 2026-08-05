# Pull request workflow

Create substantive changes on a branch and open a draft pull request unless the task explicitly requires ready-for-review status.

PR bodies must explain the change, identify the tracking issue or state `None` with a no-issue justification, record validation actually performed, and list remaining risks. A PR must not claim validation that did not run.

For pre-release planning, implementation, review, publishing, validation, and closure, apply [Pre-release engineering proportionality](../00_project/pre-release-engineering.md). Complete its process-complexity checkpoint before publishing or expanding a plan that matches any canonical trigger, and select validation from the changed failure surface.

## No-issue exception

A tracking issue remains the default whenever work benefits from separate planning, coordination, acceptance criteria, dependency tracking, or durable history. A pull request may proceed without one only when all of the following are true:

- the change is small, bounded, self-contained, and complete in one review cycle;
- it is low risk and does not need independent scheduling or coordination;
- it preserves runtime behavior, public and product contracts, compatibility, security and privacy boundaries, release and distribution commitments, dependency and project graphs, committed milestone scope, and the tracker issue graph;
- it contains no unresolved design choice and needs no follow-up work to be complete.

Examples include the one-time repository bootstrap, an explicitly approved administrative change, typo or formatting corrections, and a documentation-only clarification that records but does not promote a future candidate. Diff size alone does not qualify a change for this exception.

The PR body must state `None` for the tracking issue and give a concise justification against this boundary. If implementation or review expands the scope or shows that any condition is false, create and link an issue before marking the PR ready for review or merging it.

Before merging, review sensitive-data and publication boundaries. No private downstream details, secrets, credentials, private or live-run prompts, raw provider responses, complete transcripts, raw logs, machine-local paths, or unpinned private-only references may enter this repository. Reviewed synthetic protocol templates and executable request, tool-call, injection, and minimized normalization fixtures are allowed only under the public-safe fixture rules in [Conventions](../00_project/conventions.md).

For a coordinated contract amendment, the PR body must list the lifecycle status, affected contract artifacts, behavioral compatibility impact, fixtures and conformance suites updated, dependent baselines invalidated or revalidated, and the exact reason an artifact-version increment is or is not required.

For roadmap or architecture work that changes GitHub milestones and issue boundaries, merge the complete repository documentation baseline first. Apply tracker updates only after its full commit SHA is verified as reachable from `main` and the synchronization manifest in [Issue workflow](issue-workflow.md) has been reviewed. Then read back and verify the rendered remote state against that manifest.

A PR that adds or moves a project must show the resulting reference graph, demonstrate that forbidden edges remain absent, classify every added package dependency, and keep M0 experiment assemblies out of production references. A TypeScript Action PR must additionally prove that the wrapper invokes the stable CLI payload and does not duplicate GitHub-adapter or campaign behavior.
