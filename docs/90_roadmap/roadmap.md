# Roadmap

## M0 — Product, contracts, and architecture validation

Exit criteria:

- Product boundary, non-goals, and public/downstream ownership are approved.
- Policy/config, audit-result, and taxonomy contracts define versioning and fixture strategy.
- A framework-dependent semantic vertical slice has been validated on a synthetic project.
- Native AOT uses the same semantic path and yields a real feasibility result.
- The loader/distribution ADR is merged.
- At least one independent synthetic repository completes the selected baseline smoke.
- Unresolved decisions exist as explicit issues rather than hidden documentation assumptions.

M0.1–M0.3 define contract inputs to M1 planning. M0.4–M0.6 perform baseline experimentation and select an evidence-based candidate. M0.7 independently validates that selected baseline against an independent synthetic repository. A failed M0.7 smoke keeps M0 open and requires the ADR or selected baseline to be revised and revalidated before M0 can close; non-blocking residual risks are recorded in linked issues.

## M1 — Deterministic audit

Exit criteria: fixed fixtures yield stable JSON; required, optional, forbidden, and skip outcomes are covered; each decision is explainable; audit has no network, provider, or write-token dependency; a real downstream repository completes a read-only smoke; and the selected distribution path remains validated.

## M2 — XML-doc patch safety

Exit criteria: production behavior, signatures, tests, and project files are rejected; valid XML-doc-only patches are accepted; formatting allowance is bounded; adversarial fixtures cover comment/code ambiguity, preprocessors, partial types, and generated files; uncertainty fails closed.

## Candidates after M2

M3 may add evidence-grounded proposal. M4 may add a GitHub proposal workflow. Neither milestone freezes a provider, secret name, state adapter, PR permission, or cost policy.
