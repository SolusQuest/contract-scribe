# Documentation Scribe

> **Status:** Current single-target runtime reference with adopted M7 direction. [M7 plan](../90_roadmap/m7-plan.md) owns shared-session/fixed-batch behavior; C1 changes planning only. Current request/result contracts, provider wire behavior and runtime remain unchanged until their code/schema owners update them. Historical M3 evaluation claims remain revision-bound.

## Goal

The Documentation Scribe turns a deterministic audit target, bounded repository evidence, and a style profile into a structured XML-documentation proposal. It is the product's model-assisted writing capability, but it is not a general coding agent.

The Scribe Runtime is self-developed so that tool policy, evidence selection, budgets, output contracts, provider behavior, and failure handling remain part of ContractScribe rather than an implicit dependency on a third-party coding-agent runtime.

The production implementation lives in `ContractScribe.Agent`, depends on Core-owned ports, and does not reference `ContractScribe.Patching` or `ContractScribe.GitHub`. Roslyn-backed tool implementations are injected by the CLI composition root. See [Project structure](project-structure.md).

The Documentation Scribe is the only initial model-assisted agent role. Its implementation is the Scribe Runtime in `ContractScribe.Agent`. Repository entrypoint discovery, nested-instruction applicability, context identity, snapshot storage, and target grouping are deterministic runtime responsibilities. See [Scribe context and prompt economics](scribe-context-and-prompt-economics.md).

## Non-goals

The initial Scribe Runtime does not provide:

- a shell or arbitrary command execution;
- arbitrary file reading outside the repository;
- arbitrary file editing or unified-diff generation;
- Git commit, push, pull-request, issue, or release operations;
- web search;
- MCP or plugin discovery;
- subagents or multi-agent planning;
- a separate model-based context loader or context-curator agent;
- a long-lived conversational assistant;
- automatic merge;
- an assertion that a model response is safe to apply.

## Current runtime shape and M7 direction

```text
audit target
  -> deterministic project-context bootstrap
  -> repository context snapshot + scope overlay
  -> target context-pack builder
  -> bounded Scribe loop
       -> optional semantic context routing
       -> semantic/read tool call
       -> bounded tool result
       -> additional tool call or terminal submission
  -> structured proposal validator
  -> documentation proposal or structured skip
```

The current production request builder selects exactly one eligible documentation target, and the current loop terminates on its structured proposal/skip. Independent current runs may reuse immutable context inputs and read-only indexes, but do not share mutable conversations. Bounded opaque assistant continuation is private to the current attempt and discarded at attempt/run boundaries; it is not durable recovery state.

M7 replaces the per-target session direction with one bounded conversation for the complete fixed selected manifest in a normal Action. Necessary target identities, relationships, instruction/style constraints and validated prior progress are visible at the start; detailed evidence is read lazily through bounded tools and reused by content identity/revision/range. Grouping chooses the manifest and provides relationship hints, rather than feeding groups sequentially. Incremental per-target/small-batch proposals and skips receive host validation/acknowledgement while the conversation continues. Foreign/duplicate targets cannot advance state, and the host independently verifies final coverage. Submission, accepted proposal, patch validation, publication and merge remain distinct.

Compatible unpublished recovery reconstructs accepted results without regeneration and starts a new bounded conversation only for remaining original members. Controlled resource/context stops may safely publish a validated subset and seal it pending review; abrupt unpublished stops restore the original fixed batch. No full conversation/reasoning restoration or automatic compaction across Actions is introduced. Exact multi-target protocol shapes and continuation handling belong to their implementation issues.

## Relationship to audit evidence

The canonical Audit Result and deterministic work plan select documentation targets before Scribe runs. The current request admits one; M7 admits the complete fixed batch. Scribe may submit a proposal or structured insufficient-evidence skip only for an admitted target; it cannot add targets, change classification or policy semantics, or reinterpret an audit outcome.

Audit evidence proves that a bounded audit judgment is justified. Scribe target evidence supports writing useful documentation and may require additional signature, relation, implementation, test, usage, maintained-documentation, or style context. The two evidence sets share identity, repository-relative locator, source-revision, hash, authority, and truncation principles, but they are not the same artifact and need not contain the same material.

Additional evidence is acquired only through the bounded tools below. It may justify proposal content or a skip, but it never grants source-mutation authority. See [Semantic foundation](semantic-foundation.md).

## Context pack

The model input is assembled from three different reuse layers:

1. a repository context snapshot containing accepted repository-wide instructions and project context;
2. a scope overlay containing the nested instructions and selected documents applicable to the target path;
3. a target evidence pack containing symbol-specific facts and excerpts.

The default target evidence pack should satisfy common targets without tool calls. It contains allowlisted, size-bounded evidence such as:

- full symbol identity and signature;
- containing type and namespace;
- declared and effective accessibility;
- generic constraints and nullability;
- attributes relevant to caller-visible behavior;
- parameters, return type, and component identities;
- existing XML documentation;
- interface, base, override, and implementation relations;
- a bounded declaration or body excerpt;
- nearby documentation-style examples;
- repository-relative locators, spans, hashes, and evidence IDs.

Every evidence item identifies its authority, subject, source revision, and truncation state. The model never receives an unlabeled concatenation of repository text.

The deterministic bootstrapper loads the configured entrypoint and applicable nested `AGENTS.md` stack before the Scribe begins. Directions that require semantic judgment, such as selecting the relevant procedure or architecture document, are followed by the same Scribe through bounded read tools. ContractScribe does not recursively treat arbitrary Markdown links as instruction imports.

Repository, scope, and target context have separate identities. Only repository and scope context are eligible for the stable reusable prompt prefix. Target evidence and run-specific state are appended after that prefix.

## Read-only tool set

The initial tool registry is closed:

- `get_symbol_context`
- `get_related_symbols`
- `find_symbol_usages`
- `find_tests_for_symbol`
- `get_symbol_source`
- `read_excerpt`
- `search_text`
- `list_files`
- `submit_documentation_proposal`

Roslyn semantic tools are preferred over text search. `read_excerpt`, `search_text`, and `list_files` are bounded fallbacks for project-context routing, maintained documentation, usage examples, configuration, and non-Roslyn artifacts. They are implemented directly through repository-confined C# services rather than by spawning shell commands or a general coding-agent runtime.

Tools enforce:

- repository-root confinement;
- symlink, junction, and reparse-point escape rejection;
- maximum item count and UTF-8 byte count;
- deterministic ordering and deduplication;
- repository-relative public locators;
- binary and invalid-encoding rejection;
- explicit truncation;
- cancellation and per-call timeouts.

The current terminal submission tool returns a structured proposal or skip and ends that target's loop. M7 separates incremental submission/acknowledgement from final conversation completion; the exact tool/result protocol is updated with its runtime owner. There is no file-edit, state-store or publication tool.

Context traversal additionally enforces route-depth, selected-file, and aggregate-byte budgets; canonical-path deduplication; cycle termination; and source/content identity checks. A file discovered as ordinary evidence never becomes an instruction merely because its text asks the model to read another file or disregard prior rules.

## Evidence authority

The Scribe uses evidence in this general order:

1. explicit public contracts and existing interface/base documentation;
2. signatures, attributes, nullability, constraints, and semantic relations;
3. maintained project documentation and usage examples;
4. tests and stable call sites;
5. implementation body and direct dependencies;
6. identifier-name inference.

Lower-authority evidence cannot silently override higher-authority evidence. Implementation details are not documented as caller-visible promises unless supported by stronger evidence.

When evidence cannot justify a behavioral claim, the Scribe omits the claim or returns an insufficient-evidence skip.

## Proposal contract

The model returns structured content, not C# and not a diff. The initial proposal family is expected to represent:

- target identity and expected source revision;
- summary;
- type-parameter documentation;
- parameter documentation;
- return or value documentation;
- exception documentation when supported by explicit evidence;
- remarks when permitted by the style profile;
- selected `<inheritdoc/>` behavior;
- evidence references for material claims;
- a structured skip reason when no safe proposal is possible;
- provider, model, prompt, tool-policy, and style-profile provenance in a separate run envelope.

The exact shape is owned by the M3 contract issue. Unknown fields, missing required evidence, dangling evidence IDs, unexpected parameter names, unsupported XML structures, and invalid Unicode fail closed.

## Style profile

Style is a versioned project input rather than an implicit prompt convention. A style profile may define:

- output language;
- sentence voice and terminology;
- tag ordering;
- summary and remarks length limits;
- parameter, type-parameter, return, value, and exception policy;
- `<inheritdoc/>` policy;
- allowlisted repository examples;
- forbidden phrases and unsupported claim types.

Changing the style profile changes the work-plan identity for unprocessed targets. It does not silently rewrite accepted documentation.

## Provider abstraction

The initial provider surface is the common OpenAI-compatible request, assistant tool-call, tool-result, and terminal-response shape used by the selected DeepSeek and MiMo validation targets.

DeepSeek is the primary development, quality, latency, and economic-evaluation provider. MiMo is the compatibility-validation provider. This is the selected M3 validation direction, not a current provider-support claim.

Before implementation claims compatibility, an M3 provider-validation issue must freeze:

- an executable request and tool-call corpus;
- expected observations for each provider;
- normalization rules, including usage and cache observations when reported;
- stable failure and unsupported-capability classifications;
- bounded evidence required for any compatibility or support statement.

The request and tool-call path remains shared unless executable evidence demonstrates a real protocol difference. A configuration-bound request profile selects thinking, reasoning effort, tool choice, continuation policy, and one output-token field without provider-name branching. For a nonterminal tool response, exact assistant `content` and optional `reasoning_content` form one bounded opaque continuation envelope per response round. It is replayed only on that round's reconstructed assistant message within the same attempt and never interpreted as evidence. Usage response shapes are expected normalization inputs, but this baseline does not assert that usage accounting is the only provider divergence. Supported content-free observations are mapped into ContractScribe-owned metrics, and every newly observed difference is recorded and reviewed before it creates a provider-specific branch or product-contract claim. Fallback between providers or models is explicit and recorded.

ContractScribe owns normalized runtime behavior for:

- messages and structured tool definitions;
- tool calls and tool results;
- structured terminal output;
- usage, cache, and cost reporting;
- cancellation, timeout, and retry classification;
- provider errors and unsupported capabilities.

A deterministic test runtime is mandatory. M3 requires live, synthetic evaluation against DeepSeek and compatibility validation against MiMo. The selected provider/model configuration is recorded in run provenance.

The initial implementation does not attempt a complete provider compatibility matrix or require separate assemblies per provider. It uses the selected direct HTTP/JSON transport. That transport may not reorder or obscure product messages and tools, drop opaque continuation required for the current attempt, expose continuation outside its private wire/runtime boundary, insert unstable prefix content, or own the product contracts.

### Provider-response diagnostics

Normal product and evaluation execution never retains a raw provider response. The shared production response codec may optionally emit one authoritative closed disposition for each bounded HTTP 200 JSON body that reaches it. The disposition is selected by the same first production check that accepts or rejects the response; Evaluation does not parse the response again. A response is not recorded as accepted until the codec has constructed every normalized product object. Request preparation, transport and HTTP-status failures, response metadata/framing failures, and bounded-read failures have no codec-disposition row and continue to use the existing closed failure taxonomy.

The only raw-response exception is an explicit, Linux x64-only, private safety-gate diagnostic mode. The architecture restriction keeps the native file-identity ABI closed and fail-safe. The mode writes the exact already-bounded HTTP 200 JSON bytes into a newly created operator-owned directory below the physical operating-system temporary root. It never captures requests, headers, credentials, prompts, transcripts, tool arguments, continuation, non-success bodies, encoded or non-JSON bodies, oversized or unreadable bodies, or companion metadata. The capability retains a no-follow directory handle and physical identity, rejects checkout/corpus/repository/output overlap and pre-existing targets, verifies owner-only directory and file modes (`0700` and `0600`) before data is written, and uses exclusive temporary creation plus atomic no-overwrite publication. Identity replacement, permission failure, collision, write failure, and incomplete cancellation fail closed without another provider request and remove the incomplete temporary entry.

The private bytes are temporary diagnostic material, not fixtures, report data, correctness evidence, compatibility evidence, or support evidence. They must not enter source control, CI artifacts, logs, GitHub, Relay, reports, resume/cache state, or publication. The operator reduces them locally to the closed codec disposition and Core validation result, immediately deletes the entire capture directory, and removes any crash residue before another launch. A diagnostic or capture failure is a local `evaluation.diagnostics.failed` or `evaluation.capture.failed`, never a provider failure; any already-created closed rows remain available to the partial case report.

## Prompt-prefix economics

Prompt-prefix reuse is a required economic property of the runtime. Compatible requests are constructed in this order:

```text
system and safety protocol
+ closed tool definitions
+ documentation-scribe protocol
+ repository context snapshot
+ scope context overlay
---------------- reusable-prefix boundary ----------------
+ target evidence and current attempt state
+ subsequent tool calls and results
```

Message, tool, schema, document, and evidence-label ordering before the boundary is deterministic. Timestamps, run IDs, temporary paths, random identifiers, target symbols, cursors, remaining budgets, and prior target conversations are forbidden from that prefix.

The runtime computes a local cacheable-prefix identity from the Scribe protocol, tool registry, repository context, scope context, every other prefix-resident input including a style profile when present, and compatible provider/model configuration. The identity covers the exact normalized logical prefix rather than only a subset of its named inputs. Provider cache hits are observed but never trusted as the sole proof that two requests had the same prefix. A cache miss does not change correctness; it consumes uncached-input and cost budgets and remains visible in evaluation.

M7 reuses bounded common context within one selected conversation while keeping instruction/style/evidence scopes explicit. Cache hits are measured and never authorize target/state transitions. No parent-agent fork or cache-driven concurrency/warm-up stage is required. See [Scribe context and prompt economics](scribe-context-and-prompt-economics.md).

## Budgets and termination

[M7 resource defaults](../90_roadmap/m7-plan.md#shared-resources-and-initial-defaults) adopt shared per-Action ceilings across all selected targets, tools, requests and retries, with optional/default-unlimited campaign aggregate caps. Zero is not an unlimited sentinel. These defaults are not implemented by C1; current runtime/configuration contracts retain their actual ceilings until the owning changes land. PR-size business quotas are removed by M7 while necessary parser/transport/context/patch safety bounds remain.

Every run has explicit limits for:

- tool rounds;
- tool calls by kind;
- evidence items and bytes;
- model requests;
- input and output tokens;
- uncached-input consumption when reported;
- estimated cost when available;
- wall-clock duration;
- attempts per target.

Budget exhaustion never converts an invalid or unfinished run into an unverified proposal. M7 preserves accepted incremental results, permits safe partial publication, and reports remaining/skipped/failed work explicitly. Transient errors and invalid proposals get finite retry/repair; insufficient evidence can skip without automatically looping on the same snapshot. No automatic context compaction extends an exhausted conversation.

Cached and uncached input, output, reasoning, and total-token fields are retained as observations when the selected provider reports them. Post-request usage may stop subsequent attempts even when the completed request could not be rejected in advance.

M7 per-invocation observations distinguish new work from reconstructed historical results and known actual usage from unknowns, in-flight exposure and conservative charges. Reasoning is an output subset; missing usage is not zero. Checkpoints may retain only bounded host-validated structured generated documentation and necessary recovery facts, including unpublished/unmerged proposals, never source-evidence bodies, raw provider responses, transcripts/reasoning, secrets or candidate bytes. Scribe itself receives no persistence authority.

## Failure taxonomy

At minimum, distinguish:

- insufficient evidence;
- unsupported target;
- invalid tool request;
- tool result unavailable;
- provider unavailable or rate-limited;
- timeout or cancellation;
- invalid structured output;
- evidence-reference mismatch;
- style-contract violation;
- retry budget exhausted;
- internal bounded failure.

Human-readable provider text is diagnostic only. Stable product behavior is expressed through ContractScribe-owned codes.

## Evaluation

M3 validation includes:

- deterministic fake-runtime fixtures;
- adversarial prompt-injection content in source, comments, tests, and documentation;
- malformed and over-budget tool calls;
- structured-output repair and terminal failure;
- evidence-binding and unsupported-claim checks;
- the frozen provider-validation request corpus and expected observations;
- DeepSeek primary and MiMo compatibility synthetic runs against that corpus;
- normalization fixtures for every evidenced DeepSeek and MiMo response shape, including usage and cache observations when reported;
- bounded compatibility evidence and stable provider failure classification;
- stable cacheable-prefix identity across independent Scribe runs;
- context-route and nested-instruction fixtures;
- proposal acceptance rate after deterministic validation;
- patch-engine acceptance rate;
- unsupported or hallucinated claim rate;
- total and uncached input, cache reuse, output, cost, latency, and tool-call observations;
- sensitive-data and publication-boundary scans of requests, results, diagnostics, and evidence.

The checked-in validation corpus may contain reviewed synthetic system and Scribe protocol templates, canonical ordered-message and tool-call vectors, adversarial prompt-injection strings, and minimized synthetic provider-response shapes. A fixed credential-free reasoning marker may appear only in a synthetic inbound response and its exact outbound replay assertion. Those artifacts are stable, commit-pinned test inputs, not captured live conversations. The corpus excludes private repository content, live-run prompts, complete tool transcripts, captured live assistant continuation, hidden reasoning, credentials, and raw provider responses. Live evaluation publishes only bounded provenance, closed continuation observations, field-shape observations, metrics, and sanitized outcomes needed to justify compatibility claims.

The fake runtime proves orchestration mechanics. Real-provider evaluation determines which request, tool, response, normalization, and failure paths are evidenced; claims remain limited to that executed corpus. It also tests whether the product can produce useful documentation under the same bounded-output contract and whether its request layout has measurable economic behavior. Live provider credentials remain explicit opt-in and are not required by ordinary CI.
