# Solus Book adoption

ContractScribe uses Solus Book for shared engineering rules and task procedures. Product architecture, contracts, roadmap, commands, and explicit local requirements stay in this repository.

## Source and acquisition

| Field | Adopted value |
| --- | --- |
| Source | [SolusQuest/solus-book](https://github.com/SolusQuest/solus-book) |
| Revision | [`c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67`](https://github.com/SolusQuest/solus-book/commit/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67), verified against source `main` on 2026-10-06 |
| Local source | Git submodule at `docs/shared/solus-book`, configured in [.gitmodules](../../.gitmodules) |
| Initialization | `git submodule update --init --recursive` from the repository root |
| Fresh clone | `git clone --recurse-submodules https://github.com/SolusQuest/contract-scribe.git` |

The parent repository's gitlink selects the source revision. Initialization retrieves that revision; it does not select the latest Book branch. Access to the source repository is required. Check that `git -C docs/shared/solus-book rev-parse HEAD` matches the adopted revision before relying on shared content.

For an authorized update, fetch Book, check out the selected full commit in the submodule, compare affected shared meaning and local requirements, and update this record and the gitlink in the same change. Do not use an implicit latest-revision fallback. Keep edits to canonical shared content in Book's own task and repository.

## Entrypoints and resources

[AGENTS.md](../../AGENTS.md) is the shared repository entrypoint and routes to [local agent context](agent-context.md), shared rules, and [Book task routing](../shared/solus-book/agents/task-routing.md). The redundant harness-specific root entrypoint was removed. Book's own maintenance entrypoint is not adopted as this project's root.

Codex's [repository skill discovery](https://learn.chatgpt.com/docs/build-skills#where-codex-loads-local-skills) uses `.agents/skills`. Five checked-in thin adapters link to the canonical procedure and local context:

| Discoverable adapter | Canonical procedure |
| --- | --- |
| [contract-scribe-design-refinement](../../.agents/skills/contract-scribe-design-refinement/SKILL.md) | [Design refinement](../shared/solus-book/skills/design-refinement/SKILL.md) |
| [contract-scribe-issue-refinement](../../.agents/skills/contract-scribe-issue-refinement/SKILL.md) | [Issue refinement](../shared/solus-book/skills/issue-refinement/SKILL.md) |
| [contract-scribe-issue-publishing](../../.agents/skills/contract-scribe-issue-publishing/SKILL.md) | [Issue publishing](../shared/solus-book/skills/issue-publishing/SKILL.md) |
| [contract-scribe-pr-publishing](../../.agents/skills/contract-scribe-pr-publishing/SKILL.md) | [PR publishing](../shared/solus-book/skills/pr-publishing/SKILL.md) |
| [contract-scribe-test-validation](../../.agents/skills/contract-scribe-test-validation/SKILL.md) | [Test validation](../shared/solus-book/skills/test-validation/SKILL.md) |

The adapters contain no copied procedure. Follow their links to Book, then resolve Book's relative links from the canonical source file, not from the adapter directory. The complete submodule retains all referenced standards and templates. A reader in a nested project directory uses the same repository-root source.

If the source or a required resource is unavailable, follow [Loading failures](../shared/solus-book/agents/context-model.md#loading-failures). Continue independent work with applicable available rules; pause operations that depend on missing or conflicting guidance, and do not claim complete loading.

## Local requirements and semantic deduplication

Shared principles and procedures are maintained in Book. The local pages below contain project values, stronger requirements, and historical interpretation:

| Local owner | Removed shared material now owned in Book | Local requirements retained |
| --- | --- | --- |
| [Conventions](../00_project/conventions.md) | [Content and information rules](../shared/solus-book/standards/conventions.md) | English, branch/title practice, stricter exclusion of private downstream material, reviewed synthetic fixtures, license disposition, product terminology, and implementation boundaries. |
| [Source of truth](../00_project/source-of-truth.md) | [Record ownership, durable decisions, and historical evidence](../shared/solus-book/standards/source-of-truth.md) | Immutable repository-file evidence must use a full commit verified reachable from `main`; local ownership and routing. |
| [Pre-release engineering](../00_project/pre-release-engineering.md) | [Purpose, coherent outcomes, draft replacement, precedent reuse, proportional process, and coupled corrections](../shared/solus-book/standards/pre-release-engineering.md) | One human merge/coherent review budget, implementation-choice latitude, internal-architecture precedent scope and original rationale, one production implementation per capability, exact exception triggers/blocker behavior, early-validation freeze limits, content versus merge identity, experiment retirement, and closed-work handling. |
| [Contract lifecycle](../00_project/contract-lifecycle.md) | [Lifecycle concepts, draft replacement, compatibility obligations, and historical acceptance](../shared/solus-book/standards/contract-lifecycle.md) | Integer version `1`, required increments/rejection, released identifier behavior, concrete transfer provenance, Host Validation restrictions, first-consumable-release gate, allowed product identities, and M0/#70/#75 interpretation. |
| [Issue workflow](../10_workflow/issue-workflow.md) | [Refinement, coherent decomposition, native-field operations, and readback](../shared/solus-book/standards/issue-workflow.md); [publishing procedure](../shared/solus-book/skills/issue-publishing/SKILL.md) | Native type order, mandatory executable acceptance/review fields, actual track membership, useful parent relationships, merge-first structural migration with reviewed manifest fields, and project-boundary evidence. |
| [PR workflow](../10_workflow/pr-workflow.md) | [Reviewable content, publication, evidence, and readiness](../shared/solus-book/standards/pr-workflow.md) | Repository template, tighter no-required-follow-up exception with `None`, issue creation before Ready/merge after scope growth, contract status, local information exclusions, and project/Action reference evidence. |
| [Local test validation](skills/test-validation.md) | [Input validity, evidence, process handles, failure classification, and completion claims](../shared/solus-book/standards/validation.md) | Windows feedback versus required Ubuntu exact-head CI, commands/build invalidation inputs, suite/partition definitions, timing/TRX guidance, owned-process proof, fixture isolation, coverage, and performance-sample dispositions. |
| Product decisions | No product-specific content was extracted. | Project context, security, architecture, machine contracts, release/Action documents, and roadmap remain in their original paths. |

The deduplication pass replaced shared bodies rather than appending references: the three largest general-rule pages (pre-release, lifecycle, issue workflow) went from 5,968 to 2,685 whitespace-delimited words. Local requirements duplicated across those pages now have one owner; for example, lifecycle owns workflow provenance and Host Validation restrictions, while the issue/PR pages route to that rule.

Removed whole documents: the old collaboration-layer, design-refinement, issue-refinement, and PR-publishing pages under `docs/50_ai`. The local test-validation path remains because it owns executable project details. Incoming references and document consumers were checked against the resulting ownership.

Intentional product, contract, privacy, or workflow-authority changes: none. This adoption changes source placement and loading. It adds the pinned submodule, thin discoverable adapters, and acquisition instructions without changing product behavior or historical acceptance.

## Validation and limits

The following checks ran on 2026-10-06 against this adoption's working tree, using native Windows, Codex CLI app-server 0.160.1, Python 3.14.5, and PyYAML 6.0.3:

| Check | Observed result |
| --- | --- |
| Source identity | Remote Book `main`, the parent index gitlink, and the clean detached submodule all matched the full revision recorded above. |
| Acquisition without an existing Book checkout | A temporary consumer containing the same `.gitmodules` and indexed gitlink ran `git submodule update --init --recursive` against the remote and obtained the selected revision. This tests initialization before the consumer change is committed. |
| Native skill discovery | Read-only `skills/list` with `forceReload=true` discovered all five enabled adapters with no local skill errors, both at the repository root and under `src/ContractScribe.Core`. The same check passed in the temporary consumer's root and nested directory. No model turn or remote publication ran. |
| Skill structure and resources | The bundled skill-creator validator passed all five adapters and all five canonical skills. The acquired consumer's 30 shared/adapter Markdown files had 173 readable local references outside code fences, including canonical standards, templates, and local context. |
| Documents and preserved consumers | After deduplication and removal of the redundant harness entrypoint, all 478 local links and heading anchors across 90 Markdown files passed, as did UTF-8/LF/final-newline hygiene and incoming references. All 46 architecture, roadmap, release-policy, release-runbook, and Action-usage documents remained byte-for-byte identical to the starting revision. Local validation command blocks and selection/timing tables were unchanged. The M1 target-observation test and its recorded lifecycle metadata were unchanged; that reader checks historical fixture metadata rather than the current lifecycle body. |
| Semantic walkthrough | Editorial comparison preserved the outcomes for a type-preserving text update without retyping capability, a documentation-first structural migration, a complete no-issue PR versus scope growth, a same-version draft correction versus a real cross-revision transfer, Host Validation without placeholder release machinery, and a coupled current-owner correction without rewriting historical acceptance. This is document review, not an executed product or tracker workflow. |
| Existing Release document check | `python tests/release/check_release_workflow.py` returned `check-release-workflow: ok`. `git diff --check` and the staged equivalent passed. |
| Representative task routing | This adoption's final validation followed the new root context and test-validation adapter through the canonical Book procedure to the local command/environment rules, then checked the affected loading, references, and document consumers. |

Raw local check output stays under ignored `TestResults/book-adoption/`; temporary protocol and consumer fixtures are removed after verification. No maintained validation framework or certification record was added. The cleanup retained the adopted revision, entrypoint paths, and five adapters, so acquisition and native-discovery observations above were reused; document, resource, consumer, and Release checks were refreshed.

These results establish native local discovery, resource readability, and source acquisition for this arrangement. They do not demonstrate automatic skill selection in a new model turn, discovery in other harnesses, another platform, or remote CI. No product build or .NET test suite ran: production source, machine contracts, fixtures, project references, and executable commands were unchanged. Ubuntu exact-head CI remains the required platform conclusion when a later task needs product validation.
