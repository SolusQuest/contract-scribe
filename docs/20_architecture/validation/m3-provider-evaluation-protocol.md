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

Omitting `--offline` selects the same offline default. Offline mode uses deterministic model exchanges but still traverses production composition, real closed tools, terminal validation, proposal projection, and M2. Its persisted report uses `latency.status = not-measured`; complete report bytes must remain stable across fresh processes, working directories, cultures, and time zones.

## Frozen live selection

The sole M3-P4 target is recorded in `tests/fixtures/documentation-scribe/evaluation/selection.json`:

- Endpoint: `https://api.openai.com/v1/chat/completions`
- Model snapshot: `gpt-4.1-mini-2025-04-14`
- Evidence date: `2026-08-21`

The official [GPT-4.1 mini model record](https://developers.openai.com/api/docs/models/gpt-4.1-mini) identifies the exact snapshot, Chat Completions and function-calling support, and operation without a reasoning step. The official [Chat Completions reference](https://developers.openai.com/api/reference/cli/resources/chat/subresources/completions) defines function tools, required tool selection, assistant tool calls, correlated tool-result messages, parallel tool calls, and non-streaming completions. That is the smallest current documented shape matching the fixed production codec; the evaluator does not add a provider-specific branch or a fallback matrix.

Selection means only that M3-P4 may test this exact configuration. Real request acceptance, tool-loop behavior, field availability, proposal usefulness, latency, cache reporting, and practical cost remain observations from the authorized exact-revision run.

## Live invocation and safety gate

Live execution must launch the already-built artifact directly. Do not use `dotnet run`, `dotnet test`, restore, or build with a credential present.

First execute the manifest-designated safety case:

```text
set CONTRACTSCRIBE_EVALUATION_API_KEY=<operator-injected-value>
dotnet tools/ContractScribe.Evaluation/bin/Release/net10.0/ContractScribe.Evaluation.dll --live --safety-gate --corpus tests/fixtures/documentation-scribe/evaluation --endpoint https://api.openai.com/v1/chat/completions --model gpt-4.1-mini-2025-04-14 --secret-env CONTRACTSCRIBE_EVALUATION_API_KEY --output <ignored-directory-below-os-temp> --currency <currency-id> --cached-input-rate <microunits-per-million> --uncached-input-rate <microunits-per-million> --output-rate <microunits-per-million>
```

The safety gate executes exactly `manifest.safetyGateCaseId` to a terminal disposition under its normal provider-request and tool-call limits. It can make request 1, execute a real tool, and make request 2 before terminal completion; it stops before initializing or sending any request for corpus case 2. The report records denominator 1, actual request/tool counts, `executionPurpose = safety-gate`, and `fullCorpusComplete = false`.

After human inspection of that local report, M3-P4 may execute the full selected corpus only under its separate authorization:

```text
set CONTRACTSCRIBE_EVALUATION_API_KEY=<operator-injected-value>
dotnet tools/ContractScribe.Evaluation/bin/Release/net10.0/ContractScribe.Evaluation.dll --live --all --corpus tests/fixtures/documentation-scribe/evaluation --endpoint https://api.openai.com/v1/chat/completions --model gpt-4.1-mini-2025-04-14 --secret-env CONTRACTSCRIBE_EVALUATION_API_KEY --output <ignored-directory-below-os-temp> --currency <currency-id> --cached-input-rate <microunits-per-million> --uncached-input-rate <microunits-per-million> --output-rate <microunits-per-million>
```

Full mode starts again from case 1. There is no checkpoint, resume ledger, or cache-derived correctness state.

## Credential lifetime and execution restrictions

Argument parsing reads only enough syntax to identify live mode and the named environment variable. The harness reads the named value, immediately removes that environment entry, creates a non-serializable fingerprint for fail-closed output scanning, and transfers the value once into the existing authenticated HTTP transport. Credential, transport option, marker, and outcome objects have bounded non-secret formatting.

After credential acquisition the evaluator contains no process-launch, shell, Git, build, restore, test, or external MSBuild API. The already-prepared corpus is loaded by the reviewed in-process production loader only after the environment entry is absent. The secret is never an argument, prompt field, report field, exception message, or child environment value.

Missing opt-in, selector, endpoint/model match, secret name/value, caller cost configuration, or confined output directory fails before any provider request. The endpoint and model must exactly match the frozen selection.

## Cost observations

Prices are caller input and are not committed as current facts. Rates are integer microunits per one million cached input, uncached input, and output tokens. The effective policy is hashed into the report identity.

For each provider response, both input splits are used when supplied and any residual total is conservatively uncached. Cached plus total derives uncached residual. Uncached plus total prices the entire total as uncached. Total without splits is all uncached. Both splits without total form a complete input partition; one split without total is partial. Output is priced separately and reasoning tokens are not added again. Contradictions fail closed.

Products and sums use checked `Int128`; the combined response numerator is ceiling-divided once by one million. Only a complete input partition plus output count creates production runtime cost authority. Partial and not-reported observations remain report-only. Response costs and run aggregation are checked against the product maximum and require one currency.

## Report and sanitization contract

Live output requires an operator-selected physical directory below the operating-system temporary root. Symlink, junction, reparse, checkout, corpus, and non-temporary destinations are rejected. Reports are same-directory atomic replacements. A partial allowlisted report is written before the first case and after each disposition; only the selected denominator can become complete. Partial, interrupted, canceled, timeout, budget, rate-limit, malformed, unavailable, validation, and M2 outcomes stay explicit and are never called passed or compatible.

The report allowlist contains corpus/selection/cost/source identities; bounded case IDs, status codes, counts, usage/cache/cost completeness, and live elapsed time; proposal-validation and M2 outcomes; and production-validated bounded content units with claim category and sorted evidence IDs for local human review. Expected-line differences are bounded IDs.

Reports exclude credentials, private source, complete prompts, complete tool transcripts, raw requests/responses, hidden reasoning, complete diffs, absolute paths, exception messages/graphs/stacks, and unvalidated provider prose. A fail-closed scan covers the credential fingerprint, checkout/corpus/output paths, bearer/header tokens, forbidden payload names, and the hostile fixture marker. Detection suppresses the new report payload and emits only a bounded failure code.

M3-P4 owns human review of proposal usefulness, unsupported claims, material quality failures, request/tool/terminal behavior, usage/cache/latency/cost observations, and exact-revision Issue evidence. Nothing in the evaluator edits tracked files, posts to GitHub, or copies live output into the repository.
