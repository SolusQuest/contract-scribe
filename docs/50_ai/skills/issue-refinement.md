# Issue refinement procedure

Before creating or refining an issue, read the relevant roadmap, architecture, and [issue workflow](../../10_workflow/issue-workflow.md). Apply its decomposition rule to decision, design, documentation, contract, experiment, validation, and implementation work.

Choose the native issue type from the primary outcome before finalizing the title and body. Keep the title outcome-focused and do not repeat the native type as a title prefix. Set or correct the native type when publishing the issue and read back the remote native type field instead of inferring it from the title or body. If the publishing client cannot set the type during creation, apply it immediately afterward; publication is incomplete until the remote type matches the reviewed value.

State one primary outcome, bounded scope, exclusions, a complete acceptance boundary, dependencies, validation, expected pull-request boundary, and parent relationship. Record sensitive-data, credential, or publication constraints only when the work actually crosses those boundaries; do not add a generic safety section without a concrete risk.

Use a separate issue when work has an independently useful acceptance contract, independently unblockable dependency state, pull request, or review cycle. Verify that every resulting child remains coherent and independently acceptable. Do not split required implementation, tests, contract artifacts, fixtures, or validation unless the separated artifact is itself independently useful and acceptable.

For experimental work, define the question, fixture or evidence source, expected observation, failure classification, and decision it can inform. Do not promise implementation outcomes that the experiment cannot establish.
