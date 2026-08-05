# Issue workflow

Use the native GitHub issue type as the authoritative type metadata. Apply these rules in order and use the first match:

1. `Bug` for an unexpected problem, regression, or behavior that violates the current contract. This takes precedence even when the correction also improves existing behavior.
2. `Feature` for a net-new user- or consumer-visible capability, including a new external option or operation on existing functionality.
3. `Enhancement` for an intentional improvement to existing functionality, behavior, or workflow that neither corrects a contract violation or regression nor introduces a net-new external capability.
4. `Task` for all remaining bounded decision, design, documentation, contract, experiment, validation, implementation, or coordination work.

Choose the type from the issue's primary outcome. Do not repeat a native type as a title prefix such as `Task:`, `Bug:`, `Feature:`, or `Enhancement:`. Titles describe the concrete outcome. A work-mode qualifier such as `Design:` or `Experiment:` may be used only when it materially disambiguates the outcome; it does not replace the native `Task` type.

## Native type publication gate

Before any remote issue mutation, resolve the reviewed type against the repository's currently enabled native issue types. A configured name is insufficient if the type is disabled or no longer available; this check applies to organization-managed types such as `Enhancement` as well as the default types.

Prove before the first mutation that the selected publishing client sequence can set or correct the resolved type and read the remote native type field back. The sequence may include a separate follow-up client only when its mutation and readback capabilities have already been established. Stop before creating or updating an issue if the complete sequence is unavailable.

After a mutation, read back the native type and compare it with the reviewed value. If mutation or readback fails after a partial remote write, mark the synchronization failed, record the target and observed state, stop dependent writes, and reconcile the issue to the reviewed manifest or perform explicit cleanup before resuming. Do not treat a partially written or unverified issue as published.

Every executable work issue must state:

- one primary outcome;
- bounded scope and explicit exclusions;
- a complete acceptance boundary;
- validation and evidence required for acceptance;
- dependencies and independently unblockable states;
- the expected pull-request and review boundary, or why no repository change is expected;
- its owning parent or why it is an external dependency;
- relevant authoritative documentation links, using full commit SHAs in tracker bodies after the referenced baseline has merged.

An executable issue should ordinarily complete through one focused pull request and review cycle. Split it when it contains multiple independently useful outcomes with separate acceptance contracts, independently unblockable dependency states, or changes that require independent pull-request or review cycles.

Before declaring pre-release work agent-ready, apply [Pre-release engineering proportionality](../00_project/pre-release-engineering.md). Complete its process-complexity checkpoint whenever any trigger in that rule applies; do not narrow or extend the canonical trigger set in an issue. Record the complete process profile in the issue. If an extra stage does not protect a distinct current failure and lacks evidence against a simpler reversible alternative, remove or combine it.

Every resulting child must remain coherent and independently acceptable with its own complete acceptance contract. A dependency state may justify a split, but it never substitutes for an acceptable child outcome. Keep implementation with the tests, contract artifacts, fixtures, and validation required to accept it; separate one of those artifacts only when the separated result is independently useful and acceptable.

A coordination parent may be intentionally broader because it owns a dependency graph, blocker classification, or closure evidence. It must not retain unbounded executable work. Delegate executable outcomes to complete child issues and make each parent/child relationship explicit.

Use repository-local parent/sub-issue relationships for decomposed work; use full URLs for cross-repository traceability.

M0 experiments must report observed evidence and unresolved risks. They must not turn an untested architecture option into a project-wide assumption.

## Roadmap and milestone changes

Long-lived milestone scope and exit criteria must land in repository docs before GitHub milestone descriptions or issue graphs are treated as authoritative. Tracker drafts may be prepared locally while the docs are under review, but tracker synchronization begins only after the complete documentation baseline has merged.

Before the first tracker write:

1. record the merged baseline commit and verify that it is reachable from `main`;
2. construct authoritative repository-file links with that full commit SHA;
3. review one synchronization manifest entry for every proposed tracker write;
4. complete the native type publication gate for every proposed issue write.

Each synchronization entry records:

- the existing target or proposed issue or milestone;
- operation: `create`, `update`, `close`, `move`, or `no change`;
- reviewed title and body draft, or title and description draft for a milestone;
- native issue type for every issue entry;
- verified native-type mutation and readback path for every issue entry;
- expected state;
- owning parent;
- milestone or non-product track;
- native dependencies;
- labels when they carry contractual meaning;
- authoritative full-commit-SHA documentation links.

Repository-file links in issues and milestones identify immutable semantics and must use a full commit SHA whose commit is reachable from `main`. Live issue, pull-request, and milestone URLs are mutable tracker references and are recorded separately. A per-body digest is not required; the reviewed synchronization manifest is the comparison source.

After applying the writes, read back the rendered remote state and compare the title, body, native issue type, state, milestone, parent/sub-issue relationships, dependencies, contractual labels, and pinned links with the reviewed manifest. A title prefix or body field is not evidence that the native type was set.

A milestone parent issue owns:

- the dependency graph;
- the exit-evidence checklist;
- blocking versus non-blocking follow-ups;
- the final closure record.

Every executable issue counted by a milestone must be owned by its milestone parent or identified as an external dependency with an owner, milestone or track, and rationale.

Do not place a non-gating research item, release gate, or later-track task in a milestone merely to keep it visible. Use the milestone that owns its actual completion condition.

## Contract changes

Before refining a contract issue, read [Contract lifecycle](../00_project/contract-lifecycle.md).

For a pre-release contract, also apply [Pre-release engineering proportionality](../00_project/pre-release-engineering.md). Separate early validation design from final producer-consumer freeze, and require an end-to-end satisfiability sweep before freezing implementation-level schemas, fixtures, adapters, or certification identities.

A pre-release breaking amendment does not automatically require a new artifact version. It does require a coordinated acceptance contract that names every affected specification, schema, registry, fixture, oracle, implementation, and validation gate.

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
