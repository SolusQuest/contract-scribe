# M3 provider evaluation protocol

## Status and boundary

`ContractScribe.Evaluation` is explicit-opt-in local evaluation infrastructure for the M3-P3 to M3-P4 handoff. It exercises the accepted production loader, classifier, documentation observer, policy evidence, audit authority, context bootstrap, closed tools, Agent runtime, provider transport, proposal validator, and M2 patch engine. It is not a product command, correctness oracle, benchmark, provider endorsement, compatibility promise, release gate, durable campaign system, or authorization to publish provider output.

The checked-in corpus is public synthetic material. The in-process Roslyn/MSBuild load is trusted-input evaluation, not a sandbox or isolation boundary for arbitrary repositories. Live evaluation of an external or reduced-trust repository requires a separately reviewed credential-free external isolation design.

## Credential-free preparation and offline replay

Restore and build the complete solution without any provider credential:

```text
dotnet restore ContractScribe.slnx
dotnet build ContractScribe.slnx --configuration Release --no-restore
```

The Evaluation build target copies the hash-committed corpus into a worktree-specific directory below the operating-system temporary root, restores and builds that isolated pure-SDK fixture without a credential, and writes its location beside the Evaluation artifact. This preparation is part of credential-free build, not live execution. The runtime validates that the prepared copy has the same complete corpus identity as the operator-selected checked-in corpus.

Run the deterministic offline corpus without a secret or network:

```text
dotnet run --project tools/ContractScribe.Evaluation/ContractScribe.Evaluation.csproj -- --offline --corpus tests/fixtures/documentation-scribe/evaluation
```

Omitting `--offline` selects the same offline default. Offline mode uses deterministic model exchanges but still traverses production composition, real closed tools, terminal validation, proposal projection, and M2. Its eleven cases freeze one exact status and code each, including a production-runtime timeout. Each case also freezes a separate closed offline observation oracle: attempt/request/tool counts, proposal-validation presence and exact expected line, exact synthetic usage/cache fields when present, and the exact allowlisted scripted observation IDs. These are distinct from declarative input-coverage labels. Every mismatch becomes a bounded case-level `differenceId`, increments the aggregate difference count, and makes offline execution fail. Required Ubuntu validation must match all eleven. The Linux-only candidate workspace means Windows records the useful proposal as `platform-not-observed` rather than treating its bounded host failure as an accepted semantic outcome; its remaining offline observations must still match. A true exact-platform or offline-observation difference makes the command fail. The persisted report uses `latency.status = not-measured`; complete report bytes must remain stable and semantically conforming across fresh processes, working directories, cultures, and time zones.

## Frozen live selection

`tests/fixtures/documentation-scribe/evaluation/selection.json` freezes two exact configuration-bound profiles, both with evidence date `2026-08-21`:

| Configuration | Endpoint and model | Request policy | Live denominator | Safety gate |
| --- | --- | --- | --- | --- |
| `deepseek-primary` | `https://api.deepseek.com/chat/completions`, `deepseek-v4-flash` | thinking enabled, `reasoning_effort: high`, omit `tool_choice`, `max_tokens` | `conflicting-evidence`, `patch-rejection`, `useful-proposal` | `useful-proposal` |
| `mimo-compatibility` | `https://api.xiaomimimo.com/v1/chat/completions`, `mimo-v2.5` | thinking enabled, no reasoning-effort field, `tool_choice: auto`, `max_completion_tokens` | `useful-proposal` only | `useful-proposal` |

Both profiles omit sampling overrides, `parallel_tool_calls`, and `n`. The shared parser still accepts multiple validated tool calls in one assistant response. The official [DeepSeek thinking-mode guide](https://api-docs.deepseek.com/guides/thinking_mode/), [DeepSeek Chat Completions reference](https://api-docs.deepseek.com/api/create-chat-completion/), [MiMo pass-back guide](https://mimo.mi.com/docs/en-US/usage-guide/passing-back-reasoning_content), and [MiMo OpenAI-compatible API reference](https://mimo.mi.com/docs/en-US/api/chat/openai-api) define the selected request and continuation fields.

Every repository tool definition binds `scopeId` to the sorted closed enum of request-visible opaque scope IDs authorized for that exact operation. A model never has to infer whether a source, maintained-context, or instruction scope permits read, list, or search. Read bounds are omitted as a pair unless an exact range is known; list/search omit `subdirectory` to query the selected scope root unless an exact in-scope repository-relative directory is already known, and their page sizes expose the production maximum of 64. The `useful-proposal` synthetic instruction is narrower still: its exact-file search uses only the named `scopeId` and `literal`, and omits `subdirectory`, `pageSize`, `cursor`, and every other optional argument. The terminal prompt separately names the exact proposal and skip root-property sets, requires proposal `target` to be the exact object supplied as `terminalTarget`, identifies `componentIdentity` as the content-unit field, marks `applicableComponents` and `identity` as guidance-only, and requires proposal evidence references to remain inside their content units rather than at the terminal root. These provider-facing constraints do not relax the production decoders or Core terminal validation; output outside the closed tool or terminal contract still fails closed.

Selection means only that M3-P4 may test one explicitly named exact configuration. It is not fallback authority, a Kimi claim, or a broader provider-support claim. Real request acceptance, continuation/tool-loop behavior, proposal usefulness, latency, usage/cache reporting, and practical cost remain observations from the authorized exact-revision run.

The selection freezes the complete Scribe execution-control set: attempts; context and evidence counts and byte ceilings; provider requests, tool rounds, and tool calls; total and uncached input plus output token ceilings; maximum cost; and elapsed time. Its hash is emitted as `selectionIdentity`. Caller-reviewed pricing remains a separate runtime input identified by `costConfigurationIdentity`.

The complete live-eligible denominator for `deepseek-primary` is exactly `conflicting-evidence`, `patch-rejection`, and `useful-proposal`. The complete denominator for `mimo-compatibility` is exactly `useful-proposal`. Synthetic transport cases for structured skip, insufficient evidence, invalid tool/output, rate limit, unavailability, budget exhaustion, and timeout remain offline-only and cannot add paid requests or claim live evidence. Live reports separate declarative `intendedCoverage` from outcome-derived `observedCoverage`.

The selected configuration's `expectedObservations` are closed predicates, not literal members of `observedCoverage`. Evaluation records configuration-execution-level `observationExpectationStatus` and `missingExpectedObservationIds` separately from each scenario's outcome `expectationStatus`. Each case retains its own `unexpectedProtocolObservationIds`. A safety gate evaluates the selected case; `--all` evaluates the complete selected configuration denominator. A partial or interrupted execution is `not-evaluable`, never matched. The exact predicates are:

- `continuation.observed` and `continuation.history-replayed` require those literal facts somewhere in the selected execution;
- `request.accepted-or-bounded-provider-failure` requires at least one parsed response, received HTTP status, successful-response metadata/body/codec failure, or before-response transport failure after dispatch was attempted; local request preparation does not satisfy it;
- `tool-call-or-terminal` requires a parsed ordinary tool call or terminal submission;
- `tool-result-continuation-when-requested` requires history replay for every completed ordinary tool round that caused another provider request;
- `usage-fields-when-supplied` and `cache-fields-when-supplied` require every supplied observation to survive in its corresponding bounded case report;
- `validated-proposal-or-structured-skip-or-bounded-failure` requires every applicable runtime execution to end in a Core-validated proposal, structured skip, or `DocumentationScribeFailureTerminal`; preflight-only cases are excluded and cancellation never satisfies this predicate.

The unexpected provider-protocol vocabulary is limited to `continuation.missing-required`, `request.preparation-rejected`, `response.malformed`, `tool-protocol.rejected`, and `terminal-validation.rejected`. Case status/code, intended coverage, cancellation, and other observation IDs are not reclassified as unexpected protocol facts. A direct valid terminal remains legal but does not satisfy the two literal continuation predicates.

The `useful-proposal` safety case intentionally adds one narrower synthetic repository instruction: before a documentation proposal for `M:EvaluationCorpus.Fixture.Run`, the model must make exactly one call to the provider-visible repository capability described as searching for one ordinal literal, using exactly `scopeId` `evidence.source` and `literal` `public void Run` and no optional arguments. The rule does not apply to a structured skip or any other target, and it names no internal operation ID because ordinary tools use opaque provider aliases. The manifest separately freezes the evaluation-only internal operation, complete two-property argument shape and values, and exact call count. Once an accepted ordinary-tool or terminal response makes the call observable, matching is independent of whether the later tool execution or proposal succeeds: this lets an over-budget optional argument remain closed negative evidence rather than `not-evaluable`. A response that never yields an accepted tool or terminal remains `not-evaluable`. The report records `safetyToolExpectationStatus` and closed count, operation, argument-shape, scope, and literal difference IDs without retaining or publishing provider-supplied operation IDs or arguments. The target is structurally empty and the exact quality oracle is `Performs no operation.`. A direct terminal remains legal for the general product path, but it does not satisfy this case's tool/continuation observation contract.

Changing this shared fixture or its oracle creates a new `corpusIdentity`; unchanged `selection.json` preserves `selectionIdentity`. Provider observations remain bound to the corpus identity they executed. Earlier DeepSeek and MiMo observations are historical evidence for the prior corpus, not execution of the corrected corpus. A DeepSeek-only corrected rerun records MiMo as not executed for that corpus and keeps the MiMo compatibility claim withheld unless a separately authorized later run supplies narrower evidence.

For a nonterminal thinking-mode tool response, the transport retains one bounded opaque assistant continuation containing exact `content` plus exact decoded `reasoning_content`. The runtime anchors it once to response index zero even when the response contains parallel calls and replays it only on the matching assistant message in later requests of the same attempt. Missing or null required `reasoning_content` fails closed; a present empty string is replayable. Attempt restart and every terminal run boundary discard it. Continuation shares the existing 1 MiB normalized-response and 32 MiB logical-request ceilings and never enters product messages, evidence, tools, proposals, patches, diagnostics, logs, durable state, or reports. Evaluation sees only the closed observations `continuation.observed`, `continuation.history-replayed`, and `continuation.missing-required`.

## Live invocation and safety gate

Live execution must launch the already-built artifact directly. Do not use `dotnet run`, `dotnet test`, restore, or build with a credential present.

When a live run discovers a production or harness correction, the claim-bearing rerun occurs only after one reviewed correction is unchanged and reachable from `main`, required GitHub-hosted Ubuntu x64 CI passes for the exact revision, and a native-Linux checkout completes credential-free Release build and offline preflight. Fresh operator authorization names that exact revision, current corpus/selection identities, provider/model/endpoint, caller pricing and remaining budget, credential source, Linux temporary output, and public-evidence intent. DeepSeek and MiMo safety gates are separately authorized. DeepSeek `--all` requires a separate authorization after human review of its passing safety report. Windows may provide credential-free development feedback but does not run an avoidable paid gate for the Linux-only M2 acceptance path.

First execute the selected configuration's manifest-designated safety case. For DeepSeek:

```text
set CONTRACTSCRIBE_EVALUATION_API_KEY=<operator-injected-value>
dotnet tools/ContractScribe.Evaluation/bin/Release/net10.0/ContractScribe.Evaluation.dll --live --safety-gate --corpus tests/fixtures/documentation-scribe/evaluation --configuration deepseek-primary --endpoint https://api.deepseek.com/chat/completions --model deepseek-v4-flash --secret-env CONTRACTSCRIBE_EVALUATION_API_KEY --output <ignored-directory-below-os-temp> --currency <currency-id> --cached-input-rate <microunits-per-million> --uncached-input-rate <microunits-per-million> --output-rate <microunits-per-million>
```

For MiMo, use a separately authorized secret/output/cost invocation and replace the configuration-specific arguments with `--configuration mimo-compatibility --endpoint https://api.xiaomimimo.com/v1/chat/completions --model mimo-v2.5`.

Private MiMo response diagnosis is a separate, non-claim-bearing invocation. It is accepted only with `--live --safety-gate` on Linux x64 and adds `--unsafe-capture-provider-response <new-absolute-direct-child-of-os-temp>`. The private normal-output directory is also a new direct child of that root. Other architectures fail before credential acquisition because the native physical-identity ABI is deliberately closed to the reviewed x64 layout. The normal `--model` remains the frozen `mimo-v2.5`. One reviewed comparator may additionally specify exact `--diagnostic-model mimo-v2.5-pro`; it inherits the frozen `mimo-compatibility` endpoint, request profile, safety case, limits, selection identity, and caller pricing. Normal selection still rejects Pro, and no other diagnostic override is accepted. The report identifies the actual model and a distinct deterministic diagnostic configuration while retaining the frozen selection separately; `executionPurpose` is `private-response-diagnostic` and `fullCorpusComplete` is always false.

The capture factory retains a no-follow descriptor for the physical operating-system temporary root, rejects any existing final entry with no-follow metadata inspection, and creates the one-component capture directory with an exclusive relative `mkdirat`. It retains the capture descriptor, compares both inode/device identity and Linux mount identity with the temporary root, and repeats root/capture identity checks before each write and publication. A same-device bind mount, root or capture replacement, dangling link, special entry, or create collision therefore fails closed. Response files are created relative to the retained capture descriptor with `0600`, written only after the `0700` directory boundary exists, and published atomically without overwrite. A deterministic invalid path is rejected before credential capture; a final setup race makes zero provider calls and removes directories created by that attempt.

Before either paid diagnostic launch, the exact pushed executable must pass required GitHub-hosted Ubuntu x64 CI and same-session exact-head code/security review. Fresh operator authorization must then bind the exact SHA, endpoint, actual model, inherited profile, one safety-gate launch with at most eight in-run provider requests, current rates and remaining aggregate budget, credential source, and unique normal-output and capture directories. A correction invalidates that launch authorization. The operator launches the already-built exact-head artifact, examines the private bytes only locally, reduces them to closed facts, deletes the complete capture directory immediately, confirms that deletion, and publishes no raw material or path.

The safety gate executes exactly the selected configuration's `safetyGateCaseId` to a terminal disposition under its normal provider-request and tool-call limits. It can make request 1, execute a real tool, and make request 2 before terminal completion. DeepSeek then stops before case 2 and reports `fullCorpusComplete = false`. MiMo's one-case gate is its complete selected denominator, so successful completion reports `fullCorpusComplete = true` and does not authorize a second MiMo case or the DeepSeek configuration.

After human inspection of that local report, M3-P4 may execute the full selected corpus only under its separate authorization:

```text
set CONTRACTSCRIBE_EVALUATION_API_KEY=<operator-injected-value>
dotnet tools/ContractScribe.Evaluation/bin/Release/net10.0/ContractScribe.Evaluation.dll --live --all --corpus tests/fixtures/documentation-scribe/evaluation --configuration deepseek-primary --endpoint https://api.deepseek.com/chat/completions --model deepseek-v4-flash --secret-env CONTRACTSCRIBE_EVALUATION_API_KEY --output <ignored-directory-below-os-temp> --currency <currency-id> --cached-input-rate <microunits-per-million> --uncached-input-rate <microunits-per-million> --output-rate <microunits-per-million>
```

Full mode starts again from case 1. There is no checkpoint, resume ledger, or cache-derived correctness state.

## Credential lifetime and execution restrictions

Argument parsing reads only enough syntax to identify live mode and the named environment variable. The harness reads the named value, immediately removes that environment entry, creates a non-serializable fingerprint for fail-closed output scanning, and transfers the value once into the existing authenticated HTTP transport. Credential, transport option, marker, and outcome objects have bounded non-secret formatting.

After credential acquisition the evaluator contains no process-launch, shell, Git, build, restore, test, or external MSBuild API. The already-prepared corpus is loaded by the reviewed in-process production loader only after the environment entry is absent. The secret is never an argument, prompt field, report field, exception message, or child environment value.

Missing opt-in, selector, exact configuration/endpoint/model match, secret name/value, complete caller cost configuration, or confined output directory fails before any provider request. Live mode requires currency plus all three rates; only offline replay can be unpriced. One configuration's gate or credential does not authorize the other configuration.

The executable installs the same cooperative Ctrl+C/SIGTERM lifetime pattern used by the product CLI and passes that token through repository preparation, the production composition, provider transport, tools, patching, and report publication. A handled signal persists the active case as canceled in the partial report, suppresses the complete report, and exits with a bounded error code.

## Cost observations

Prices are caller input and are not committed as current facts. Rates are integer microunits per one million cached input, uncached input, and output tokens. The effective policy is hashed into the report identity.

For each provider response, both input splits are used when supplied and any residual total is conservatively uncached. Cached plus total derives uncached residual. Uncached plus total prices the entire total as uncached. Total without splits is all uncached. Both splits without total form a complete input partition; one split without total is partial. Output is priced separately and reasoning tokens are not added again. Contradictions fail closed.

Products and sums use checked `Int128`; the combined response numerator is ceiling-divided once by one million. Only a complete input partition plus output count creates production runtime cost authority. Partial and not-reported observations remain report-only. Response costs and run aggregation are checked against the product maximum and require one currency.

## Report and sanitization contract

Live output requires an operator-selected physical directory below the operating-system temporary root. Symlink, junction, reparse, non-temporary destinations, prior evaluation report files, and any equality or ancestor/descendant overlap with the checkout, caller corpus, prepared corpus, or analyzed repository are rejected before publication or provider invocation. The same physical non-overlap check runs immediately before every same-directory atomic replacement. A partial allowlisted report names the active case before its initialization and is updated after each disposition; only the selected denominator can become complete, and successful final publication removes the partial file. Partial, interrupted, canceled, timeout, budget, rate-limit, malformed, unavailable, validation, and M2 outcomes stay explicit and are never called passed or compatible.

Every provider response failure is retained in `providerFailures` as an ordered request number, one closed `model.failure.*`, an optional closed origin, and an optional numeric HTTP status. Origins are request preparation, received HTTP status, before-response transport, successful-response metadata/body handling, and response codec. Status is present if and only if a non-success HTTP response was received. The transport never reads, hashes, classifies, or retains a non-success body. The report also projects only the Core-validated runtime diagnostic rows: code, stage, optional tool `referenceId`, and optional result `validationCode`. It excludes validation pointers, provider request IDs, response headers, exception type/message/stack, body length/hash/content, and provider prose. Cost decoration preserves these facts unchanged. Continuation behavior contributes only closed observation IDs; provider prose, raw responses, assistant content, and reasoning content remain excluded.

Each case also contains `providerResponses`. Offline and preflight-only cases use `[]`. Each row contains exactly an integer `providerRequestNumber` and one `codecDisposition`; request numbers are strictly increasing, unique, between 1 and the selected maximum (currently 8), and have at most one row per request. Codec rows are a subset of provider requests, so row count is never used as provider request count. There is no row for request preparation, before-response transport, non-success status, encoded/non-JSON/declared-oversize responses, bounded-read overflow or truncation, or unreadable bodies. Closed rows already selected before cancellation or a local diagnostic/capture failure remain in the failed or canceled partial case when normal report persistence remains available. The partial case also retains actual attempt/provider-request/tool counts and all already-available provider failures, runtime diagnostics, usage/cache, caller-priced cost, proposal, and observed coverage. Only the case status/code becomes the local failure disposition; local failures never create `providerFailures`.

The complete first-disposition allowlist, in production evaluation order, is:

1. `codec.accepted-tool`
2. `codec.accepted-terminal`
3. `codec.response.exceeds-limit`
4. `codec.json.invalid`
5. `codec.json.duplicate-property`
6. `codec.root.invalid`
7. `codec.choices.invalid`
8. `codec.choice.index.invalid`
9. `codec.message.invalid`
10. `codec.message.role.invalid`
11. `codec.message.content.invalid`
12. `codec.message.thinking-content.invalid`
13. `codec.message.refusal.invalid`
14. `codec.tool-calls.invalid`
15. `codec.tool-call.index.invalid`
16. `codec.tool-call.envelope.invalid`
17. `codec.tool-call.alias.invalid`
18. `codec.arguments.invalid`
19. `codec.terminal.mixed`
20. `codec.usage.invalid`
21. `codec.finish-reason.unsupported`
22. `codec.finish-reason.inconsistent`
23. `codec.terminal.arguments-exceeds-limit`
24. `codec.continuation.missing`
25. `codec.tool-calls.exceeds-limit`
26. `codec.tool-call.arguments-exceeds-limit`

`codec.response.exceeds-limit` covers both the direct raw codec ceiling and a structurally valid response whose combined normalized representation exceeds the normalized aggregate ceiling. If multiple defects exist, only the first check reached in the production parser is recorded. The thinking-content identifier deliberately avoids reproducing the provider wire-field spelling that the report scanner forbids.

The report allowlist contains corpus/selection/cost/source identities; bounded case IDs, status codes, counts, usage/cache/cost completeness, case-level expectation status and difference IDs, and live elapsed time; proposal-validation and M2 outcomes; and production-validated bounded content units with claim category and sorted evidence IDs for local human review. Offline differences cover the frozen outcome and observation oracle rather than treating deterministic byte equality or declarative input-coverage labels as semantic conformance.

Reports exclude credentials, private source, complete prompts, complete tool transcripts, raw requests/responses, hidden reasoning, complete diffs, absolute paths, exception messages/graphs/stacks, and unvalidated provider prose. A fail-closed scan covers the credential fingerprint, exact checkout/source/prepared/output paths, general POSIX rooted paths regardless of prose or Markdown delimiter, Windows drive roots, backslash and forward-slash UNC/device roots, decoded JSON-escaped forms, bearer/header tokens, forbidden payload names, and the hostile fixture marker. URI schemes such as `https://` remain distinct from filesystem roots. Detection suppresses the new report payload and emits only a bounded failure code.

M3-P4 owns human review of proposal usefulness, unsupported claims, material quality failures, request/tool/terminal behavior, usage/cache/latency/cost observations, and exact-revision Issue evidence. Before posting or closing, that review compares every bounded content unit with its cited synthetic evidence, records evidence-support and material-quality judgments, confirms exact proposal validation and the accepted M2 candidate, reconciles missing expected and unexpected protocol observations, lists withheld claims, and rechecks report/comment sanitization. A failed review may post bounded negative evidence but leaves the Issue open and does not authorize an unchanged repeat. Nothing in the evaluator edits tracked files, posts to GitHub, or copies live output into the repository.
