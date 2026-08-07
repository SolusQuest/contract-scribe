# Pull request publishing procedure

Before publishing, inspect the branch, working tree, tracking issue when one is useful, changed files, and validation status. Apply the no-issue path in [Pull request workflow](../../10_workflow/pr-workflow.md) from the change's coordination and decision needs rather than diff size alone. Create a draft PR by default with a conventional title and a body that distinguishes completed validation from remaining risk.

For pre-release work, read [Pre-release engineering proportionality](../../00_project/pre-release-engineering.md) before publishing. If the plan actually exceeds the default budget, require its concise exception note before publishing the extra stage. Do not repeat review or validation beyond the changed failure surface, preserve a retired experiment gate, or introduce a second validation product without current evidence.

Review the final PR for scope, sensitive-data and publication boundaries, full-commit-SHA links, and accidental product-contract claims. Never publish secrets, credentials, private downstream material, raw provider responses, or an unverified release or distribution assertion.
