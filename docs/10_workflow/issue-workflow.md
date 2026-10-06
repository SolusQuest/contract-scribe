# Issue workflow

Apply Solus Book's [Issue workflow](../shared/solus-book/standards/issue-workflow.md), [Issue refinement](../shared/solus-book/skills/issue-refinement/SKILL.md), and [Issue publishing](../shared/solus-book/skills/issue-publishing/SKILL.md) for refinement, coherent decomposition, native-field publication, and readback. This document owns ContractScribe's required metadata and structural-migration rules.

## Native issue types

Use native GitHub types, taking the first matching primary outcome:

1. `Bug`: unexpected problem, regression, or violation of the current contract, even when correction also improves behavior.
2. `Feature`: net-new user/consumer capability, including a new external option or operation.
3. `Enhancement`: intentional improvement with neither a contract violation/regression nor a net-new external capability.
4. `Task`: remaining bounded decision, design, documentation, contract, experiment, validation, implementation, or coordination work.

The shared publication procedure's enabled-type and write/read capability checks are mandatory for creation and intentional retyping, including organization-managed `Enhancement`. Stop before such a write if the reviewed type cannot be applied safely. For a body-, title-, or relationship-only correction, preserve the type and include it in changed-field readback. A partial type failure stops dependent writes until reconciliation.

Outcome titles do not repeat types. `Design:` or `Experiment:` is permitted only when it materially disambiguates the outcome and does not replace native `Task`.

## Executable issue requirements

Every executable issue must state all of these, rather than treating the shared template as optional acceptance detail:

- one primary outcome, bounded scope, and explicit exclusions;
- complete acceptance, required validation/evidence, and dependencies with independently unblockable states;
- expected PR/review boundary, or why no repository change is expected;
- owning parent when it adds coordination value, otherwise its actual milestone/track;
- authoritative document links under [Source of truth](../00_project/source-of-truth.md).

Before declaring pre-release work agent-ready, apply the local [process budget and exception checkpoint](../00_project/pre-release-engineering.md). Each split child requires its own independently acceptable outcome; a dependency state alone cannot justify an otherwise unacceptable child.

Use repository-local parent/sub-issue relationships for decomposed work and full URLs for cross-repository traceability. A coordination parent may add a dependency graph, blocker classification, exit-evidence checklist, or closure view; it must not retain unbounded executable work or merely mirror milestone membership, state, or CI links. Every executable issue belongs to its actual milestone/track. Non-gating research, release gates, and later-track work belong to the milestone that owns their completion condition.

M0 experiments report observed evidence and unresolved risks; they must not make an untested option a project-wide assumption.

## Roadmap and milestone changes

Long-lived milestone scope and exit criteria live in repository docs. Tracker drafts may be prepared during document review; dependent implementation starts only after those docs merge. Ordinary body corrections may use current `main` links without an immutable publication package.

A bulk milestone, parent/sub-issue, dependency-graph, or multi-record migration requires one reviewed synchronization manifest when partial application would create ambiguous planning state. Before the first write, verify the governing docs are reachable from `main`, select living or historical evidence links under Source of truth, review an entry for every structural write, and complete required native-type handling.

Each entry records only intended fields:

| Entry content | Required detail when applicable |
| --- | --- |
| Target and operation | Existing target or proposed issue/milestone; `create`, `update`, `close`, `move`, or `no change`. |
| Reviewed text | Title/body for an issue; title/description for a milestone. |
| Native type | Reviewed type for creation, retyping, or a structural dependency on type; verified mutation/readback path for creation or retyping. |
| State and relationships | Expected state, owning parent, milestone/non-product track, and native dependencies. |
| Contractual metadata and evidence | Labels with contractual meaning; immutable document links for historical claims. |

The shared complete-graph readback applies to this migration. Ordinary body/title updates need no repository-wide graph snapshot, source/target digest table, publication-capability proof, or per-body digest.

## Contract changes

Read [Contract lifecycle](../00_project/contract-lifecycle.md) for the required affected surfaces, version decision, provenance, and released compatibility disposition, and [Pre-release engineering](../00_project/pre-release-engineering.md) for early validation and release-freeze limits.

Keep unresolved materially different semantics in a decision issue, or an experiment issue when executable evidence must resolve them. Do not hide that choice in production implementation. A released breaking-change issue must own or depend on its new version, compatibility behavior, migration guidance, and old-version disposition.

## Project-boundary changes

An issue creating, removing, renaming, or changing references between C# projects cites [Project structure](../20_architecture/project-structure.md) and states classification (production/test/fixture/experiment/tool/host), required dependency or authority boundary, allowed/forbidden references, introduced package/runtime dependencies, migrated source, architecture/integration checks, and the active milestone needing it.

Do not reserve an empty future project or move product logic into a TypeScript wrapper to avoid a C# adapter/Core contract. The Action-host decision belongs to the payload-distribution gate and requires candidate evidence. GitHub mutation remains in the M5 .NET adapter regardless of wrapper language.
