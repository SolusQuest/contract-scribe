# Issue workflow

Use the native GitHub issue type as the authoritative type metadata. Apply these rules in order and use the first match:

1. `Bug` for an unexpected problem, regression, or behavior that violates the current contract. This takes precedence even when the correction also improves existing behavior.
2. `Feature` for a net-new user- or consumer-visible capability, including a new external option or operation on existing functionality.
3. `Enhancement` for an intentional improvement to existing functionality, behavior, or workflow that neither corrects a contract violation or regression nor introduces a net-new external capability.
4. `Task` for all remaining bounded decision, design, documentation, contract, experiment, validation, implementation, or coordination work.

Choose the type from the issue's primary outcome. Do not repeat a native type as a title prefix such as `Task:`, `Bug:`, `Feature:`, or `Enhancement:`. Titles describe the concrete outcome. A work-mode qualifier such as `Design:` or `Experiment:` may be used only when it materially disambiguates the outcome; it does not replace the native `Task` type.

## Native type handling

Before creating an issue or intentionally changing its native type, resolve the reviewed type against the repository's currently enabled native issue types. A configured name is insufficient if the type is disabled or no longer available; this check applies to organization-managed types such as `Enhancement` as well as the default types.

For that create or type-changing operation, verify that the selected client can set and read the native type. Stop before that operation if the type cannot be applied safely. A body-, title-, or relationship-only correction preserves the existing type and is not blocked by unavailable type-mutation capability; read the type back with the changed fields instead.

After a create or type change, read back the native type and compare it with the reviewed value. If mutation or readback fails after a partial remote write, record the target and observed state, stop dependent writes that rely on the type, and reconcile before resuming. Do not infer the native type from a title prefix.

Every executable work issue must state:

- one primary outcome;
- bounded scope and explicit exclusions;
- a complete acceptance boundary;
- validation and evidence required for acceptance;
- dependencies and independently unblockable states;
- the expected pull-request and review boundary, or why no repository change is expected;
- its owning parent when a parent adds real coordination value, or its milestone/track otherwise;
- relevant authoritative documentation links, using full commit SHAs for immutable historical evidence rather than every living planning reference.

An executable issue should ordinarily complete through one focused pull request and review cycle. Split it when it contains multiple independently useful outcomes with separate acceptance contracts, independently unblockable dependency states, or changes that require independent pull-request or review cycles.

Before declaring pre-release work agent-ready, apply [Pre-release engineering proportionality](../00_project/pre-release-engineering.md). When the plan actually exceeds the default PR, merge, review, mutation, compatibility, or validation budget, record its concise process exception. A routine issue does not need a process-profile section. If an extra stage does not protect a distinct current failure, remove or combine it.

Every resulting child must remain coherent and independently acceptable with its own complete acceptance contract. A dependency state may justify a split, but it never substitutes for an acceptable child outcome. Keep implementation with the tests, contract artifacts, fixtures, and validation required to accept it; separate one of those artifacts only when the separated result is independently useful and acceptable.

A coordination parent may be intentionally broader when it adds a useful dependency graph, blocker classification, or closure view beyond the milestone. It must not retain unbounded executable work. Do not create or maintain a parent merely to duplicate GitHub milestone membership, issue state, or CI links.

Use repository-local parent/sub-issue relationships for decomposed work; use full URLs for cross-repository traceability.

M0 experiments must report observed evidence and unresolved risks. They must not turn an untested architecture option into a project-wide assumption.

## Roadmap and milestone changes

Long-lived milestone scope and exit criteria live in repository docs. Tracker drafts may be prepared while the docs are under review, but dependent implementation begins only after the applicable documentation is merged. Ordinary issue-body corrections may follow the current `main` document path without creating an immutable publication package.

Use a reviewed synchronization manifest only for a bulk milestone, parent/sub-issue, dependency-graph, or multi-record migration whose partial application would create ambiguous planning state. Before the first write in such a migration:

1. verify the governing documentation is reachable from `main`;
2. choose living `main` links or immutable full-SHA evidence links according to the rule below;
3. review one synchronization manifest entry for every proposed structural tracker write;
4. complete native type handling for issues being created or intentionally retyped.

Each bulk synchronization entry records only the fields the migration intends to change:

- the existing target or proposed issue or milestone;
- operation: `create`, `update`, `close`, `move`, or `no change`;
- reviewed title and body draft, or title and description draft for a milestone;
- native issue type when the migration creates, retypes, or structurally depends on it;
- verified native-type mutation and readback path for created or retyped issues;
- expected state;
- owning parent;
- milestone or non-product track;
- native dependencies;
- labels when they carry contractual meaning;
- immutable documentation links when the entry makes a historical claim.

Living issue and milestone guidance may link the current `main` repository path. Use a full commit SHA whose commit is reachable from `main` when the link is evidence for an immutable historical acceptance or external claim. Live issue, pull-request, and milestone URLs remain mutable tracker references. A per-body digest is not required.

After applying writes, read back the fields and relationships actually changed. A bulk migration reads back its complete changed graph; an ordinary body or title update does not require a repository-wide graph snapshot, source/target digest table, or publication-capability proof.

A milestone parent issue may own, when that view is useful:

- the dependency graph;
- the exit-evidence checklist;
- blocking versus non-blocking follow-ups;
- the final closure record.

Every executable issue belongs to its actual milestone or track. Add a parent/sub-issue relationship only when it contributes coordination beyond that membership.

Do not place a non-gating research item, release gate, or later-track task in a milestone merely to keep it visible. Use the milestone that owns its actual completion condition.

## Contract changes

Before refining a contract issue, read [Contract lifecycle](../00_project/contract-lifecycle.md).

For a pre-release contract, also apply [Pre-release engineering proportionality](../00_project/pre-release-engineering.md). Define only the minimum early validation boundary, test the affected producer-consumer path when executable, and defer any release freeze or certification identity to a concrete release boundary.

A pre-release breaking amendment does not automatically require a new artifact version. Its acceptance contract names the specification, producer, consumer, schema or registry, fixture, validator, implementation, and validation surfaces that are actually affected.

Create a separate decision issue when the amendment must choose among materially different semantics. Create a separate experiment issue when executable evidence is needed before the choice can be made. Do not hide an unresolved contract choice inside a production implementation issue.

After a contract is released, any breaking change issue must also own or depend on the new artifact version, compatibility behavior, migration guidance, and old-version disposition.

## Project-boundary changes

An issue that creates, removes, renames, or changes references between C# projects must cite [Project structure](../20_architecture/project-structure.md) and state:

- the production, test, fixture, experiment, tool, or host classification;
- the dependency or authority boundary that requires the project;
- allowed and forbidden project references;
- package and runtime dependencies introduced;
- source migrated from an existing project;
- architecture and integration tests required;
- the active milestone that needs the project now.

Do not create an empty project to reserve a future name. Do not move product logic into a TypeScript Action wrapper to avoid defining the corresponding C# adapter or Core contract.

An Action-host decision belongs to the payload-distribution gate and requires candidate evidence. GitHub mutation logic belongs to the M5 .NET adapter regardless of the eventual wrapper language.
