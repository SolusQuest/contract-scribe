# Security boundary

The deterministic audit baseline must not initiate network access or require a provider secret or GitHub write token. Proposal and publishing capabilities are optional, explicitly configured, and separated from deterministic audit execution.

Public fixtures and CI must be synthetic and must not contain downstream-private source, private or live-run prompts, complete transcripts, raw logs, credentials, or private paths. Reviewed public-safe protocol templates, canonical request/tool-call vectors, adversarial prompt-injection fixtures, and minimized or synthetic response-normalization fixtures are permitted when required for executable contract validation. When a result cannot be supported by bounded evidence and a defined policy, the future system must produce a structured skip or fail closed rather than inventing a contract.

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

Provider requests must not contain credentials, machine-absolute paths, private issue text, raw build logs, or unrelated repository content. Public artifacts must not contain private or live-run prompts, raw provider responses, hidden reasoning, or complete live tool transcripts. Project-owned, reviewed synthetic protocol templates and executable request/tool-call fixtures may be public when required by a contract. Provider normalization fixtures reproduce only the minimal public-safe fields needed for deterministic parsing and never preserve a raw live response.

Repository and scope context snapshots use repository-relative paths, source/content hashes, explicit roles, and truncation records. Full context text and tool results are not stored in the GitHub Issue ledger. Shared prompt prefixes must not contain secrets, run-specific identifiers, temporary paths, or unrelated target evidence merely to improve provider cache reuse.

The Scribe's product mutation boundary is enforced by the closed read-only tool registry, internal Core capabilities whose production friend allowlist contains only Cli, and CLI composition/API-surface tests that exclude `ContractScribe.Agent`. Project-reference direction alone is not treated as proof that Agent cannot receive a mutation capability. This in-process boundary does not claim protection against malicious code or replace external isolation for repositories of reduced trust.

Independent Scribe runs may share immutable context inputs and local read-only indexes. They do not share mutable provider conversations or hidden reasoning. A provider cache hit or provider session identifier never grants authority and is never used as proof of repository identity or safe resume.

## Patch boundary

Only the deterministic patch engine may write a candidate source file. It verifies the expected source identity, selected target, allowed documentation span, syntax, semantic invariants, encoding, and idempotency before a patch becomes publishable.

A model-generated proposal is never treated as a source patch or as evidence that a patch is safe.

## GitHub boundary

Only the GitHub adapter may consume or use a GitHub write token to mutate GitHub. Permissions are least privilege and scoped to the requested operation. A selected Action host may receive and forward an explicitly allowlisted token to the CLI, so it is inside the credential-handling boundary, but it cannot interpret, persist, log, or independently use that token. The audit, planner, model runtime, and patch validator do not receive the token.

Campaign and ledger records contain identifiers, hashes, statuses, counts, budgets, validation summaries, and GitHub URLs only. They exclude source excerpts, private or complete prompt content, raw provider responses, secrets, and complete diffs.

All mutations are idempotent and reconciled before replay. Base drift, branch ownership mismatch, unexpected human changes, malformed state, and conflicting active pull requests fail closed.

### Implemented GitHub transport boundary (M5-R2)

`ContractScribe.GitHub/Transport` accepts only the existing Core-validated publication authority plus an already-supplied opaque credential. It neither discovers environment/configuration values nor persists credentials, invokes a shell, reads source files, logs requests, or exposes raw HTTP/JSON/error objects. Source-bearing successful blob/commit/PR observations stay inside the adapter. Failure values contain only closed codes, HTTP status, bounded retry facts and source-free reconciliation identity; all transport DTOs have non-content-bearing `ToString`. The private client releases its credential reference on disposal. This is an in-process capability boundary, not protection from malicious code in the same process.

Production requests use fixed `https://api.github.com/` REST and GraphQL routes, API version `2026-03-10`, explicit Bearer authorization, and no redirects, cookies, proxy, ambient credentials or activity-header propagation. The client pins the first authenticated numeric ID, node ID and canonical owner/name tuple after ASCII case-insensitive comparison to configured names; subsequent identity drift fails. Exact ref reads strip only `refs/` and encode each remaining segment without decoding literal percent sequences. Repository identity, returned OIDs, and present Git object URLs must agree; a response URL is never sent directly.

GET retries are bounded to one additional attempt for transient 502/503/504 or connection/response loss without a rate hint. Authentication, permission, rate-limit, not-found, conflict, invalid-data, permanent TLS/protocol failures and every mutation are not retried. Each request has a 30-second deadline (at most two attempts for reads); a PR enumeration has a 90-second total deadline. Writes use exact HTTP/1.1, no Expect-Continue negotiation, and one-shot content that refuses a second serialization, including a transparent handler resend. No request buffering/replay helper exists. Once dispatched, cancellation, timeout, transport failure, hostile redirect, or malformed response retains ambiguity and the exact prepared context. Even a valid response is only `NeedsReadback`, never publication success.

The only ref write is a fixed GraphQL `updateRefs` document with one repository and one owned coordination/proposal ref, mandatory before/after OIDs, `force=false`, and a deterministic operation-bound client mutation ID. Zero before means explicit expected absence; zero after/deletion is forbidden. Partial/null data, errors, duplicate fields, unexpected selected members or a different mutation ID cannot acknowledge the write. R3/R4 must perform direct exact ref readback before accepting a transition. Object contexts bind repository, resource kind, expected OID and operation commitment. PR-create contexts additionally bind creation commitment, head/base refs and OIDs, title/body hashes, fixed draft state and disabled maintainer modification. Raw bodies, titles, source bytes and credential material never enter these failure contexts.

REST parsing rejects duplicate members recursively, wrong required types, invalid identities, unknown consumed enum states, truncated trees and contradictory observations while ignoring unused additive metadata. JSON is strict UTF-8 with maximum depth 32, 256 properties per object and 100,000 items per array. Successful bodies and encoded requests are at most 24 MiB; error bodies are at most 64 KiB; response headers are at most 64 KiB. Blob decoded bytes are at most 16 MiB. Trees are nonrecursive, at most 100,000 unique entries, with conservative pre-encoding request bounds. PR title/body limits are 256/65,536 UTF-16 code units. PR enumeration is bounded to 100 pages, 100 rows per page, 10,000 observed rows and 64 MiB aggregate body bytes; no partial result escapes a failed bound.

PR enumeration validates optional Link relations, fixed filters and exactly the next page, accepting only the pinned canonical repository route or its authenticated numeric-ID alias on the fixed origin. It reconstructs the outgoing canonical route itself. Duplicate IDs deduplicate only when all consumed facts agree, and numeric ID/number/node ID must remain bijective. A contradictory promised continuation fails; terminal no-next evidence proves observed exhaustion, not cross-request snapshot isolation. Valid fork and deleted-head repository/ref observations remain nullable typed facts for R5 ownership classification. Read selectors and observed branch refs use bounded [Git wire naming rules](https://git-scm.com/docs/git-check-ref-format.html), not the stricter Core policy for owned publication targets: Unicode whitespace, uppercase `.LOCK`, and dots at nonfinal component ends remain exact data. ASCII control/space/DEL, lowercase `.lock` suffixes, traversal and whole-ref terminal dots remain invalid. Mutation targets still require the exact Core-derived owned refs and authority-bound PR refs; observing a valid foreign name grants no write authority. R5 must reread mutable observations before claiming ownership or uniqueness.

`GET /user` is an optional authenticated-user observation, not a factory prerequisite or a promise that every token kind supports it. Repository-role booleans are not effective token grants. `X-Accepted-GitHub-Permissions` represents alternatives (semicolon OR, comma AND), not granted scopes: only complete supported metadata/contents/pull_requests groups are projected, and omitted unknown-name alternatives are explicitly reported. No Issues permission is requested or inferred. Actual token-kind and GitHub platform proof remains H3's responsibility.

The single test hook is private, reflection-only and default-inert. It accepts only canonical numeric `127.0.0.1`/`[::1]` HTTP roots with an explicit nondefault port and the exact synthetic placeholder; all other credentials fail before handler use. The placeholder also fails when no hook is registered. There is no product environment variable, command-line switch, configuration key, or handler/endpoint factory overload to select it. Scripted fast tests use this same hook; H2 later owns process-startup reflection wiring. These tests prove the local transport boundary, not live platform behavior.

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
