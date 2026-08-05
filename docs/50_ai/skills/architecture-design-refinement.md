# Architecture design refinement procedure

Turn an architecture uncertainty into a decision-ready contract: identify the decision, alternatives, evidence required, dependencies, non-goals, acceptance criteria, and ADR destination.

Do not resolve an untested runtime boundary by introducing speculative projects, abstractions, or distribution commitments. Prefer a small synthetic experiment that makes the relevant execution path observable.

For pre-release work, first read [Pre-release engineering proportionality](../../00_project/pre-release-engineering.md). Complete its process-complexity checkpoint whenever any trigger in that rule applies; do not narrow or extend the canonical trigger set in an architecture decision. Treat an unjustified plan as `PROCESS_COMPLEXITY_BLOCKER` and present the minimum sufficient reversible alternative.

Before inventing a non-trivial architecture boundary, contract, protocol, persisted format, validation or security mechanism, distribution design, or workflow mechanism, complete the document's bounded precedent check against a mature comparable system or standard. Adopt only the smallest applicable pattern, omit compatibility and operational machinery whose constraints are absent, and state the concrete project constraint behind any divergence. Do not turn the check into a general market survey or require it for routine local implementation.

Default to one current production implementation per capability and one active draft contract shape before the first downstream-consumable release. Replace or delete superseded paths in a coherent producer-consumer change instead of adding compatibility layers, migration frameworks, aliases, version coexistence, or silent fallback. Preserve historical truth without retaining obsolete runtime entrypoints; test-only alternatives remain allowed. Any exception must satisfy the current-boundary, evidence, ownership, lifetime, and removal-condition requirements in the pre-release engineering rule and pass its process-complexity checkpoint.

Separate an early design gate from a final freeze when a real producer or consumer is not yet executable. The early gate fixes invariants and evidence ownership; the later gate freezes implementation-level schemas, fixtures, identities, and certification only after an end-to-end satisfiability sweep.
