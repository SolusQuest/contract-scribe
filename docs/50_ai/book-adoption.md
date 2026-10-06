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

[AGENTS.md](../../AGENTS.md) is the shared repository entrypoint and routes to [local agent context](agent-context.md), shared rules, and [Book task routing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/agents/task-routing.md). The redundant harness-specific root entrypoint was removed. Book's own maintenance entrypoint is not adopted as this project's root.

Codex's [repository skill discovery](https://learn.chatgpt.com/docs/build-skills#where-codex-loads-local-skills) uses `.agents/skills`. Five checked-in thin adapters link to the canonical procedure and local context:

| Discoverable adapter | Canonical procedure |
| --- | --- |
| [contract-scribe-design-refinement](../../.agents/skills/contract-scribe-design-refinement/SKILL.md) | [Design refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/design-refinement/SKILL.md) |
| [contract-scribe-issue-refinement](../../.agents/skills/contract-scribe-issue-refinement/SKILL.md) | [Issue refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-refinement/SKILL.md) |
| [contract-scribe-issue-publishing](../../.agents/skills/contract-scribe-issue-publishing/SKILL.md) | [Issue publishing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-publishing/SKILL.md) |
| [contract-scribe-pr-publishing](../../.agents/skills/contract-scribe-pr-publishing/SKILL.md) | [PR publishing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/pr-publishing/SKILL.md) |
| [contract-scribe-test-validation](../../.agents/skills/contract-scribe-test-validation/SKILL.md) | [Test validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/test-validation/SKILL.md) |

The adapters contain no copied procedure. Each names its repository-relative local procedure path and provides a pinned canonical browser link. Open the local procedure from the repository root, then resolve Book's relative resources from that source file rather than from the adapter directory. The complete submodule retains all referenced standards and templates; a reader in a nested directory uses the same repository-root source.

GitHub does not traverse descendant blobs inside a parent repository's gitlink. Parent Markdown links therefore target Book's repository at the adopted full commit. For local reading, map `https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/<path>#<section>` to `docs/shared/solus-book/<path>` and locate the named section in that file. Verify the checked-out Book revision first. The browser link and local file select the same canonical content.

If the source or a required resource is unavailable, follow [Loading failures](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/agents/context-model.md#loading-failures). Continue independent work with applicable available rules; pause operations that depend on missing or conflicting guidance, and do not claim complete loading.

## Local requirements and semantic deduplication

Shared principles and procedures are maintained in Book. The local pages below contain project values, stronger requirements, and historical interpretation:

| Local owner | Removed shared material now owned in Book | Local requirements retained |
| --- | --- | --- |
| [Conventions](../00_project/conventions.md) | [Content and information rules](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/conventions.md) | English, branch/title practice, stricter exclusion of private downstream material, reviewed synthetic fixtures, license disposition, product terminology, and implementation boundaries. |
| [Source of truth](../00_project/source-of-truth.md) | [Record ownership, durable decisions, and historical evidence](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/source-of-truth.md) | Immutable repository-file evidence must use a full commit verified reachable from `main`; local ownership and routing. |
| [Pre-release engineering](../00_project/pre-release-engineering.md) | [Purpose, coherent outcomes, draft replacement, precedent reuse, proportional process, and coupled corrections](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md) | One human merge/coherent review budget, implementation-choice latitude, internal-architecture precedent scope and original rationale, one production implementation per capability, exact exception triggers/blocker behavior, early-validation freeze limits, content versus merge identity, experiment retirement, and closed-work handling. |
| [Contract lifecycle](../00_project/contract-lifecycle.md) | [Lifecycle concepts, draft replacement, compatibility obligations, and historical acceptance](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/contract-lifecycle.md) | Integer version `1`, required increments/rejection, released identifier behavior, concrete transfer provenance, Host Validation restrictions, first-consumable-release gate, allowed product identities, and M0/#70/#75 interpretation. |
| [Issue workflow](../10_workflow/issue-workflow.md) | [Refinement, coherent decomposition, native-field operations, and readback](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/issue-workflow.md); [publishing procedure](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-publishing/SKILL.md) | Native type order, mandatory executable acceptance/review fields, actual track membership, useful parent relationships, merge-first structural migration with reviewed manifest fields, and project-boundary evidence. |
| [PR workflow](../10_workflow/pr-workflow.md) | [Reviewable content, publication, evidence, and readiness](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pr-workflow.md) | Repository template, tighter no-required-follow-up exception with `None`, issue creation before Ready/merge after scope growth, contract status, local information exclusions, and project/Action reference evidence. |
| [Local test validation](skills/test-validation.md) | [Input validity, evidence, process handles, failure classification, and completion claims](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/validation.md) | Windows feedback versus required Ubuntu exact-head CI, commands/build invalidation inputs, suite/partition definitions, timing/TRX guidance, owned-process proof, fixture isolation, coverage, and performance-sample dispositions. |
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
| Skill structure and resources | The bundled skill-creator validator passed all five adapters and all five canonical skills. Each adapter's explicit local procedure path and pinned canonical URL select the same readable source; Book's standards, templates, and local context remain accessible. |
| Documents and preserved consumers | Across 90 Markdown files, 427 local links/anchors and 51 pinned Book links mapped to local source files/anchors passed, as did UTF-8/LF/final-newline hygiene and incoming references. All 46 architecture, roadmap, release-policy, release-runbook, and Action-usage documents remained byte-for-byte identical to the starting revision. Local validation command blocks and selection/timing tables were unchanged. The M1 target-observation test and its recorded lifecycle metadata were unchanged; that reader checks historical fixture metadata rather than the current lifecycle body. |
| Browser navigation | The parent-repository descendant-gitlink URL returned 404. Corrected consumer text rendered through GitHub's Markdown API links to the pinned Book repository for a representative standard and skill; both canonical browser targets returned 200 and their corresponding local files were readable. No parent Markdown link descends through the gitlink. |
| Semantic walkthrough | Editorial comparison preserved the outcomes for a type-preserving text update without retyping capability, a documentation-first structural migration, a complete no-issue PR versus scope growth, a same-version draft correction versus a real cross-revision transfer, Host Validation without placeholder release machinery, and a coupled current-owner correction without rewriting historical acceptance. This is document review, not an executed product or tracker workflow. |
| Existing Release document check | `python tests/release/check_release_workflow.py` returned `check-release-workflow: ok`. `git diff --check` and the staged equivalent passed. |
| Representative task routing | This adoption's final validation followed the new root context and test-validation adapter through the canonical Book procedure to the local command/environment rules, then checked the affected loading, references, and document consumers. |

Raw local check output stays under ignored `TestResults/book-adoption/`; temporary protocol and consumer fixtures are removed after verification. No maintained validation framework or certification record was added. The browser-link correction retained the adopted revision and source paths, so acquisition proof was reused; native discovery at the current root/nested directory and document/resource checks were refreshed. Release inputs and product documents were unchanged.

These results establish native local discovery, resource readability, and source acquisition for this arrangement. They do not demonstrate automatic skill selection in a new model turn, discovery in other harnesses, another platform, or remote CI. No product build or .NET test suite ran: production source, machine contracts, fixtures, project references, and executable commands were unchanged. Ubuntu exact-head CI remains the required platform conclusion when a later task needs product validation.
