# M6 plan: consumable GitHub Action release

## Status and purpose

M6 is the current product milestone. This document records the accepted scope, dependency routing, and ownership boundaries established by the M6 refinement tracker (#179–#182). Live GitHub issue bodies and relationships are authoritative for current tracker status.

M6 delivers the validated M5 workflow as a downstream-consumable GitHub Action bound to a selected payload and release policy. It does not add model/provider capability, change M4 state semantics, or publish a release by itself.

## Execution graph

```text
Track C — consumer configuration     Track A — Action/payload        Track R — release
#183 C1 layered consumer config
  -> #184 C2 GitHub runtime authority
       -> #18  A1 D2 DLL payload archive
            -> #185 A2 thin composite Action
                 |-> #186 A3 manual/scheduled caller workflows
                 +-> #187 R1 release candidate + guarded promotion
                      -> #188 R2 live consumer acquisition/replay
                           -> #189 R3 first release (also needs #29)
```

`#29` (Release Gate — Governance) proceeds in parallel by maintainer direction and blocks only `#189` publication. `#27` (process-topology research) stays triggered-only and non-gating.

## Track C — consumer configuration

`#183` (C1) established the public consumer configuration layer: payload defaults, an optional downstream file, and an optional invocation override merged inside the production C# CLI with declared-field, case-sensitive, closed-shape semantics. Invocation authority (campaign lineage, snapshot binding, state path) and product-derived identity are CLI inputs or derived facts, never consumer-layer content. The resolved output is the complete `campaign-configuration-v1` runtime-authority document; `#184` (C2) replaces the `github-proposal` invocation surface so both commands consume that resolved-settings contract, with `github-proposal`'s remaining runtime authority carried by the separately validated `--request` invocation document.

The public field inventory, precedence, merge/null semantics, defaults and their sourcing, limits, and error behavior are recorded in [Consumer configuration](../20_architecture/consumer-configuration.md) and the schema at `schemas/consumer-configuration/v1.schema.json`.

## Track A — payload and Action wrapper

`#18` (A1) produces the verified DLL payload archive. `#185` (A2) wraps acquisition and invocation in a thin composite Action that never duplicates campaign, patch, ledger, or GitHub-publication logic. `#186` (A3) provides caller-owned manual and scheduled workflow examples.

## Track R — release

`#187`–`#189` prepare, prove, and publish the first release under the release policy gates: payload, host, provenance, permissions, secrets, smoke evidence, and maintainer approval. The first release establishes the external compatibility freeze; nothing before it carries a compatibility promise.

## Boundaries

- Configuration field names are the consumer contract; the Action does not expand into one input per field.
- The same C# implementation runs locally, in validation workflows, and inside the Action; the wrapper adds acquisition and invocation only.
- No live H3 revalidation, credential use, or provider claims are introduced by configuration work.
