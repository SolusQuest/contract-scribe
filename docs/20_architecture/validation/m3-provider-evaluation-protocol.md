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

Selection means only that M3-P4 may test one explicitly named exact configuration. It is not fallback authority, a Kimi claim, or a broader provider-support claim. Real request acceptance, continuation/tool-loop behavior, proposal usefulness, latency, usage/cache reporting, and practical cost remain observations from the authorized exact-revision run.

The selection freezes the complete Scribe execution-control set: attempts; context and evidence counts and byte ceilings; provider requests, tool rounds, and tool calls; total and uncached input plus output token ceilings; maximum cost; and elapsed time. Its hash is emitted as `selectionIdentity`. Caller-reviewed pricing remains a separate runtime input identified by `costConfigurationIdentity`.

The complete live-eligible denominator for `deepseek-primary` is exactly `conflicting-evidence`, `patch-rejection`, and `useful-proposal`. The complete denominator for `mimo-compatibility` is exactly `useful-proposal`. Synthetic transport cases for structured skip, insufficient evidence, invalid tool/output, rate limit, unavailability, budget exhaustion, and timeout remain offline-only and cannot add paid requests or claim live evidence. Live reports separate declarative `intendedCoverage` from outcome-derived `observedCoverage`.

For a nonterminal thinking-mode tool response, the transport retains one bounded opaque assistant continuation containing exact `content` plus exact decoded `reasoning_content`. The runtime anchors it once to response index zero even when the response contains parallel calls and replays it only on the matching assistant message in later requests of the same attempt. Missing or null required `reasoning_content` fails closed; a present empty string is replayable. Attempt restart and every terminal run boundary discard it. Continuation shares the existing 1 MiB normalized-response and 32 MiB logical-request ceilings and never enters product messages, evidence, tools, proposals, patches, diagnostics, logs, durable state, or reports. Evaluation sees only the closed observations `continuation.observed`, `continuation.history-replayed`, and `continuation.missing-required`.

## Live invocation and safety gate

Live execution must launch the already-built artifact directly. Do not use `dotnet run`, `dotnet test`, restore, or build with a credential present.

First execute the selected configuration's manifest-designated safety case. For DeepSeek:

```text
set CONTRACTSCRIBE_EVALUATION_API_KEY=<operator-injected-value>
dotnet tools/ContractScribe.Evaluation/bin/Release/net10.0/ContractScribe.Evaluation.dll --live --safety-gate --corpus tests/fixtures/documentation-scribe/evaluation --configuration deepseek-primary --endpoint https://api.deepseek.com/chat/completions --model deepseek-v4-flash --secret-env CONTRACTSCRIBE_EVALUATION_API_KEY --output <ignored-directory-below-os-temp> --currency <currency-id> --cached-input-rate <microunits-per-million> --uncached-input-rate <microunits-per-million> --output-rate <microunits-per-million>
```

For MiMo, use a separately authorized secret/output/cost invocation and replace the configuration-specific arguments with `--configuration mimo-compatibility --endpoint https://api.xiaomimimo.com/v1/chat/completions --model mimo-v2.5`.

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

Live output requires an operator-selected physical directory below the operating-system temporary root. Symlink, junction, reparse, non-temporary destinations, prior evaluation report files, and any equality or ancestor/descendant overlap with the checkout, caller corpus, prepared corpus, or analyzed repository are rejected before publication or provider invocation. The same physical non-overlap check runs immediately before every same-directory atomic replacement. A partial allowlisted report names the active case before its initialization and is updated after each disposition; only the selected denominator can become complete, and successful final publication removes the partial file. Partial, interrupted, canceled, timeout, budget, rate-limit, malformed, unavailable, validation, and M2 outcomes stay explicit and are never called passed or compatible. Every provider response failure is retained as an ordered request number plus one closed `model.failure.*` classification in `providerFailures`, including a transient failure followed by a successful retry. Continuation behavior contributes only closed observation IDs; provider prose, raw responses, assistant content, and reasoning content remain excluded.

The report allowlist contains corpus/selection/cost/source identities; bounded case IDs, status codes, counts, usage/cache/cost completeness, case-level expectation status and difference IDs, and live elapsed time; proposal-validation and M2 outcomes; and production-validated bounded content units with claim category and sorted evidence IDs for local human review. Offline differences cover the frozen outcome and observation oracle rather than treating deterministic byte equality or declarative input-coverage labels as semantic conformance.

Reports exclude credentials, private source, complete prompts, complete tool transcripts, raw requests/responses, hidden reasoning, complete diffs, absolute paths, exception messages/graphs/stacks, and unvalidated provider prose. A fail-closed scan covers the credential fingerprint, exact checkout/source/prepared/output paths, general POSIX rooted paths regardless of prose or Markdown delimiter, Windows drive roots, backslash and forward-slash UNC/device roots, decoded JSON-escaped forms, bearer/header tokens, forbidden payload names, and the hostile fixture marker. URI schemes such as `https://` remain distinct from filesystem roots. Detection suppresses the new report payload and emits only a bounded failure code.

M3-P4 owns human review of proposal usefulness, unsupported claims, material quality failures, request/tool/terminal behavior, usage/cache/latency/cost observations, and exact-revision Issue evidence. Nothing in the evaluator edits tracked files, posts to GitHub, or copies live output into the repository.
