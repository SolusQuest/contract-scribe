# Pre-release engineering proportionality

Apply Solus Book's [Pre-release engineering](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md) for coherent outcomes, precedent reuse, current drafts, proportional checks, and corrections. The requirements below specify ContractScribe's process before its first downstream-consumable release. They apply to architecture, issue refinement, implementation, contracts, validation infrastructure, review, publication, and closure.

## Default process budget

The local default is zero or one implementation issue, one implementation PR, one human merge decision, and one coherent review of the complete change. An additional independent review requires a distinct current authority that the coherent review cannot accept: release, security, privacy, destructive mutation, legal obligation, or independent attestation.

An issue's stated implementation mechanism does not freeze ordinary implementation choices. Amend it before implementation only if simplification changes product semantics, external commitments, security or privacy boundaries, destructive-operation authority, dependencies, or the independently acceptable outcome.

## Precedent scope and current implementation

The shared precedent rule also applies to durable internal architecture boundaries. Use primary standards, official documentation, source code, or public design records where available. When precedent materially affects the decision, record the inspected systems, adopted pattern, intentional omissions, and concrete project constraint requiring divergence; no separate research artifact is required for routine internal implementation, tests, draft schemas, or refactoring.

The original rationale used [GitHub flow](https://docs.github.com/en/get-started/using-github/github-flow), Google's [Small CLs](https://google.github.io/eng-practices/review/developer/small-cls.html) and [review standard](https://google.github.io/eng-practices/review/reviewer/standard.html), and [Semantic Versioning 2.0.0](https://semver.org/). ContractScribe adopted lightweight, self-contained branch/PR review and unstable initial-development interfaces. Deployment steps, stacked-change governance, reviewer organizations, and released-version migration machinery were omitted because their triggering constraints were absent. Externally consumed or irreversible production evidence can require additional identity or authority separation only for the concrete integrity failure it prevents.

ContractScribe maintains one current production implementation per capability by default. A one-time mechanical rewrite of checked-in draft state must leave no shipped migration framework. Historical artifacts remain in the current tree only when a current claim or external obligation explicitly depends on them; they are not supported runtime entrypoints. Test-only alternatives, intentional product defaults, and contract-defined fail-closed handling remain permitted. [Contract lifecycle](contract-lifecycle.md) owns version and transfer-provenance requirements.

## Exception-only process-complexity checkpoint

Record an exception only when an actual plan introduces a trigger below. Routine work needs no process-profile section.

| Trigger | Local boundary |
| --- | --- |
| Additional delivery stages | More than one PR for one primary outcome, more than one human merge before it is usable, or a required post-merge mutation beyond the human merge. |
| Additional review or repeated validation | An independent review beyond the coherent review; repeated full build, test, or platform matrices for metadata outside runtime behavior, public contracts, protected inputs, and content-identity preimages. |
| Draft compatibility machinery | Compatibility, migration, coexistence, deprecation, or multiple artifact versions before a real consumer needs them. |
| Historical or duplicated ceremony | Reopening closed design, contract, or historical-authority work; manual ancestry, tracker, or closure ceremony duplicating machine-verifiable state. |
| Additional certification machinery | A custom evidence schema, manifest, identity, aggregator, validator, mutation corpus, self-test framework, publication record, or certification lifecycle beyond ordinary tests and CI. |
| New external authority | A credential, paid resource, deployment, release, destructive action, privacy boundary, or security-sensitive authority. |

The exception note states the extra stage or machinery, current failure prevented, why the simpler reversible path is insufficient, and removal or simplification condition when applicable. Identify the exposed consumer or state, triggering sequence, and expected versus unsafe behavior. Each item above the budget must protect a distinct current failure; otherwise combine or remove it.

Report `PROCESS_COMPLEXITY_BLOCKER` only for an actual unjustified gate, mutation, or maintained mechanism, and present the minimum sufficient alternative. A missing template is not a blocker. Authorized deterministic implementation-local work may continue; stop before publishing or expanding an unjustified process when correction would amend an accepted product, contract, review, or workflow decision.

## Validation design and persistent identity

Before the real producer and consumer are executable, define only the validation goal, essential invariants, concrete failure surface, and minimum platform coverage. An oracle, synthetic consumer, internal handoff, or completed integration run does not freeze implementation schemas, fixture projections, protected-input sets, or final bundle identities. [Contract lifecycle](contract-lifecycle.md#pre-release-validation-provenance) owns workflow provenance, Host Validation restrictions, and decisions deferred to the first consumable release.

At a justified persistent-identity boundary, prefer a content digest, release-bundle identity, or verified tree identity. Bind acceptance to a future squash-merge commit only when that commit identity is itself consumed or authorizes an irreversible action and content identity is insufficient. If this creates another PR or merge, compare it with content-bound or externally stored attestation and name the failure only the heavier option prevents. One accepted review protects unchanged content; metadata-only records repeat review or full validation only if changed authorization semantics invalidate those checks.

## Validation proportionality

Solus Book's [Validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/validation.md) governs evidence and behavioral document consumers. ContractScribe selects these initial checks:

| Change class | Local expectation |
| --- | --- |
| Runtime, public contract, schema, fixture semantics, or platform behavior | Applicable build, focused tests, contract/schema checks, affected integration/platform coverage, and repository CI. |
| Run-local metadata or a real cross-job artifact envelope | Focused producer-consumer, canonicality, stale-state, or digest checks plus existing CI; no duplicated full local product matrix without an affected path. |
| Documentation or tracker metadata without executable contract changes | Appropriate document, link, and rendering checks; remote readback only for an actually changed remote tracker or rendered representation. |

[Local test validation](../50_ai/skills/test-validation.md) owns commands and platform qualification. Existing broader CI does not create another manual matrix or review cycle. Investigate observed failures even when the planned checks were narrower.

## Experiment retirement and closed work

After production supersedes an experiment, remove it from ordinary CI and delete current-tree manifests, allowlists, compatibility modes, tombstone entrypoints, validators, and tests maintained solely for that experiment. Move useful fixtures and regressions under their current production owner; preserve concise conditions, results, limitations, and exact historical revision.

Before reopening closed work, report the current defect and failing path, original acceptance gap versus new requirement, impact on current and historical evidence, current-owner fix or new correction as the default alternative, and whether an adjacent satisfiability check would prevent serial corrections. Obtain the user's explicit decision before reopening or changing historical completion or milestone ownership.

Use the shared [Corrections](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md#corrections) rule for coupled defects. Before materially expanding a correction, identify the coupled path and adjacent failures, current owner and coherent scope, any obsolete lifecycle/identity/validation layer to delete, and material product, commitment, security, privacy, destructive-operation, dependency, or scope decisions needing the user's choice. Unchanged decisions require no new ceremony. Recurrence handling must not delay immediate containment of a security, privacy, legal, destructive-operation, publication-authority, or evidence-integrity failure; stop normal dependent work without using emergency containment to justify another unchecked serial cycle.

Closure records link authoritative commits, checks, identities, and remaining owners, with concise query results rather than copied ancestry or tracker state. A repeatedly delaying process with no distinct findings is itself a design problem; a separate workflow issue is needed only for independent planning or a material decision.
