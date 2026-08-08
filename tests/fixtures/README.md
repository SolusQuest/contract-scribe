# Fixtures

This directory contains public, synthetic fixtures for deterministic contract, audit, and patch-safety validation.

Current pre-release fixtures are owned by the behavior they exercise:

- `policy-configuration/v1` owns Policy parsing and selection cases.
- `symbol-evidence-taxonomy/v1` owns Taxonomy records, origin/skip applicability, and classification conformance cases.
- `audit-result/v1` owns Audit Result payloads, candidate-locator, authority, and fresh-process replay cases.
- `m1-audit-cli` owns the unreleased Audit CLI v1 annex.

`m1-target-observation` remains the historical, commit-pinned ADR 0003 decision annex. Its recorded M0 registry digests are evidence about that accepted decision input and must not be rewritten when current pre-release drafts change.

There is no aggregate pre-release fixture manifest or compatibility alias. Add or update a fixture under its semantic owner and test that behavior directly.
