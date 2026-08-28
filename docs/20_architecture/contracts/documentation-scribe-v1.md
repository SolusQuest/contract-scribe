# Documentation Scribe v1

Status: current pre-release draft

This contract defines the minimum producer-consumer boundary for one bounded Documentation Scribe run. Version `1` identifies the compatibility family; the repository revision identifies the exact current draft semantics. Before the first release, a coherent change may replace this draft in place. Consumers must reject unsupported versions rather than add aliases, migration readers, or dual formats.

The Scribe receives one caller-selected audit target and returns one terminal run result. It is a read-only structured-documentation role. It does not select work, render XML, edit source, produce a patch or diff, persist a conversation, or publish anything.

## Contract artifacts

The family consists of:

- `schemas/documentation-scribe/v1.request.schema.json`;
- `schemas/documentation-scribe/v1.run-result.schema.json`;
- `schemas/documentation-scribe/v1.registry.json`;
- raw public fixtures under `tests/fixtures/documentation-scribe/v1/`;
- Core DTOs and typed read/tool ports in `DocumentationScribeContracts.cs`;
- strict parsing, semantic validation, correlation, and M2 projection in `DocumentationScribeValidation.cs`.

The schemas validate serialized shape and closed vocabularies. The Core validator owns cross-field identity, ordering, reference, evidence, policy, correlation, and budget rules that JSON Schema cannot express. A payload is accepted only when both applicable layers accept it.

## Serialization boundary and identity

The request and run result are UTF-8 JSON objects with no BOM. They use JSON Schema Draft 2020-12, local `$ref` values, and closed objects. Unknown properties, duplicate properties, comments, trailing commas, malformed UTF-8, invalid JSON, invalid scalar content, and nesting deeper than 64 fail closed. Each artifact is limited to 1 MiB of encoded UTF-8.

`ParseRequest(ReadOnlyMemory<byte>)` is the only public request construction boundary. The accepted request identity is lowercase SHA-256 over the exact accepted request bytes. The contract defines no canonical JSON serializer and no semantic identity that survives reserialization. A different byte sequence is a different request even if it would decode to equivalent fields.

The run result, every terminal variant, and the run envelope repeat that exact request SHA-256 and the validated attempt identity. The attempt identity has the lexical form `scribe-attempt.` followed by 32 lowercase hexadecimal characters. A result is never valid without the parsed request and expected attempt supplied to `ParseRunResult`.

Model-produced proposal/skip payloads enter through that raw validated parse boundary. After that parse succeeds, the runtime may re-envelope the immutable validated proposal/skip at its final reducer checkpoint through `CreateResultFromValidatedTerminal`; the factory rejects a result correlated to any other request or attempt. Runtime-owned skip, failure, and cancellation results may instead use the other public Core factories. All factories require the parsed request and validated attempt, derive or verify both correlation fields, copy tool/style identities from the request, bound every observation against its limits, and admit only the same code-specific diagnostic shapes.

## Scribe Request

`scribeRequestVersion` is exactly `1`. One request contains the following fields and no others.

### Context

`context` binds facts owned by the deterministic caller:

- `repositoryContextRef`: the current `repoctx-` identity;
- `inputIdentity`: one normalized repository-relative `.sln`, `.slnx`, or `.csproj` input identity prepared by the caller;
- `targetProfile`: the current M1 `profile.external-api` or `profile.assembly-visible` value;
- `auditOutcome`: the current M1 audit outcome copied for the selected target.

`inputIdentity` follows the current CLI input domain exactly: its final extension is `.sln`, `.slnx`, or `.csproj`, compared ASCII case-insensitively. Repository-relative paths follow the current M1/M2 rules: at most 512 Unicode scalars, forward slash separators, no root, backslash, NUL, empty, `.` or `..` segment, and no drive-like `A:` prefix. Other colon characters are not rejected merely for being colons.

The request deliberately omits `AuditReason`. M1 owns the outcome/reason matrix, and the later composition path revalidates the copied outcome against the exact current Audit Result row and loaded session before invoking the Scribe. The Scribe cannot reinterpret classification.

### One selected target

`target` is one object, never an array. It contains:

- the current Core `SymbolRef` (`compilationContextRef` and `documentationCommentId`);
- one source commitment containing a current evidence-locator shape and exact `contentSha256`;
- the ordered applicable-component set using the current M2 `typeParameter`, `parameter`, `return`, and `value` component semantics.

Applicable-component identities are unique and strictly ordinally increasing. Named parameter and type-parameter components carry their exact name; return and value components do not. The source commitment is data used for later freshness validation. It is not an M2 patch locator, edit kind, write handle, or rendering instruction.

The reused M1/M2 identity domains are exact rather than Scribe-local aliases. Compilation or assembly references are 1–128 lowercase ASCII alphanumeric, dot, underscore, or hyphen characters and begin with a lowercase alphanumeric character. Documentation comment IDs are 3–1024 XML-valid scalars, begin with one of `T:`, `M:`, `P:`, `F:`, `E:`, or `N:`, and contain no control scalar. Source-generator locator IDs are `sgp.`/`sgo.` plus lowercase SHA-256; tool-generated IDs are `tgp.`/`tgo.` plus lowercase SHA-256. Type-parameter and parameter identities are exactly `type-parameter/<canonical ordinal>` and `parameter/<canonical ordinal>`; return/value identities are exactly `return` and `value`. Names are non-empty XML-valid text of at most 128 scalars. The set cannot contain both return and value, duplicate identities, or duplicate names within one named kind.

### Style Profile

`styleProfile` contains only machine-visible input needed by the current producer-consumer path:

- a closed `styleProfileId` and `outputLanguageId`;
- summary, remarks, and exception dispositions (`required`, `optional`, or `forbidden`) plus scalar ceilings;
- one ordered policy for every exact applicable-component identity;
- `<inheritdoc/>` disposition (`allowed`, `required`, or `forbidden`);
- strictly ordered, disjoint exact allowed and forbidden literal lists;
- strictly ordered closed claim-category policies;
- content-unit and per-unit evidence-reference ceilings.

Each claim policy declares whether complete evidence is required and the exact allowed evidence-authority set. Authorities are, from lower to higher:

1. `authority.source-implementation`;
2. `authority.source-declaration`;
3. `authority.existing-documentation`;
4. `authority.test`;
5. `authority.repository-documentation`;
6. `authority.public-contract`.

The validator checks only declared identifiers, closure, ordering, component equality, scalar/count ceilings, dispositions, exact literal matches, and claim/evidence metadata. It does not infer language adherence, voice, general terminology quality, semantic entailment, usefulness, or whether an unmodeled claim is material. Those remain evaluation observations.

When `<inheritdoc/>` is required, every structured-content policy is forbidden and its scalar ceiling is zero. An inherit-doc proposal then contains exactly one `content.inherit-doc` unit. When inherit-doc is forbidden, that unit is invalid.

### Context and evidence references

`contextReferences` is a strictly ID-ordered bounded list of project instructions, repository documentation, or style examples. Each row carries only:

- a stable reference ID and closed kind;
- the exact current repository-context identity;
- a repository-relative path and content SHA-256;
- original/included UTF-8 byte counts and an exact truncation flag.

Context references are prompt/context inputs, not claim evidence, and proposal units cannot cite their IDs.

`evidenceReferences` is a strictly ID-ordered bounded list. Each row carries:

- the exact current repository-context identity;
- a current target or applicable-component `EvidenceSubject`;
- the current M1 `EvidenceKind`, `EvidenceRelation`, and `EvidenceLocator` semantics;
- one closed Scribe authority;
- exact content SHA-256 and byte-count/truncation facts;
- a non-empty ordered list of claim categories the evidence may support.

No source excerpt is part of the request artifact. The run-time read ports may return bounded typed content, but complete tool arguments/results and excerpts do not enter the canonical or publishable result.

Every evidence authority must agree with its existing evidence kind: implementation, declaration/attribute, existing XML documentation, test, repository documentation, and public contract map to their corresponding authority. Every evidence subject must be the selected target or one exact selected applicable component. Every evidence/context row must carry the current request repository context. `includedUtf8ByteCount` cannot exceed `originalUtf8ByteCount`, and `isTruncated` is true exactly when included bytes are fewer.

`evidenceConflicts` contains only the relation `evidence-conflict.higher-authority-contradicts`. A row names two request-visible evidence IDs with the same exact subject, and the declared higher row must have strictly greater registered authority. Conflict rows are unique and strictly ordered by `(higher ID, lower ID)`. This explicit input is the only deterministic lower-authority conflict rule; the validator does not infer contradictions from prose.

### Tool policy and limits

`toolPolicyId` identifies the closed tool policy selected by the caller. It does not grant an operation or carry a provider tool schema.

`limits` contains independent ceilings for attempts, context references and included bytes, evidence references and included bytes, provider requests, tool rounds, tool calls, input tokens, uncached input tokens, output tokens, cost microunits, and elapsed milliseconds. Request arrays and normal successful run observations cannot exceed them. Values are non-negative or positive as their schemas specify and must fit the published numeric bounds. Attempt numbers are positive and cannot exceed `maximumAttempts`. No token decomposition equality, cache dependency, price lookup, or cost derivation is defined here.

The request contains no M4 work-plan, campaign, branch, issue, pull-request, provider conversation, fallback, cursor, checkpoint, or publication identity.

## Scribe Run Result

`scribeRunResultVersion` is exactly `1`. The result contains root request/attempt correlation, the complete active `dynamicEvidenceReferences` overlay, exactly one terminal object, and one separate run envelope.

### Trusted dynamic evidence overlay

`dynamicEvidenceReferences` uses the same closed row, identifier, subject, locator, authority, claim-category, ordering, and repository-context namespace as request `evidenceReferences`. It is empty when no successful tool result added evidence. For a proposal or skip, it contains the full active trusted overlay even when a row is not cited by the terminal. It must be empty for failure and cancellation.

Raw result JSON cannot authorize an overlay. `ParseRunResult` receives the exact parsed request, expected attempt, and a separately supplied trusted overlay; the embedded rows must equal that overlay exactly. Terminal evidence lookup uses the ordered union of request rows and the accepted overlay, so a model-provided ID without a trusted typed producer row is dangling evidence and fails closed.

The Agent passes a successful tool result to Core as bounded typed evidence inputs without a caller-selected evidence ID, repository-context identity, request identity, attempt identity, session identity, or content bytes. Core validates the subject, kind/relation/authority, locator, committed content SHA-256, exact original/included byte counts, truncation fact, and ordered claim categories against the request, supplies the current repository context, and derives the ID. A truncated repository or generated-output row is valid only with an exact span over its fully committed source.

The derived ID is `evidence.dynamic.` plus lowercase SHA-256 over a domain-separated canonical preimage containing only subject, kind, relation, authority, locator, content SHA-256, byte counts, truncation, and ordered claim-category IDs. Repository/request/attempt/session/call/cursor identities, time, and current culture do not participate.

Within one tool payload, a duplicate derived ID is a terminal tool-protocol failure. Against request evidence or previously charged dynamic evidence, exact row equivalence reuses the existing row; any differing metadata fails closed. `tool.outcome.complete` and `tool.outcome.incomplete` may add rows, `tool.outcome.unavailable` adds none, and failure, cancellation, timeout, or budget outcomes commit none.

The runtime publishes one complete tool round atomically: completed exchanges and active-overlay additions become visible together at the final priority checkpoint or neither becomes visible. Charged and observed state is deliberately separate and monotonic. A prevalidated round is counted when execution begins; each call is counted when invoked; and successfully validated dynamic identities, semantic bytes, and canonical successful-exchange bytes are charged as incurred. A later invalid result, collision, failure, timeout, cancellation, budget crossing, or final-checkpoint priority outcome discards only the buffered exchanges and current-round active additions, not those run-wide charges or public call/round observations. Each completed exchange exposes product-owned `evidenceReferences` separately from opaque `resultJson`, and deterministic provider history serializes those rows explicitly. A transient provider retry clears completed exchanges and the active overlay before the next attempt, but retains tool observations and run-wide charged evidence identity/byte history; this prevents a retry from reclaiming work or evidence budget.

Three checks share the request evidence ceilings but are evaluated independently: distinct charged semantic-reference count, included bytes of distinct charged rows, and cumulative canonical model-visible bytes for every successful tool exchange including repeated equivalent results and product metadata. Crossing any check terminates with the budget failure and commits none of that tool round.

### Proposal

A `proposal` repeats the selected repository context, `SymbolRef`, and complete source commitment (locator plus SHA-256), all exactly equal to the request. It contains one strictly ordered flat list of content/claim units. Unit kinds are:

1. `content.summary`;
2. `content.type-parameter` ordered by exact component identity;
3. `content.parameter` ordered by exact component identity;
4. `content.return`;
5. `content.value`;
6. `content.exception` ordered by exception type documentation ID;
7. `content.remarks`;
8. `content.inherit-doc` as the sole unit when selected.

Every unit owns exactly one request-declared claim category and its own non-empty, strictly ordered request-visible evidence-ID list. Component units repeat the exact applicable-component identity; named units also repeat its exact name. Exception units carry a type documentation ID. Structured units carry ordered plain-text line arrays. Inherit-doc carries an empty line array.

A structured proposal contains exactly one summary plus exactly one unit for every applicable component and no extra component unit. Logical lines are non-empty XML 1.0 text, contain no line-separator scalar, are at most 2,048 scalars each, and total at most 32,768 scalars across the projected block. Exception IDs follow the current M2 safe `T:` subset: 3–1,024 scalars with no whitespace, control, or XML markup punctuation. Raw documentation syntax includes XML tags, comments, processing instructions, CDATA terminators, entity references, and `///`; it is never accepted as plain content.

The validator rejects a unit when any evidence reference is missing, duplicated, dangling, stale, for the wrong subject, outside the evidence row's claim-category allowlist, outside the Style Profile authority allowlist, truncated when the claim requires complete evidence, or named as the lower row of an explicit accepted conflict. Exact forbidden literals are compared ordinally against actual unit text. Raw `///` or XML-documentation syntax is not content text.

After validation, Core retains the original ordered M3 units and projects them to the current M2 `DocumentationPatchContent` union. Structured units map to summary/type-parameter/parameter/return/value/exception/remarks fields; the single inherit-doc unit maps to `DocumentationPatchInheritDocContent`. Projection does not grant source mutation authority and does not erase the original claim/evidence facts.

Core may bind the exact parsed request, expected attempt, and immutable validated Run Result into one current-call `DocumentationScribeValidatedRunOutcome`. Binding repeats the complete root/envelope request and attempt correlation, target/source/context, tool/style, trusted overlay/terminal, proposal, provider-disposition, and bounded-envelope checks. The outcome retains the exact accepted request and result objects, including the original ordered proposal units, and has no independently supplied proposal or disposition field. It is not serialized, persisted, transferable to another process or session, or a source-mutation capability. Completion of Agent execution plus successful binding is the authoritative M3 point; later proposal postflight or M2-authorization failure cannot erase that outcome, although it withholds M2 authority.

The proposal has no C#, XML nodes or trivia, source bytes, replacement text, edit kind, patch, diff, formatting instruction, or writer capability.

### Structured domain skip

A `skip` contains only one of:

- `scribe.skip.insufficient-evidence`;
- `scribe.skip.unsupported-current-m3-domain`.

It may cite an ordered subset of request evidence IDs. A skip is a domain result. Provider, tool-protocol, validation, timeout, cancellation, and budget outcomes are not skips, and a skip cannot change the caller-owned M1 classification.

### Failure and cancellation

A terminal `failure` contains one closed code:

- `scribe.failure.provider`;
- `scribe.failure.tool-protocol`;
- `scribe.failure.validation`;
- `scribe.failure.timeout`;
- `scribe.failure.budget`;
- `scribe.failure.internal`.

Only `scribe.failure.provider` also contains `providerFinalDisposition`, with exactly `retryable` or `terminal`. Every other failure and terminal forbids that property. The disposition summarizes the final provider terminal after the Agent-owned retry loop; it does not expose transport/provider taxonomy and does not authorize a campaign retry. A provider failure is producer-realizable only when at least one provider request was made and `providerRequestCount >= attemptNumber`. `retryable` additionally requires `attemptNumber == maximumAttempts`; `terminal` is allowed at any otherwise valid attempt. A transient final-attempt/provider-request-limit tie is therefore provider/retryable, while an observed higher-priority cancellation, timeout, or budget crossing remains its existing terminal.

Cancellation is a disjoint `cancelled` variant with `scribe.cancelled.caller` or `scribe.cancelled.shutdown`. A terminal object cannot combine fields from another variant.

## Run envelope

The envelope repeats the exact request SHA-256 and attempt identity and records only:

- provider and model configuration identities;
- Scribe protocol, tool-policy, and Style Profile identities;
- positive attempt number bounded by the request's `maximumAttempts`;
- provider-request, tool-round, tool-call, and elapsed-time counts;
- independently optional usage, cache, and cost observations;
- a bounded ordered diagnostic list.

Tool-policy and Style Profile identities must exactly equal the request. Provider/tool counts cannot exceed request limits. Optional usage fields independently observe input, cached input, uncached input, output, and reasoning tokens; no equality between token fields is asserted. Cache is one of hit, miss, mixed, or not reported. Cost is a currency configuration identity plus non-negative microunits. Provider transport supplies the closed model-failure class, the Agent owns invocation-local retry exhaustion and the final disposition, and configured cost calculation is owned by evaluation work.

The request ceilings are execution controls, while the result schema's larger published maxima are artifact-safety bounds. Each configured token, cost, and elapsed maximum is strictly lower than its corresponding artifact-safety maximum, leaving a bounded range in which the result can preserve the overrun that caused termination. A proposal or skip must remain within configured ceilings. A budget failure may truthfully report token or cost observations that crossed a configured ceiling. A timeout failure may truthfully report the elapsed overrun and any simultaneous bounded token or cost overruns observed at the same reducer checkpoint. Cancellation may report either kind of already-incurred observation. Those observations must still remain within the artifact-safety maxima. This terminal-aware allowance preserves the facts that caused termination without granting permission for another attempt.

Diagnostics are stable code plus code-specific allowlisted metadata:

| Code | Stage | Additional field |
| --- | --- | --- |
| `scribe.diagnostic.provider-failure` | `provider` | none |
| `scribe.diagnostic.tool-failure` | `tool` | `referenceId` |
| `scribe.diagnostic.result-rejected` | `result` | `validationCode` |
| `scribe.diagnostic.runtime-failure` | `runtime` | none |

The envelope and terminal contain no credential, complete prompt, tool argument/result, transcript, source excerpt, raw provider response or error, arbitrary exception text, hidden reasoning, absolute path, GitHub identity, or arbitrary metadata dictionary.

## Provider-neutral typed read/tool seam

Core defines only compile-time typed contracts:

- `IDocumentationScribeToolRequest<TResult>`;
- `IDocumentationScribeToolResult`;
- `IDocumentationScribeToolDescriptor<TRequest, TResult>`;
- `IDocumentationScribeToolPort<TRequest, TResult>`.

Concrete repository and semantic operations are selected by their downstream X2/X3 owners as sealed request, result, descriptor, and port types. Defining a descriptor is not authority. An operation becomes callable only when R2's internal reviewed registry explicitly admits the exact descriptor/request/result/adapter tuple. Any heterogeneous routing or type erasure stays behind that Agent-owned closed registry. The public Core port contract never uses `object`, a dictionary, `JsonElement`, JSON or raw-byte payloads, dynamic/reflection dispatch, arbitrary delegates, a service provider, or a capability bag.

Every typed result carries one Core-owned closed `DocumentationScribeToolOutcome`:

- complete;
- bounded incomplete;
- unavailable;
- terminal no-content failure;
- cancelled;
- operation-local timed out;
- budget exhausted.

Operation results add only their bounded typed data, including optional ID-free dynamic-evidence inputs on complete or incomplete outcomes. Encoders do not accept an evidence-item count; the runtime derives semantic counts and encoded exchange bytes from the accepted typed values. The seam exposes no physical or absolute path, workspace/filesystem object, writer, mutable session, generic network object, credential, or mutation method. Repository-relative locator values are bounded data, not ambient filesystem authority. R1 defines no fake-only normative operation and does not freeze the later provider-visible tool inventory, names, JSON schemas, grouping, cursors, paging, or Roslyn mechanics.

## Stable validation categories

Request failures use `scribe.request.<category>` and result failures use `scribe.result.<category>` with an RFC 6901 pointer when a bounded location exists. A property-name segment is retained only when it is a bounded safe lexical name; otherwise the pointer stops at the containing object so rejected source or provider text cannot become diagnostic content. Current categories cover document size, BOM, UTF-8/JSON, duplicate property, unsupported version, shape, unknown field, vocabulary/identity, order, component, reference, stale/wrong subject, style, evidence, content, diagnostic, correlation, and budget failures.

The validator reports deterministic structured facts only. It never includes the rejected source, provider text, exception text, or other arbitrary input in a diagnostic.

## Campaign completion binding

M3 binding remains an immutable execution fact, but it does not independently authorize a Campaign State completion. For campaign proposal execution, Core issues one process-local, one-shot completion registrar from the exact accepted provider invocation. X1's private campaign binder is the sole production consumer: it interprets the actual private prepared result, gates dispatch on the first underlying model `SendAsync`, and mints one opaque completion authority for coherent ordinary M3 and X1-only final facts. The campaign executor receives neither the registrar nor the prepared result or raw Run Result.

A coherent ordinary M3 result may complete while the reservation lifecycle is still available when the runtime terminates before its first model send, or after dispatch has started. A pre-send cancellation, timeout, or exhaustion with no bound M3 result instead uses the campaign stop transition and leaves the work planned. X1 proposal-invalid or host facts may retire an available lifecycle conservatively; abrupt loss or incoherent output leaves the reservation active for recovery. The X1 campaign binder revalidates exact request SHA and complete current request authority across its private byte reparse, runtime provider/model/protocol identity, and exact attempt without requiring cross-parse object identity.

The registrar, completion authority, prepared result, Run Result, host elapsed observation, and repository session are process-local and are never serialized. This join does not grant M2, filesystem, credential, GitHub, or publication authority and does not change the accepted one-target `ExecuteAsync`/Patch path.

## Compatibility and non-claims

M1 Audit Result and M2 Documentation Patch remain current pre-release v1 drafts. Introducing this producer does not increment either family. Cross-contract tests bind reused symbol/profile/evidence-locator/component/content semantics, and the ordinary M1/M2 suites remain authoritative for those contracts.

This draft does not claim provider compatibility, cache behavior, price stability, model quality, semantic evidence entailment, useful prose, production release support, persistence compatibility, migration, campaign resumability, or publication authority. Those claims require their later executable evidence and gates.
