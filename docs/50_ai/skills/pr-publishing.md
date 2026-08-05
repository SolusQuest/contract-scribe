# Pull request publishing procedure

Before publishing, inspect the branch, working tree, tracking issue or documented no-issue justification, changed files, and validation status. Apply the no-issue exception in [Pull request workflow](../../10_workflow/pr-workflow.md) narrowly. Create a draft PR by default with a conventional title and a body that distinguishes completed validation from remaining risk.

For pre-release work, read [Pre-release engineering proportionality](../../00_project/pre-release-engineering.md) before publishing. Do not publish or expand a multi-stage plan that matches a canonical process-complexity trigger until its profile justifies every extra stage, and do not repeat review or validation beyond the changed failure surface without current evidence.

Review the final PR for scope, sensitive-data and publication boundaries, full-commit-SHA links, and accidental product-contract claims. Never publish secrets, credentials, private downstream material, raw provider responses, or an unverified release or distribution assertion.
