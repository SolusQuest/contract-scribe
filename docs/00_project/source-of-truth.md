# Source of truth

| Surface | Owns |
| --- | --- |
| Repository docs | Long-lived product, workflow, architecture, security, and roadmap decisions. |
| Repository issues | Short-lived, independently executable work and decision tracking. |
| Pull requests | Implementation history, review, and validation evidence. |
| Releases/tags | Immutable consumer-facing versions. |
| Project boards | Views only; never the sole execution contract. |

No important requirement may exist only in a chat, a board field, or an unpinned branch link.

Executable repository work follows [Issue workflow](../10_workflow/issue-workflow.md). Issues remain focused, independently acceptable execution contracts; coordination parents own graphs and closure evidence rather than unbounded executable work.

Pre-release architecture, workflow, review, identity, validation, and certification costs follow [Pre-release engineering proportionality](pre-release-engineering.md). A conversational decision to avoid repeated reopen, re-review, multi-merge, or redundant validation is not durable until that rule and its agent routing are recorded in the repository.

Before the first downstream-consumable release, a repository revision identifies exact draft contract semantics. An artifact version is not a substitute for revision identity and is not an edit counter. The rules for draft changes, historical milestone evidence, compatibility freezes, and released versions are defined in [Contract lifecycle](contract-lifecycle.md).

Historical experiment and validation conclusions remain bound to the exact revision and evidence that produced them. Updating a draft contract on `main` does not rewrite that history or require a successor baseline; current work uses the new main-reachable draft and reruns the checks affected by its change.
