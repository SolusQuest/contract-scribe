# Pull request workflow

Create substantive changes on a branch and open a draft pull request unless the task explicitly requires ready-for-review status.

PR bodies must explain the change, link the tracking issue, record validation actually performed, and list remaining risks. A PR must not claim validation that did not run. The one-time repository bootstrap and an explicitly approved administrative change may state that no repository issue exists yet.

Before merging, review sensitive-data and publication boundaries. No private downstream details, secrets, credentials, prompts, raw provider responses, complete transcripts, raw logs, machine-local paths, or unpinned private-only references may enter this repository.

For a coordinated contract amendment, the PR body must list the lifecycle status, affected contract artifacts, behavioral compatibility impact, fixtures and conformance suites updated, dependent baselines invalidated or revalidated, and the exact reason an artifact-version increment is or is not required.

For roadmap or architecture work that changes GitHub milestones and issue boundaries, merge the complete repository documentation baseline first. Apply tracker updates only after its full commit SHA is verified as reachable from `main` and the synchronization manifest in [Issue workflow](issue-workflow.md) has been reviewed. Then read back and verify the rendered remote state against that manifest.

A PR that adds or moves a project must show the resulting reference graph, demonstrate that forbidden edges remain absent, classify every added package dependency, and keep M0 experiment assemblies out of production references. A TypeScript Action PR must additionally prove that the wrapper invokes the stable CLI payload and does not duplicate GitHub-adapter or campaign behavior.
