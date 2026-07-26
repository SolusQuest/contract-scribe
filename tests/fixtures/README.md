# Fixtures

This directory contains public, synthetic fixtures for deterministic contract, audit, experiment, and patch-safety validation.

`m1-contract-baseline/v1` is the current coordinated pre-release Policy/Taxonomy/Audit v1 baseline. Its manifest pins the amended registries, its row crosswalk maps every executable ADR 0003 row, and its generated-identity and audit-authority vectors contain only synthetic inputs and opaque output-shaped identifiers.

`m1-target-observation` remains the historical, commit-pinned decision annex. Its recorded M0 registry digests are evidence about that accepted decision input and must not be rewritten when the current pre-release v1 baseline changes.
