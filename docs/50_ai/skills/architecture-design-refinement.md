# Architecture design refinement procedure

Turn an architecture uncertainty into a decision-ready contract: identify the decision, alternatives, evidence required, dependencies, non-goals, acceptance criteria, and ADR destination.

Do not resolve an untested runtime boundary by introducing speculative projects, abstractions, or distribution commitments. Prefer a small synthetic experiment that makes the relevant execution path observable.

For pre-release work, first read [Pre-release engineering proportionality](../../00_project/pre-release-engineering.md). Complete its process-complexity checkpoint before accepting an architecture that needs multiple ordered pull requests or human merges, post-merge mutations, repeated independent review or full validation, compatibility machinery, or reopening closed historical work. Treat an unjustified plan as `PROCESS_COMPLEXITY_BLOCKER` and present the minimum sufficient reversible alternative.

Separate an early design gate from a final freeze when a real producer or consumer is not yet executable. The early gate fixes invariants and evidence ownership; the later gate freezes implementation-level schemas, fixtures, identities, and certification only after an end-to-end satisfiability sweep.
