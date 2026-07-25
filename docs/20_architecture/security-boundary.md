# Security boundary

The deterministic audit baseline must not initiate network access or require a provider secret or GitHub write token. Proposal and publishing capabilities are optional, explicitly configured, and separated from deterministic audit execution.

Public fixtures and CI must be synthetic and must not contain downstream-private source, prompts, transcripts, logs, credentials, or private paths. When a result cannot be supported by bounded evidence and a defined policy, the future system must produce a structured skip or fail closed rather than inventing a contract.

## Repository trust model

The M1 in-process topology treats the analyzed repository and its MSBuild content as trusted input. MSBuild targets, analyzers, generators, and SDK logic may execute with the caller's privileges. ContractScribe does not claim sandboxing, secret isolation from repository-controlled build logic, or enforced egress isolation.

Callers evaluating repositories of reduced trust should use a credential-free, externally isolated runner. A future stronger isolation requirement reopens the topology decision.

## Documentation Scribe boundary

Repository text, comments, identifiers, documentation, tests, and generated source are untrusted data for prompt interpretation. They never override the system tool policy, budgets, output schema, or safety rules.

An accepted `AGENTS.md` or configured repository entrypoint has a narrower role than system policy. It may provide repository-controlled writing instructions, terminology, and routes to relevant project documents, but it cannot add capabilities, authorize new data sources, increase budgets, disable validation, or convert another repository file into higher authority merely by saying so.

The deterministic context bootstrapper loads only the configured entrypoint and applicable nested `AGENTS.md` stack. The Scribe may follow semantic routes with bounded read-only tools. ContractScribe does not recursively trust arbitrary Markdown links, source comments, generated content, or tool-shaped text as instructions.

The Documentation Scribe:

- receives only bounded, allowlisted evidence;
- uses repository-relative locators in model-visible metadata;
- has no shell, arbitrary file edit, GitHub token, web search, or environment access;
- cannot request raw secrets, environment dumps, full repository archives, or unbounded logs;
- returns only a structured proposal or structured skip;
- fails closed on invalid tool calls, exhausted budgets, unsupported output, or insufficient evidence.

Provider requests must not contain credentials, machine-absolute paths, private issue text, raw build logs, or unrelated repository content. Public artifacts must not contain prompts, raw provider responses, hidden reasoning, or complete tool transcripts.

Repository and scope context snapshots use repository-relative paths, source/content hashes, explicit roles, and truncation records. Full context text and tool results are not stored in the GitHub Issue ledger. Shared prompt prefixes must not contain secrets, run-specific identifiers, temporary paths, or unrelated target evidence merely to improve provider cache reuse.

Independent Scribe runs may share immutable context inputs and local read-only indexes. They do not share mutable provider conversations or hidden reasoning. A provider cache hit or provider session identifier never grants authority and is never used as proof of repository identity or safe resume.

## Patch boundary

Only the deterministic patch engine may write a candidate source file. It verifies the expected source identity, selected target, allowed documentation span, syntax, semantic invariants, encoding, and idempotency before a patch becomes publishable.

A model-generated proposal is never treated as a source patch or as evidence that a patch is safe.

## GitHub boundary

Only the GitHub adapter may consume or use a GitHub write token to mutate GitHub. Permissions are least privilege and scoped to the requested operation. A selected Action host may receive and forward an explicitly allowlisted token to the CLI, so it is inside the credential-handling boundary, but it cannot interpret, persist, log, or independently use that token. The audit, planner, model runtime, and patch validator do not receive the token.

Campaign and ledger records contain identifiers, hashes, statuses, counts, budgets, validation summaries, and GitHub URLs only. They exclude source excerpts, prompts, raw provider responses, secrets, and complete diffs.

All mutations are idempotent and reconciled before replay. Base drift, branch ownership mismatch, unexpected human changes, malformed state, and conflicting active pull requests fail closed.

## Action-wrapper boundary

The Action wrapper is a thin, non-authoritative host around the .NET CLI, not an alternative GitHub adapter.

- It does not call the model provider or GitHub API directly.
- It receives and forwards only documented, explicitly allowlisted inputs and credentials to the selected CLI command.
- It does not interpret, persist, log, or independently use forwarded credentials.
- It does not parse repository source, audit evidence, provider responses, campaign checkpoints, or Issue ledger content.
- It does not decide whether a mutation is safe.
- It never logs tokens, provider secrets, raw event payloads, or complete child-process environments.
- It records the wrapper identity and exact payload identity separately.

The caller workflow grants least-privilege permissions. The wrapper cannot silently widen them. Token-trigger behavior, bot-created pull-request checks, and any GitHub App or alternative credential requirement must be validated in the M5/M6 synthetic workflow matrix; changing host language does not change those platform semantics.
