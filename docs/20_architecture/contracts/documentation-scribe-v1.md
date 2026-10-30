# Documentation Scribe v1

Status: current pre-release draft

This contract defines the minimum producer-consumer boundary for one bounded Documentation Scribe run. Version `1` identifies the compatibility family; the repository revision identifies the exact current draft semantics. Before the first release, a coherent change may replace this draft in place. Consumers must reject unsupported versions rather than add aliases, migration readers, or dual formats.

The Scribe receives one complete caller-selected manifest and an independently authorized executable subset. The runtime accepts incremental proposal or skip submissions for explicit executable target IDs and returns a separate session stop with Host-derived coverage. It is a read-only structured-documentation role. It does not select work, render XML, edit source, produce a patch or diff, persist a conversation, or publish anything.

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

`ParseRequest(ReadOnlyMemory<byte>)` is the only public request construction boundary. The accepted request identity is lowercase SHA-256 over the exact accepted request bytes. The contract defines no canonical JSON serializer and no semantic identity that survives reserialization. A different byte sequence is a different request even if it would decode to equivalent fields. A campaign executor must bound and copy caller-owned request memory once before parsing; reservation correlation, the private X1 reparse, and provider execution then consume only that immutable owned snapshot, so later mutation of the caller buffer cannot substitute a different request.

The run result and run envelope repeat that exact request SHA-256 and the validated attempt identity. Each validated target submission binds the request hash, exact manifest target ID, canonical content hash and acknowledgement stage. The attempt identity has the lexical form `scribe-attempt.` followed by 32 lowercase hexadecimal characters. A result is never valid without the parsed request and expected attempt supplied to `ParseRunResult`.

Model-produced per-target proposal/skip content enters `ValidateTargetSubmission` with an explicit executable target ID and trusted typed evidence. It reuses the same content/style/evidence validator for that member. `AcknowledgeSubmission` correlates an immutable validated submission to the Host's receipt; it cannot turn a foreign receipt into acceptance. `CreateSessionResult`, `CreateFinishedResult` and the failure/cancellation factories bind progress to the parsed request and expected attempt. `ParseRunResult` accepts only the current incremental shape. Factories derive tool/style identities and enforce the same bounded observations and diagnostics.

## Scribe Request

`scribeRequestVersion` is exactly `1`. One request contains the following fields and no others.

### Context

`context` binds facts owned by the deterministic caller:

- `repositoryContextRef`: the current `repoctx-` identity;
- `inputIdentity`: one normalized repository-relative `.sln`, `.slnx`, or `.csproj` input identity prepared by the caller;
- `targetProfile`: the current M1 `profile.external-api` or `profile.assembly-visible` value.

`inputIdentity` follows the current CLI input domain exactly: its final extension is `.sln`, `.slnx`, or `.csproj`, compared ASCII case-insensitively. Repository-relative paths follow the current M1/M2 rules: at most 512 Unicode scalars, forward slash separators, no root, backslash, NUL, empty, `.` or `..` segment, and no drive-like `A:` prefix. Other colon characters are not rejected merely for being colons.

The request deliberately omits `AuditReason`. M1 owns the outcome/reason matrix, and the later composition path revalidates the copied outcome against the exact current Audit Result row and loaded session before invoking the Scribe. The Scribe cannot reinterpret classification.

### Selected manifest and executable subset

`manifest` is a nonempty ordered list of at most 4,096 entries, also bounded by the 1 MiB artifact limit. Entries have unique `targetId` values and unique complete SymbolRefs. Each entry contains `target`, its own `auditOutcome`, nullable `styleProfile`, ordered `contextReferenceIds`, ID-sorted `evidenceReferenceIds`, and nullable `grouping`. Entry order is the deterministic caller's declared order, not an ID sort.

`executableTargetIds` is an ordered unique subset of manifest IDs. Every executable member has a nonnull style. Unsupported/nonexecutable members, including current class/property metadata, need no execution-only style. An empty executable subset is valid metadata and starts no runtime conversation. Each item validates against its explicitly named member and exact per-member evidence, source and style commitments. The envelope lists the sorted unique style identities of all executable members. Campaign durable proposal admission still requires one manifest member and one executable member until M7-S5.

The CLI receives a separate Host-owned selected collection from accepted audit/declaration/planning authority. It verifies exact member order, context, symbol, source, components, outcome, style, grouping and executable selection before publishing context/tools or sending a provider request. A valid same-snapshot symbol added only by request JSON is unauthorized. Nested instruction stacks are independently rediscovered and checked against each member's map.

`grouping`, when available from the trusted declaration owner, contains `projectIdentity`, `containingTypeSymbolRef` and `memberFamilySha256`. Direct members share a containing root; overloads additionally share a family. A named type is its own root, nested types have independent roots, and compilation identities remain distinct. These hints grant no additional read or result authority.

Each entry's `target` object contains:

- the current Core `SymbolRef` (`compilationContextRef` and `documentationCommentId`);
- one source commitment containing a current evidence-locator shape and exact `contentSha256`;
- the ordered applicable-component set using the current M2 `typeParameter`, `parameter`, `return`, and `value` component semantics.

Applicable-component identities are unique and strictly ordinally increasing. Named parameter and type-parameter components carry their exact name; return and value components do not. The source commitment is data used for later freshness validation. It is not an M2 patch locator, edit kind, write handle, or rendering instruction.

The reused M1/M2 identity domains are exact rather than Scribe-local aliases. Compilation or assembly references are 1–128 lowercase ASCII alphanumeric, dot, underscore, or hyphen characters and begin with a lowercase alphanumeric character. Documentation comment IDs are 3–1024 XML-valid scalars, begin with one of `T:`, `M:`, `P:`, `F:`, `E:`, or `N:`, and contain no control scalar. Source-generator locator IDs are `sgp.`/`sgo.` plus lowercase SHA-256; tool-generated IDs are `tgp.`/`tgo.` plus lowercase SHA-256. Type-parameter and parameter identities are exactly `type-parameter/<canonical ordinal>` and `parameter/<canonical ordinal>`; return/value identities are exactly `return` and `value`. Names are non-empty XML-valid text of at most 128 scalars. The set cannot contain both return and value, duplicate identities, or duplicate names within one named kind.

### Style Profile

Each nonnull manifest `styleProfile` contains only machine-visible input needed by the current producer-consumer path:

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

Context references are prompt/context inputs, not claim evidence, and proposal units cannot cite their IDs. Every pool row must be mapped by at least one manifest entry. A member's unique `contextReferenceIds` retain root-to-leaf instruction priority (nearest last), independently of global pool ID order. Reusing content never applies an unmapped instruction to another member.

`evidenceReferences` is a strictly ID-ordered bounded list. Each row carries:

- the exact current repository-context identity;
- a current target or applicable-component `EvidenceSubject`;
- the current M1 `EvidenceKind`, `EvidenceRelation`, and `EvidenceLocator` semantics;
- one closed Scribe authority;
- exact content SHA-256 and byte-count/truncation facts;
- a non-empty ordered list of claim categories the evidence may support.

No source excerpt is part of the request artifact. The run-time read ports may return bounded typed content, but complete tool arguments/results and excerpts do not enter the canonical or publishable result.

Every evidence authority must agree with its existing evidence kind: implementation, declaration/attribute, existing XML documentation, test, repository documentation, and public contract map to their corresponding authority. Every evidence subject must be one manifest target or one of its exact applicable components. Each evidence row has exactly one owning manifest entry and is listed in that member's `evidenceReferenceIds`; cross-subject, dangling or unmapped rows fail closed. Claims are checked against that owner's style. Every evidence/context row must carry the current request repository context. `includedUtf8ByteCount` cannot exceed `originalUtf8ByteCount`, and `isTruncated` is true exactly when included bytes are fewer.

`evidenceConflicts` contains only the relation `evidence-conflict.higher-authority-contradicts`. A row names two request-visible evidence IDs with the same exact subject, and the declared higher row must have strictly greater registered authority. Conflict rows are unique and strictly ordered by `(higher ID, lower ID)`. This explicit input is the only deterministic lower-authority conflict rule; the validator does not infer contradictions from prose.

### Tool policy and limits

`toolPolicyId` identifies the closed tool policy selected by the caller. It does not grant an operation or carry a provider tool schema.

`limits` contains independent ceilings for attempts, context references and included bytes, evidence references and included bytes, provider requests, tool rounds, tool calls, input tokens, uncached input tokens, output tokens, cost microunits, and elapsed milliseconds. Values fit the published numeric bounds. Attempt numbers are positive and cannot exceed `maximumAttempts`. Campaign callers inject the single checked invocation allowance shared by all targets; the stable outer request safety profile does not create a fresh per-target spending allowance. Standalone Runtime constructs an allowance from its validated standalone limits through the same accounting implementation. Caller cancellation remains first. A checked campaign Action or lifetime resource stop keeps its budget terminal when a request-safety clock also expires; standalone elapsed exhaustion keeps its timeout terminal even when the accounting clock started before Runtime. Price/rate authority remains with the campaign owner. The tool-call counter includes each actual registered tool invocation and each structured terminal submission; rejection before invocation increments neither.

Semantic queries require an explicit selected `targetId`; their enum and codec admit only targets successfully bound by the current method-only semantic port. Class/property metadata and source scopes remain in the complete manifest without advertising unsupported semantic calls. Repository operations use exact bound `scopeId` enums, including mapped context/evidence references and selected source scopes. A cursor remains bound to its target, scope, operation, query and snapshot. Lazy full-source reads can satisfy a complete-evidence claim; partial reads retain only policies that permit truncated evidence. Shared source bytes never widen the executable subset.

The request contains no M4 work-plan, campaign, branch, issue, pull-request, provider conversation, fallback, cursor, checkpoint, or publication identity.

## Scribe Run Result

`scribeRunResultVersion` is exactly `1`. Required fields are request/attempt correlation, `dynamicEvidenceReferences`, `submissions`, `targetFailures`, `stop`, `repairCount`, `nextSequence`, complete `coverage`, and `runEnvelope`. A submission contains `targetId`, `contentSha256`, `stage` and a proposal/skip `result`. The content hash is SHA-256 over recursively ordinal-key-sorted compact JSON; arrays retain their semantic order. A target has at most one immutable submission or failure row. Coverage contains every manifest member in original order, preserving its audit outcome.

### Incremental submission and finish

The model uses the existing `scribe.submit-terminal` operation with one of two closed payloads:

- `submit`: `kind`, exact `scribeRequestSha256`, positive `sequence`, and one to eight `items`, each containing only `targetId` and proposal/skip `result`;
- `finish`: only `kind` and exact `scribeRequestSha256`.

The sequence starts at one. An accepted batch advances it once; an in-conversation identical sequence/content replay returns its established acknowledgement without another persistence call. Changed replay, foreign/nonexecutable targets, duplicate targets, closed-target overwrites and out-of-order sequences cannot advance content. Items validate independently, so a rejected item does not discard valid siblings. A valid skip closes that target for the snapshot and remains unresolved. Invalid submissions consume a cumulative bound of three; the third ends repair. Retries and bound progress restoration never reset that count. Malformed JSON/UTF-8 may instead stop immediately with validation failure.

Acknowledgements are closed data with correlation, per-item disposition/stage, next sequence and repair count. They are replayed as actual tool results after the exact original assistant submission call; SDK correction data cannot duplicate them or elevate their authority. They grant no additional tools, selection, evidence or source authority. Accepted siblings and the original shared invocation/output/time allowance survive transient retry; a newly admitted retry retires only its private conversation epoch.

Stages distinguish `received`, `validated`, `durably-recorded` and `patch-accepted`. Core validates content; only the injected `IDocumentationScribeSubmissionHost` can report persistence or patch acceptance. The default path reports validation only. Content is retained as received before invoking the Host, because an interrupted callback does not prove rollback. A foreign/unknown receipt never advances that stage. Recovery containing a received item stops before model work until the Host reconciles it. Parsed stages remain data until the Host independently accepts their provenance; parsing alone grants no durability or publication authority. Fresh attempts restore only exact-bound structured documentation, typed cited evidence commitments and bounded progress; accepted/skipped/restored-failed targets cannot regenerate. They restore no evidence bodies, historical usage as new invocation consumption, conversation or continuation. Historical dispatch observations remain source-free Campaign lineage metadata.

`stop` is `finished`, `failure` or `cancelled`. Finished reasons are `completed`, `unresolved` or `context`. Completion requires every member to be originally compliant or have a validated-or-higher proposal. A model finish cannot conceal skips, failed, received or omitted members. Coverage reports `compliant`, acknowledgement stages, `skipped`, `failed` or `remaining`; submission stage is included even for skips. Context/resource cuts retain confirmed progress and remaining coverage without compaction. The full prospective result, coverage and bounded final observations must fit the 1 MiB result boundary before a potentially durable Host call.

The Runtime uses SolusAgent's existing candidate continuation channel; submissions continue the same normal conversation and explicit finish ends it. Campaign Host acceptance supports multiple original-manifest members within one shared invocation. Each member uses fresh Host postflight and checkpoint CAS/readback before a durable acknowledgement; correlated progress snapshots persist repair, sequence and target failures before continuation. CLI/M2 authorization independently checks the complete cumulative accepted/proposal set, current source and executable identity.

### Trusted dynamic evidence overlay

`dynamicEvidenceReferences` uses the same closed row, identifier, subject, locator, authority, claim-category, ordering, and repository-context namespace as request `evidenceReferences`. It contains only trusted dynamic rows cited by retained target submissions, including progress preserved under failure or cancellation. Uncited read results remain private conversation state.

Raw result JSON cannot authorize an overlay. `ParseRunResult` receives the exact parsed request, expected attempt, and a separately supplied trusted overlay; the embedded rows must equal that overlay exactly. Per-target evidence lookup uses the ordered union of request rows and the accepted overlay scoped to that exact member, so a model-provided ID without a trusted typed producer row is dangling evidence and fails closed.

The Agent passes a successful tool result to Core as bounded typed evidence inputs without a caller-selected evidence ID, repository-context identity, request identity, attempt identity, session identity, or content bytes. Core validates the subject, kind/relation/authority, locator, committed content SHA-256, exact original/included byte counts, truncation fact, and ordered claim categories against the request, supplies the current repository context, and derives the ID. A truncated repository or generated-output row is valid only with an exact span over its fully committed source.

The derived ID is `evidence.dynamic.` plus lowercase SHA-256 over a domain-separated canonical preimage containing only subject, kind, relation, authority, locator, content SHA-256, byte counts, truncation, and ordered claim-category IDs. Repository/request/attempt/session/call/cursor identities, time, and current culture do not participate.

Within one tool payload, a duplicate derived ID is a terminal tool-protocol failure. Against request evidence or previously charged dynamic evidence, exact row equivalence reuses the existing row; any differing metadata fails closed. `tool.outcome.complete` and `tool.outcome.incomplete` may add rows, `tool.outcome.unavailable` adds none, and failure, cancellation, timeout, or budget outcomes commit none.

The runtime publishes one complete tool round atomically: completed exchanges and active-overlay additions become visible together at the final priority checkpoint or neither becomes visible. Charged and observed state is deliberately separate and monotonic. A prevalidated round is counted when execution begins; each call is counted when invoked; and successfully validated dynamic identities, semantic bytes, and canonical successful-exchange bytes are charged as incurred. A later invalid result, collision, failure, timeout, cancellation, budget crossing, or final-checkpoint priority outcome discards only the buffered exchanges and current-round active additions, not those run-wide charges or public call/round observations. Each completed exchange exposes product-owned `evidenceReferences` separately from opaque `resultJson`, and deterministic provider history serializes those rows explicitly. A transient provider retry clears attempt-local exchanges and active read evidence before the next attempt, but retains confirmed structured progress, its cited typed evidence, tool observations and run-wide charged evidence identity/byte history; this prevents a retry from reclaiming work or evidence budget.

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

Only `scribe.failure.provider` also contains `providerFinalDisposition`, with exactly `retryable` or `terminal`. Every other failure and terminal forbids that property. The disposition summarizes the final provider terminal after bounded SDK retry admission and product-owned durable waits. `restoredRetryableProviderFailures` is a required bounded scalar identifying accepted transient failures restored from the same durable attempt. Current-execution physical counts remain separate: a provider failure requires at least one actual current call and `providerRequestCount >= attemptNumber - restoredRetryableProviderFailures`. The restored count is less than `maximumAttempts` and less than the current logical attempt number. `retryable` additionally requires `attemptNumber == maximumAttempts`; `terminal` is allowed at any otherwise valid attempt. For example, one current physical failure after one accepted restored failure can finish logical attempt 2. Higher-priority caller cancellation, actual provider timeout or invocation/lifetime exhaustion retains its own terminal.

Cancellation is a disjoint `cancelled` variant with `scribe.cancelled.caller` or `scribe.cancelled.shutdown`. A terminal object cannot combine fields from another variant.

## Run envelope

The envelope repeats the exact request SHA-256 and attempt identity and records only:

- provider and model configuration identities;
- Scribe protocol, tool-policy, and Style Profile identities;
- positive attempt number bounded by the request's `maximumAttempts`, and required `restoredRetryableProviderFailures` (zero for a fresh attempt);
- provider-request, tool-round, tool-call, and elapsed-time counts;
- independently optional usage, cache, and cost observations;
- a bounded ordered diagnostic list.

Tool policy must equal the request; `styleProfileIds` must equal its sorted unique executable-member style identities. Provider/tool counts observe only this execution and cannot exceed request safety limits. Optional usage fields observe input, cached input, uncached input, output, and reasoning tokens. One normalized physical response rejects a known partition larger than known input, a contradictory fully known input/cache/miss triple, or reasoning larger than known output. A multi-request envelope sums only present fields. Optional `usageCoverage` records the number of physical requests reporting input, output, cached input, uncached input, reasoning, cost and cache fields. A field is complete only when its value is present and its reporting count equals `providerRequestCount`; missing coverage means the reporting population is unknown. When coverage is supplied, a present cost (including zero) or a known cache observation requires a positive reporting count. `cache.not-reported` does not report a cache value and may have zero coverage. Missing usage is never zero, and conservative exposure is never measured usage. Complete input partitions must sum to input; complete reasoning remains an output subset. Cache is hit, miss, mixed, or not reported. Cost is a currency configuration identity plus non-negative microunits. The typed physical exchange boundary prepares the exact request commitment before admission and performs one gated send, followed by mandatory settlement before another request or retry. The campaign owner accounts each dispatch once; it does not recharge this aggregate envelope.

The request ceilings are execution controls, while the result schema's larger published maxima are artifact-safety bounds. Each configured token, cost, and elapsed maximum is strictly lower than its corresponding artifact-safety maximum, leaving a bounded range in which the result can preserve the overrun that caused termination. A normally finished result remains within configured ceilings; retained target content can coexist with a controlled stop that reports a bounded overrun. A budget failure may truthfully report token, cost or elapsed observations that crossed a configured ceiling. A checked shared Action or lifetime budget stop keeps budget priority when mandatory provider/Host closure also crosses the request-safety elapsed ceiling. A timeout failure may truthfully report the elapsed overrun and any simultaneous bounded token or cost overruns observed at the same reducer checkpoint. Cancellation may report either kind of already-incurred observation. Those observations must still remain within the artifact-safety maxima. This terminal-aware allowance preserves the facts that caused termination without granting permission for another attempt.

Diagnostics are stable code plus code-specific allowlisted metadata:

| Code | Stage | Additional field |
| --- | --- | --- |
| `scribe.diagnostic.provider-failure` | `provider` | none |
| `scribe.diagnostic.tool-failure` | `tool` | `referenceId` |
| `scribe.diagnostic.result-rejected` | `result` | `validationCode` |
| `scribe.diagnostic.runtime-failure` | `runtime` | none |

An observed typed tool failure may use the inert `referenceId` vocabulary `tool-failure.<operation>.<reason>`. Operation is one of `read-excerpt`, `list-files`, `search-text`, `get-target-evidence`, or `unknown`; it is selected from the reviewed registration, never copied from a provider call ID or arbitrary operation name. Reason is `argument-schema`, `invalid-request`, `invalid-cursor`, `scope-denied`, `stale`, `unsafe-object`, `invalid-encoding`, `invalid-result`, `result-encoding`, `result-canonical`, `evidence`, `release-rejected`, or `unknown`. At the original `read-excerpt` rejecting branch, a fixed enum may instead identify `invalid-request-shape`, `invalid-request-path`, `invalid-request-range`, `invalid-request-eof`, `invalid-request-excerpt-bounds`, or `invalid-request-evidence`; no input value or rejected content is retained. This metadata preserves the original typed `repository.invalid-request` code, terminal behavior and model-visible tool payload. The reason comes from the original typed result or rejecting boundary; a coarse `invalid-request` does not establish a more specific cause. The first observation, including an unknown reason, is immutable within that attempt; callbacks from an inactive tool round cannot replace it. Unclassified diagnostics remain unclassified. This reference does not grant evidence or tool authority and is not a new result field, persisted checkpoint component, or artifact version.

Only a final tool-protocol stop can project a known reference into the optional [closed CLI observation](../campaign-cli.md#bounded-tool-failure-observation). Cancellation, timeout, budget, failed settlement, and other primary outcomes do not gain that observation. A resumed compact checkpoint does not reconstruct discarded detail. This is a change to current draft diagnostic semantics, not a compatibility promise for earlier revisions.

The envelope and session stop contain no credential, complete prompt, tool argument/result, transcript, source excerpt, raw provider response or error, arbitrary exception text, hidden reasoning, absolute path, GitHub identity, or arbitrary metadata dictionary.

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

`IDocumentationScribeToolResult.FailureReasonCategory` is optional inert Core-owned diagnostic metadata with the closed `DocumentationScribeToolFailureReason` vocabulary above and default `Unknown`. Concrete repository and semantic results derive it from their existing typed failure and implement it explicitly, so their provider-visible JSON gains no property. It never changes outcome, admission, recovery, counters, evidence validation, or whether a failed tool result is returned to the model.

Operation results add only their bounded typed data, including optional ID-free dynamic-evidence inputs on complete or incomplete outcomes. Encoders do not accept an evidence-item count; the runtime derives semantic counts and encoded exchange bytes from the accepted typed values. The seam exposes no physical or absolute path, workspace/filesystem object, writer, mutable session, generic network object, credential, or mutation method. Repository-relative locator values are bounded data, not ambient filesystem authority. R1 defines no fake-only normative operation and does not freeze the later provider-visible tool inventory, names, JSON schemas, grouping, cursors, paging, or Roslyn mechanics.

## Stable validation categories

Request failures use `scribe.request.<category>` and result failures use `scribe.result.<category>` with an RFC 6901 pointer when a bounded location exists. A property-name segment is retained only when it is a bounded safe lexical name; otherwise the pointer stops at the containing object so rejected source or provider text cannot become diagnostic content. Current categories cover document size, BOM, UTF-8/JSON, duplicate property, unsupported version, shape, unknown field, vocabulary/identity, order, component, reference, stale/wrong subject, style, evidence, content, diagnostic, correlation, and budget failures.

The validator reports deterministic structured facts only. It never includes the rejected source, provider text, exception text, or other arbitrary input in a diagnostic.

## Campaign completion binding

M3 binding remains an immutable execution fact, but it does not independently authorize a Campaign State completion. For campaign proposal execution, Core issues one process-local, one-shot completion registrar from the exact accepted provider invocation. The registrar type and its creation, preparation-authorization, and registration members are internal to Core and cross only the explicit friend boundary to the X1-owning CLI assembly; an ordinary Core consumer cannot select an X1-only final fact or mint the opaque authority. X1's private campaign binder is the sole production consumer: it interprets the actual private prepared result, gates dispatch on the first underlying model `SendAsync`, and mints one opaque completion authority for coherent ordinary M3 and X1-only final facts. The campaign executor receives neither the registrar nor the prepared result or raw Run Result.

Before the first model send, an ordinary M3 result may complete only when its envelope records zero provider requests and its terminal is cancellation or validation, internal, timeout, or budget failure. An available proposal, skip, provider/tool failure, or any result claiming provider work is incoherent. X1 proposal-invalid or host facts may retire an available lifecycle conservatively only without an M3 outcome. After dispatch, ordinary completion requires the exact bound M3 outcome, while X1-only completion may use no M3 outcome or the exact retained proposal outcome. A pre-send cancellation, timeout, or exhaustion with no bound M3 result instead uses the campaign stop transition and leaves the work planned; abrupt loss or incoherent output leaves the reservation active for recovery. The X1 campaign binder revalidates exact request SHA and complete current request authority across its private byte reparse, runtime provider/model/protocol identity, and exact attempt without requiring cross-parse object identity.

The registrar, completion authority, prepared result, Run Result, host elapsed observation, and repository session are process-local and are never serialized. Incremental member starts and accepted content are checkpointed under that invocation's original Host permit and deadline. Terminal submit/finish select a separately configured finite Host ceiling (default300000ms), independent of physical provider requests. The original pre-admission start and remaining Runtime/invocation/lifetime/caller/owner authority bound the whole ordered member/progress operation; individual acknowledgements do not renew it. Repository operations likewise apply their configured finite bound at both outer and inner consumers. Configured checkpoint readback and at most30000ms settlement tail can observe already-started work but confer no new execution authority; see [operational timeout configuration](../consumer-configuration.md#operational-timeouts). An authoritative new Campaign retry reopens only eligible retryable members and their prior failed-target markers; cumulative repair/sequence bounds and durable siblings remain intact. A passive context rebind does not reopen closed targets. They do not consume the final completion registrar: the shared session settles its remaining Host interval and completes its lifecycle once. M2 independently reconstructs and validates the cumulative candidate; neither a member receipt nor final session completion grants M2, filesystem, credential, GitHub, or publication authority.

## Compatibility and non-claims

M1 Audit Result and M2 Documentation Patch remain current pre-release v1 drafts. Introducing this producer does not increment either family. Cross-contract tests bind reused symbol/profile/evidence-locator/component/content semantics, and the ordinary M1/M2 suites remain authoritative for those contracts.

Compatible unpublished Campaign checkpoints can restore exact original-manifest progress under fresh source/context/evidence authority without regenerating durable accepted members or charging historical dispatches again. This does not permit cross-snapshot proposal reuse, source/candidate bytes, hidden conversation continuation or automatic publication.

This draft does not claim provider compatibility, cache behavior, price stability, model quality, semantic evidence entailment, useful prose, production release support, persistence compatibility, migration, or publication authority. Those claims require their later executable evidence and gates.
