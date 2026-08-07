# Issue refinement procedure

Before creating or refining an issue, read the relevant roadmap, architecture, [issue workflow](../../10_workflow/issue-workflow.md), and [Pre-release engineering proportionality](../../00_project/pre-release-engineering.md) when the repository has not reached its first downstream-consumable release. First decide whether a separate issue adds useful planning, scheduling, coordination, dependency, or decision value; a coherent change that satisfies the no-issue path may proceed directly to a Draft PR.

Choose the native issue type from the primary outcome before finalizing the title and body. Apply the workflow's ordered type rules, keep the title outcome-focused, and do not repeat the native type as a title prefix.

Before creating an issue or changing its type, resolve the reviewed type against the repository's enabled native issue types and verify that the selected client can set and read it. A body-only correction preserves and reads back the existing type and is not blocked by unavailable type-mutation capability. If a create or type change partially succeeds, record the observed state and reconcile before dependent structural writes continue.

State one primary outcome, bounded scope, exclusions, a complete acceptance boundary, dependencies, validation, expected pull-request boundary, and parent relationship. Record sensitive-data, credential, or publication constraints only when the work actually crosses those boundaries; do not add a generic safety section without a concrete risk.

When an issue actually exceeds the default pre-release PR, merge, review, mutation, compatibility, or validation budget, include the concise exception note from the pre-release engineering rule. Do not add a process section to a routine issue. Report `PROCESS_COMPLEXITY_BLOCKER` only for an actual unjustified extra gate or maintained mechanism, not a missing template.

When an issue introduces a durable externally consumed or irreversible architecture, contract, persisted-state, security, distribution, or workflow authority, include or link the bounded precedent check from the pre-release engineering rule. Do not require that research for routine internal contracts, tests, reversible schemas, or local implementation. Before release, refine toward one current production implementation per capability and one active draft contract shape. Apply the validation-second-product and completed-experiment-retirement rules instead of preserving obsolete harness machinery.

Use a separate issue when work has an independently useful acceptance contract, independently unblockable dependency state, pull request, or genuinely separate authority review. A parent is optional unless it adds coordination beyond the milestone. Verify that every resulting child remains coherent and independently acceptable. Do not split required implementation, tests, contract artifacts, fixtures, or validation unless the separated artifact is itself independently useful and acceptable.

For experimental work, define the question, fixture or evidence source, expected observation, failure classification, and decision it can inform. Do not promise implementation outcomes that the experiment cannot establish.
