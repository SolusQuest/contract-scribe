# Architecture design refinement procedure

Turn an architecture uncertainty into a decision-ready contract: identify the decision, alternatives, evidence required, dependencies, non-goals, acceptance criteria, and ADR destination.

Do not resolve an untested runtime boundary by introducing speculative projects, abstractions, or distribution commitments. Prefer a small synthetic experiment that makes the relevant execution path observable.

For pre-release work, first read [Pre-release engineering proportionality](../../00_project/pre-release-engineering.md). Use its default one-change budget. Record the concise process exception only when the design actually adds an extra PR, merge, review, mutation, compatibility path, or maintained validation mechanism. Treat an actual unjustified extra stage as `PROCESS_COMPLEXITY_BLOCKER`; a missing template is not a blocker.

Before inventing a durable externally consumed or irreversible architecture boundary, contract, persisted format, security mechanism, distribution design, or workflow authority, complete the document's bounded precedent check against a mature comparable system or standard. Adopt only the smallest applicable pattern and omit compatibility or operational machinery whose constraints are absent. Do not require a recorded precedent check for routine internal contracts, reversible implementation, ordinary tests, or local refactoring.

Default to one current production implementation per capability and one active draft contract shape before the first downstream-consumable release. Replace or delete superseded paths in a coherent producer-consumer change instead of adding compatibility layers, migration frameworks, aliases, version coexistence, or silent fallback. Preserve historical truth without retaining obsolete runtime entrypoints; test-only alternatives remain allowed. Any exception must satisfy the current-boundary, evidence, ownership, lifetime, and removal-condition requirements in the pre-release engineering rule and pass its process-complexity checkpoint.

When a real producer or consumer is not yet executable, fix only the minimum product and safety invariants needed to proceed. Test the affected path once it exists. Freeze implementation-level schemas, identities, compatibility, or certification only at a concrete external or irreversible release boundary. Do not build a second validation product or keep a completed experiment active merely to preserve design history.
