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

Before the first downstream-consumable release, a repository commit is also the identity of a draft contract revision. An artifact version is not a substitute for commit identity and is not an edit counter. The rules for draft amendments, milestone baselines, compatibility freezes, and released versions are defined in [Contract lifecycle](contract-lifecycle.md).

Historical experiment and validation conclusions remain bound to the exact commit, manifest, toolchain, and evidence record that produced them. Updating a draft contract on `main` does not rewrite that history; it creates a new current baseline whose affected validation must be rerun.
