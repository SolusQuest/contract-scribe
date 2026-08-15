# Scribe context and prompt economics

> **Status:** The M3-X1 deterministic context-bootstrap decision in this document is accepted and executable. Provider transport, model/tool-loop behavior, durable manifests or snapshots, prompt-prefix construction, campaign scheduling, persistence, and economic evaluation remain candidate design until their owning M3 or M4 work accepts them.

## Decision

ContractScribe uses one model-assisted agent role: the Documentation Scribe. The accepted M3-X1 bootstrap and content identity are deterministic runtime responsibilities, not separate agents. Snapshot storage, target grouping, and campaign scheduling are later candidate responsibilities rather than products or contracts introduced by M3-X1.

A Scribe run may use bounded read-only tools to follow repository-defined context routes before it submits a documentation proposal. Multiple Scribe runs share immutable repository and scope context snapshots. They do not share hidden reasoning, mutable conversation history, or provider-owned session state.

The initial provider transport is constrained to an OpenAI-compatible request and tool-call protocol. DeepSeek is the primary implementation and economic-validation target; MiMo is the compatibility-validation target. This is an initial M3 validation baseline, not a permanent provider exclusivity promise or a requirement to design a general multi-provider framework.

Prompt-prefix reuse and bounded uncached input are required architecture properties. Provider caching remains an optimization rather than a correctness dependency: a cache miss may make a run more expensive, but it must not change the selected evidence, proposal semantics, validation result, or campaign state transition.

The exact request endpoint, SDK or HTTP client, model identifiers, `Microsoft.Extensions.AI` usage, provider-visible contract shapes, and calibrated model-run budget values remain later M3 implementation decisions. They must preserve the accepted bootstrap boundary below.

### Accepted M3-X1 context-bootstrap decision

M3-X1 consumes one exact, current `ClassifiedRepositorySession` produced by M1 and creates one immutable, in-memory `DocumentationScribeLoadedContext`. It does not create a durable repository snapshot, context manifest, cache, ledger, resume identity, migration reader, compatibility alias, provider request, or provider-visible operation.

The producer order is fixed:

1. caller selection supplies only the repository/session correlation, selected classified symbol and declaration anchor, optional configured entrypoint, and bounded bootstrap limits;
2. deterministic bootstrap validates the complete loaded/classified-session correlation before any file open or read;
3. bootstrap resolves one permitted declaration directory, performs confined stable reads, and publishes typed instruction, project, route, omission, and source-evidence facts plus a private session-local cursor capability;
4. later M3-X2 and M3-X3 producers may add evidence through the accepted M3-R1 typed ports;
5. one final Documentation Scribe request is assembled and bound back to the complete required X1 instruction set without another repository read.

The bootstrap accepts an explicitly configured repository-relative agent entrypoint as the sole root entrypoint. Root `AGENTS.md` is considered only when configuration supplies no alternative. Applicable nested `AGENTS.md` files are then loaded in deterministic root-to-leaf order along the one accepted declaration scope. A configured-entrypoint failure never falls back to root `AGENTS.md`.

Scope acceptance is based on uniqueness, not on a declaration category. All authoritative repository declaration references for the selected symbol are normalized. They are accepted when they resolve to exactly one permitted repository directory and the caller's selected path/span is an exact declaration anchor. Partial, linked, generated, mixed, aliasing, or multi-declaration inputs that produce zero or multiple permitted scopes fail closed; no project root, first declaration, or lexical directory is selected heuristically.

Before and after bounded reads, M3-X1 checks repository confinement, directory and file physical identity, link count, replacement, and publication state. It opens regular files without following links, decodes strict UTF-8, enforces file/count/depth/total-byte/elapsed limits, and distinguishes terminal correlation, unsafe-object, stale, encoding, identity-collision, cancellation, timeout, and budget outcomes from an accepted incomplete result. Caller cancellation is terminal and never publishes partial facts or a cursor.

Core owns inert facts and validation only. Content-bearing facts are explicitly role- and authority-labeled and source-committed. Provider observation is telemetry, never evidence authority. Deterministic content and evidence identities exclude the repository session token and cursor; a separate correlation value binds facts to the exact loaded session. The Roslyn runtime privately owns the HMAC cursor key and loaded-session capability. A cursor binds the tool kind, normalized request, repository and compilation session, selected symbol, ordering, page size, next position, and source commitments, and is rejected after tampering, cross-query/tool reuse, or a fresh bootstrap even when repository bytes are unchanged.

Diagnostics, failures, exceptions, logs, and object dumps expose stable product codes and bounded counts or repository-relative identities only. Authorized instruction or evidence content is available only through successful typed payload fields and is not copied into those diagnostic channels. Unknown exceptions map to one bounded internal-failure code.

## Goals

The context and runtime design must:

- let a repository define durable writing context through `AGENTS.md` or an explicitly configured entrypoint;
- apply nested directory-specific instructions to the targets they govern;
- allow semantic routing such as "read the relevant procedure" without introducing a second model-agent role;
- reuse common project context across many targets without carrying target-specific conversation history forward;
- keep repository text from expanding the Scribe's authority;
- preserve exact, stable prompt prefixes across compatible Scribe runs;
- make uncached input, cache reuse, tool calls, latency, and cost observable;
- remain correct when provider caching is unavailable, expired, or missed;
- support deterministic replay, bounded failure, and sanitized bounded provenance.

## Runtime components

### Project-context bootstrapper

The bootstrapper is deterministic and does not call a model. For a repository snapshot and target path, it:

- resolves the repository root and explicit analysis input;
- selects the configured agent entrypoint or the supported default entrypoint;
- discovers applicable nested `AGENTS.md` files from the repository root to the target directory;
- orders instructions from broadest to nearest scope;
- canonicalizes repository-relative paths;
- rejects symlink, junction, reparse-point, or traversal escapes;
- computes deterministic source commitments, instruction/evidence identities, and one semantic content identity distinct from session correlation;
- enforces initial file-count, byte, and depth bounds;
- creates one immutable in-memory loaded-session context.

The nearest applicable repository instruction may refine a broader repository instruction within its repository-controlled scope. It cannot override ContractScribe's system policy, tool registry, output contract, budgets, provider selection, or security boundary.

The bootstrapper handles applicability that can be derived from paths and explicit configuration. It does not attempt to interpret arbitrary prose or decide which of several semantically described project documents is relevant.

### Project-context session

The accepted M3-X1 context is a sealed runtime capability over the exact loaded/classified session. Its public surface contains immutable Core facts; it grants no ambient filesystem, shell, network, service-locator, writer, provider, Git, GitHub, persistence, or campaign authority. The Roslyn-owned capability retains the current session binding and private cursor authority needed by later typed ports. Disposal or session substitution makes request binding and cursor use stale.

This context is not a provider conversation and is not durable GitHub ledger content. Any future reconstructible snapshot, campaign state, prompt-prefix identity, usage observation, or cache artifact requires its own accepted consumer and contract.

### Documentation Scribe

The Documentation Scribe is the only initial model-assisted agent role. Within one bounded run it may:

1. receive bootstrapped repository and scope context;
2. use read-only tools to complete semantic context routing;
3. gather target-specific semantic evidence;
4. submit one structured documentation proposal or structured skip.

Context discovery is a phase of the Scribe loop, not a handoff between two model-agent roles. The Scribe has no subagent or fork tool.

### Campaign runner

The campaign runner groups targets by compatible context identity, constructs requests in stable order, applies provider and cost budgets, and starts independent short-lived Scribe runs. It may warm and then process a context group together, but it does not depend on a long-lived model conversation.

## Context layers

Context is divided by reuse and authority rather than concatenated into one unlabeled prompt.

### Candidate later repository context snapshot

This subsection describes possible later prompt/campaign reuse. It is not an M3-X1 data product or durable format.

Repository context is common to targets across the repository and may contain:

- the root agent entrypoint;
- shared project conventions;
- public API terminology;
- repository-wide documentation style;
- explicitly routed architecture or usage documents;
- a manifest of source paths, hashes, roles, and truncation state.

Its identity includes at least:

```text
repository identity
+ base commit
+ explicit input identity
+ root entrypoint identity
+ included document identities
+ repository-context policy
```

### Candidate later scope context overlay

A scope overlay applies to targets governed by the same directory-specific instructions or project context. It may contain:

- nested `AGENTS.md` files;
- project- or directory-specific conventions;
- a selected procedure or style guide;
- scope-specific documentation examples;
- the ordered instruction route that selected them.

Targets may share an overlay only when their applicable instruction stack and included document identities are equal. A repository with several projects or nested instruction files normally produces several context groups.

### Candidate later target evidence pack

Target evidence is specific to one symbol or a deliberately small batch sharing the same containing-type context. It contains:

- symbol, signature, accessibility, and containing-type facts;
- documentation observation and inheritance relations;
- bounded declaration or body excerpts;
- usages, tests, and related symbols selected for that target;
- the current attempt and target-specific budget state.

Target evidence never becomes part of the repository-wide reusable prefix merely because one Scribe run requested it. Repeated evidence may be promoted only through an explicit, deterministic context-policy change.

### Candidate later discovery reuse and snapshot promotion

The initial bootstrap may be sufficient to build the complete repository or scope context. When an entrypoint contains a semantic route, the first Scribe run in a context scope may need to select and read an additional procedure or project document.

The runtime may reuse that discovery for later targets only when:

- the selected file was reached from an accepted repository instruction;
- its role is repository- or scope-level rather than target evidence;
- the complete repository-relative path and content identity are recorded;
- the route and snapshot remain within all context budgets;
- the resulting scope identity is frozen before later requests are assembled.

Reuse stores the exact accepted document content or reconstructible identity, not a model-written summary or hidden conclusion. Target-specific usages, tests, source excerpts, and inference remain in the dynamic target evidence pack.

The first discovery-bearing Scribe request and later frozen-prefix requests may therefore have different context identities. The runtime must not claim that the earlier request warmed the final prefix unless the locally computed prefix identity is actually equal. For a large context group, the campaign may choose a bounded context-preparation phase using the same Scribe Runtime when its additional request is justified by projected reuse. That is an economic planning choice, not a second agent role or a correctness requirement.

## Context entrypoints and routing

### Supported entrypoints

The initial design recognizes:

- an explicitly configured repository-relative agent entrypoint;
- a root `AGENTS.md` when no different entrypoint is configured;
- nested `AGENTS.md` files applicable to the target path.

Platform-specific files such as `CLAUDE.md` are not discovered automatically as additional authorities. A caller may choose one as an explicit entrypoint, or an accepted repository entrypoint may route to it.

### Deterministic and semantic routing

Context routing has two parts:

1. **Deterministic bootstrap** resolves configured paths, the applicable nested instruction stack, direct file identity, ordering, and safety.
2. **Scribe-directed traversal** handles semantic directions such as "read the relevant procedure" by using bounded read tools.

ContractScribe does not recursively follow every Markdown link and does not interpret every source-code path as an instruction import. A directly routed file remains labeled with the role assigned by the route that selected it.

A route record contains enough information to explain traversal without preserving hidden reasoning:

```text
from evidence or instruction ID
+ to repository-relative path
+ route role
+ deterministic or Scribe-selected
+ source revision and content hash
+ depth and truncation state
```

Repeated canonical paths are deduplicated. Cycles are diagnosed and terminated. Maximum route depth, selected files, total UTF-8 bytes, per-file bytes, and tool calls are explicit budgets. Missing, invalid, escaped, binary, cyclic, or over-budget routes produce bounded diagnostics and may cause a structured insufficient-context skip.

## Instructions, context, and evidence

Repository content is never system authority. ContractScribe distinguishes:

- **system policy**: immutable product tool, budget, output, and security rules;
- **run policy**: explicit caller-owned configuration accepted before the model run;
- **repository instruction**: writing and routing guidance from an accepted repository entrypoint;
- **project documentation**: maintained evidence about public behavior and terminology;
- **source evidence**: symbols, tests, call sites, comments, and implementation excerpts.

Repository instructions may select terminology, relevant documents, examples, and style within the configured task. They cannot:

- add or replace tools;
- request a shell, arbitrary edit, network, environment, secret, or GitHub access;
- increase budgets or disable validation;
- change the proposal schema or patch boundary;
- promote arbitrary source comments into instructions;
- require disclosure of prompts, credentials, private paths, or complete tool results.

A file discovered as source evidence does not become an instruction merely because its content tells the model to read another file or ignore prior rules. Lower-authority content cannot silently override higher-authority evidence about caller-visible behavior.

## Read-only tools

The provider-visible names remain product-owned rather than shell-shaped:

- `read_excerpt` is the bounded equivalent of file read;
- `list_files` is the bounded equivalent of glob discovery;
- `search_text` is the bounded equivalent of repository text search.

These tools are implemented directly in C# through repository-confined services. They do not spawn `grep`, `rg`, a shell, or an external coding-agent runtime.

`read_excerpt` may return a complete small text file or a requested range from a larger file. `list_files` returns stable, paged metadata rather than file contents. `search_text` uses explicit path scopes and bounded, paged matches; literal search is the safe initial default. Every result reports repository-relative paths, source/content identity, limits, and truncation.

Roslyn semantic tools remain preferred for C# symbols, relationships, usages, and tests. Text tools exist for context routing, maintained documentation, configuration, examples, and non-Roslyn artifacts.

## Candidate shared context without shared conversation

The following request-sharing model belongs to later prompt and campaign composition. M3-X1 establishes only the deterministic facts and semantic content identity that such work may consume.

Independent Scribe runs share immutable context artifacts and tool backends:

```text
RepositoryContextSnapshot
  + ScopeContextOverlay
       -> Scribe run A + target evidence A
       -> Scribe run B + target evidence B
       -> Scribe run C + target evidence C
```

They do not share:

- hidden model reasoning;
- a mutable parent conversation;
- target-specific tool-call history;
- another target's proposal;
- provider session identifiers as correctness state.

This is logically similar to forking from a common context, but the fork is implemented by deterministic request construction. It does not require a provider-specific conversation-fork feature or a parent/subagent protocol.

A single ever-growing Scribe conversation is not the default because it serializes unrelated targets, prevents safe parallelism, expands the prompt with prior target history, complicates retry and resume, and risks cross-target contamination.

## Candidate cacheable prompt layout

Every compatible request is assembled in this order:

```text
system and safety protocol
+ closed tool definitions
+ documentation-scribe protocol
+ repository context snapshot
+ scope context overlay
---------------- reusable-prefix boundary ----------------
+ target evidence pack
+ target request and attempt state
+ subsequent tool calls and results
```

Cache matching is provider-owned, but ContractScribe owns the stability of the logical request prefix. Stable-prefix construction requires:

- deterministic message and content-block ordering;
- deterministic tool ordering, names, descriptions, and schemas;
- normalized line endings and text encoding;
- stable repository-relative locators;
- stable ordering of context documents and evidence labels;
- no timestamps, run IDs, attempt IDs, temporary paths, random IDs, current cursor, or remaining budget in the reusable prefix;
- no target-specific symbol data before the reusable-prefix boundary;
- no regenerated prose summary whose wording may vary between runs;
- no silent tool-registry or provider/model change within one context group.

ContractScribe records identities such as:

```text
Scribe protocol hash
+ tool-registry hash
+ repository-context hash
+ scope-context hash
+ conditional prefix-resident input hashes, including style-profile hash when present
+ cacheable-prefix hash
```

The exact hash composition is provisional until M3, but the cacheable-prefix identity must cover the exact normalized logical prefix, including every conditional prefix-resident input, and must be computable locally without asking the provider whether two requests were equivalent.

Provider caching does not expand the model context window and does not justify sending unrelated project documents. The default pack stays bounded; additional documents remain available through read tools.

## Provider direction

### Initial compatibility surface

The initial runtime targets the common OpenAI-compatible request, assistant tool-call, tool-result, and terminal-response shape exercised by DeepSeek and MiMo.

- DeepSeek is the primary development, quality, latency, and economic-evaluation provider.
- MiMo validates that the runtime is not accidentally dependent on DeepSeek-only response behavior.
- Provider or model fallback is explicit and recorded. It is never silent.

An M3 provider-validation issue must define the executable request corpus, expected observations, normalization rules, failure taxonomy, and evidence needed before ContractScribe makes a compatibility or support claim. Provider-reported response fields must be preserved for bounded diagnostics, and evidenced totals, usage, and cache observations are mapped into ContractScribe-owned run metrics.

The request construction, assistant message, tool-call, tool-result, retry, and terminal-response path remains shared unless executable validation demonstrates a real protocol difference. Usage response shapes are candidate normalization inputs rather than proof that usage accounting is the only divergence. ContractScribe does not create speculative provider branches, but it also does not describe unexecuted behavior as equal. Every observed difference must be recorded as compatibility evidence and reviewed before it changes the product contract.

This direction does not require one class hierarchy or assembly per provider. A separate provider implementation or project is justified only if transport, dependency, or compatibility evidence crosses the split thresholds in [Project structure](project-structure.md).

### Transport-library boundary

Using `Microsoft.Extensions.AI`, an OpenAI-compatible SDK, or direct HTTP remains an implementation choice. The selected transport must:

- preserve ordered messages, tools, schemas, and tool-call identities;
- expose the provider response and usage fields required by the frozen validation corpus and accepted economic-accounting rules;
- permit bounded cancellation, timeout, and retry classification;
- allow request-shape fixtures and fake HTTP validation;
- avoid inserting unstable or undisclosed prompt content;
- keep provider secrets out of logs and public artifacts.

An abstraction library is not allowed to become the source of product tool policy, proposal contracts, context identity, retry semantics, or cache correctness.

## Economic constraints

Scribe economics are a product property because ContractScribe may process many similar targets in one repository. The runtime must bound and observe:

- total and uncached input tokens;
- cached input tokens when the provider reports them;
- output and reasoning tokens when reported;
- provider requests and tool rounds;
- context discovery calls;
- accepted and rejected proposals per request;
- latency and wall-clock time;
- configured or estimated cost;
- cost per accepted documentation block.

Targets are grouped by:

```text
provider and model configuration
+ Scribe protocol identity
+ tool-registry identity
+ repository-context identity
+ scope-context identity
```

The scheduler should process a group close together so the provider has an opportunity to reuse its prefix. It may run one request before expanding concurrency when a provider requires a cache warm-up, but no correctness transition depends on the warm-up succeeding.

Provider cache hits are not guaranteed and must not be a hard success condition. The enforceable requirements are:

- compatible requests have the same locally computed cacheable-prefix identity;
- dynamic target data occurs only after the reusable prefix;
- cache and uncached usage are captured when available;
- cache misses are observable;
- a configurable uncached-input and cost ceiling can stop additional model work;
- economic regressions appear in M3 evaluation evidence.

Pricing is time-dependent provider configuration, not a hard-coded product constant. A run records the provider/model, applicable pricing or cost-policy identity, and observed usage. Caller-owned scheduling may target an advantageous window, but the product does not promise a particular wall-clock price.

## Candidate resume and invalidation

Resume and durable invalidation are M4 concerns. The text below is retained as design direction and is not implemented or frozen by M3-X1.

Campaign resume reconstructs context from deterministic state and the pinned repository snapshot. It does not restore a complete provider conversation.

A context group is invalidated when an identity-bearing input changes, including:

- base commit or explicit input;
- applicable entrypoint or nested instruction content;
- selected project document content;
- context-selection policy;
- Scribe protocol or tool registry;
- provider/model configuration when it affects request compatibility;
- style profile when it is included in the common prefix.

On the same commit, an incomplete campaign may reuse the same local context identities. The provider cache may have expired, so the first resumed request may pay the uncached warm-up cost while later compatible requests reuse the prefix.

## Failure behavior

The accepted M3-X1 bootstrap distinguishes exact-session correlation, ambiguous scope, unsafe repository objects, stale commitments or publication state, invalid encoding, identity collision, caller cancellation, operation timeout, and bootstrap budget exhaustion. It publishes no context on those terminal outcomes; a missing optional root entrypoint may instead produce an immutable incomplete context with an omission fact.

Later context routing and economic work is expected to distinguish:

- missing or unsupported entrypoint;
- invalid or escaped context path;
- cyclic context route;
- context file or byte budget exhausted;
- semantic route unresolved;
- repository snapshot changed while reading;
- unstable request prefix for a claimed context group;
- provider usage unavailable or unsupported;
- provider cache observation unavailable;
- uncached-input or cost ceiling exhausted.

Missing cache telemetry does not invalidate a documentation proposal. Missing required context, stale content identity, exceeded hard budgets, or a request-prefix mismatch fails closed for the affected work.

## Validation

M3-X1 includes deterministic executable coverage for exact pre-read correlation, configured-entrypoint exclusivity, root-to-leaf nested instructions, unique partial-declaration scope, stable no-follow reads, invalid encoding, changed bytes, bounded omissions and budgets, cancellation at every bootstrap stage, content/evidence collision rules, final request binding, session-local cursor tampering/replay, safe diagnostic surfaces, and ordered semantic facts across fresh processes.

Later M3 work must include deterministic tests for:

- nested `AGENTS.md` applicability and nearest-scope precedence;
- explicit entrypoint behavior;
- direct and semantic multi-file routing;
- route cycles, duplicates, traversal, reparse points, truncation, and exhausted budgets;
- instruction-versus-evidence authority;
- stable context and prefix identities across fresh processes;
- distinct scope overlays for targets with different instruction stacks;
- no target-specific data in the claimed shared prefix;
- independent Scribe runs sharing the same immutable context;
- fake tool calls and malformed provider tool arguments;
- the frozen DeepSeek and MiMo request corpus, expected observations, and failure classifications;
- normalization fixtures for every evidenced response shape, including usage and cache observations when reported;
- provider cache miss without correctness drift;
- sensitive-data and publication-boundary scanning of context manifests, requests, diagnostics, and run summaries.

Checked-in request and normalization fixtures are reviewed synthetic protocol artifacts. They may encode canonical messages, tool definitions and calls, terminal responses, adversarial injection text, and the minimal provider fields needed to replay normalization. They do not preserve private or live-run prompts, complete conversations or tool transcripts, hidden reasoning, credentials, raw provider responses, or machine-local data. Live observations are reduced to bounded provenance, field inventories, metrics, and sanitized expected outcomes before publication.

Live synthetic evaluation uses DeepSeek as the primary provider and MiMo as the compatibility provider. It executes the frozen corpus and records observed request, tool, response, failure, cached and uncached usage when available, request count, accepted proposals, validation failures, latency, and cost. Compatibility statements are limited to the paths and observations evidenced by that run. Live credentials are opt-in and are never required by ordinary CI.

## Non-goals

The initial design does not include:

- a general project-understanding or coding agent;
- a separate context-curator agent;
- parent/child agents or conversation forking;
- shared hidden memory between Scribe runs;
- arbitrary Markdown-link recursion;
- arbitrary repository read, shell, edit, web, or GitHub tools;
- a universal provider capability matrix;
- correctness that depends on provider cache retention;
- hard-coded provider prices or off-peak schedules;
- automatic promotion of all previously read files into the shared prefix.

If later evidence shows that very large repositories require model-assisted context curation before target work, that capability requires a separate design decision, structured output, evidence binding, cost comparison, and validation against deterministic selection. It is not assumed by the initial runtime.
