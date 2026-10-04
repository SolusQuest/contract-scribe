# Test validation

Use this procedure to select validation from the failure surface changed by the work. Do not default to the complete solution merely because a file changed, and do not omit a contract test merely because the changed contract is stored as documentation or data.

## Suite definitions

- The **fast suite** is the complete `tests/ContractScribe.Tests/ContractScribe.Tests.csproj` project.
- The **integration suite** is the complete `tests/ContractScribe.IntegrationTests/ContractScribe.IntegrationTests.csproj` project.
- The **full test suite** is one applicable successful build followed by the complete fast-suite command and then the complete integration-suite command, without filters or skipped projects.
- **Full validation** is restore, a Release build, the full test suite, format verification, and the existing CLI help, version, and doctor smoke commands.
- Required CI may execute that same coverage as independent runner jobs. The current layout is the complete fast suite plus four integration partitions: the exact campaign crash-boundary method, the complete GitHub HTTP response-loss method, the other GitHub proposal methods, and their exact remaining complement. The union must preserve the unfiltered integration expanded full-name multiset, including Theory multiplicity.

## Select the narrowest sufficient command

The ranges below are evidence-based guidance for the current local Windows development host. They are developer feedback estimates, not platform-support evidence or performance gates. The outer budget is an observation budget for the original process, not a deadline that authorizes killing and retrying it.

### Default local execution path

Use the existing native-Windows workspace by default for the applicable build, the complete fast suite when its failure surface is affected, and the narrowest sufficient filtered integration slice. Do not run the complete integration suite locally merely as routine pull-request validation; exact-head GitHub-hosted Ubuntu x64 CI owns the complete integration run and the final platform conclusion.

Run the complete integration suite locally only when diagnosing a failure whose surface cannot be bounded by focused tests, changing suite-wide discovery or execution behavior, collecting an explicitly requested local performance baseline, or otherwise needing evidence that exact-head CI cannot supply. A native-Windows result remains developer feedback, not support evidence.

Use WSL only to exercise Linux-specific behavior or reproduce an Ubuntu CI failure before another push. Keep a WSL validation checkout or worktree on the WSL-native filesystem rather than `/mnt/<drive>` so host-mounted filesystem overhead and semantics do not distort the result. WSL does not replace exact-head GitHub-hosted Ubuntu CI and is not independently qualified support evidence.

| Changed failure surface | Narrowest initial validation | Expected duration | Outer observation budget |
| --- | --- | ---: | ---: |
| Core behavior, schemas, registries, normative contract documents, or contract fixtures | `dotnet test tests/ContractScribe.Tests/ContractScribe.Tests.csproj -c Release --no-build --no-restore` | 5–15 seconds | 1 minute |
| CLI parsing or command behavior without real signal/process lifetime changes | Fast suite plus `dotnet run --project src/ContractScribe.Cli/ContractScribe.Cli.csproj -c Release --no-build -- <affected command>` | 10–30 seconds | 2 minutes |
| Classification | Integration project with `--filter "FullyQualifiedName~ClassificationTests|FullyQualifiedName~AuditProcessDeterminismTests"` | 1–3 minutes | 8 minutes |
| Documentation observation | Integration project with `--filter "FullyQualifiedName~DocumentationObserverTests"` | 30 seconds–2 minutes | 5 minutes |
| Policy evidence | Integration project with `--filter "FullyQualifiedName~PolicyEvidence"` | 30 seconds–2 minutes | 5 minutes |
| Repository loading or fixture preparation | Integration project with `--filter "FullyQualifiedName~RepositoryLoaderTests|FullyQualifiedName~LoaderFixtureTests"` | 4–7 minutes | 15 minutes |
| ProductionAuditHost or audit-result publication | Integration project with `--filter "FullyQualifiedName~ProductionAuditHost|FullyQualifiedName~AuditCliPublicationProcessTests"` | 3–6 minutes | 15 minutes |
| CLI signals, cancellation, timeout, child processes, process observation, or fixture process lifetime | Integration project with `--filter "FullyQualifiedName~AuditCli|FullyQualifiedName~LoaderLifecycleProcessTests|FullyQualifiedName~LoaderFixtureTests|FullyQualifiedName~ProductionAuditHostProcessObservationTests"` | 7–11 minutes | 25 minutes |
| Integration suite | Complete integration project | 9–11 minutes | 25 minutes |
| Full test suite after a valid Release build | Complete fast project, then complete integration project | 9–12 minutes | 25 minutes |
| Cold full validation | Restore, Release build, both projects, format, and all CLI smokes | 10–15 minutes plus first-use SDK/package variance | 30 minutes |

After local focused tests pass, cross-cutting changes to solution/build configuration, fixture lifecycle, shared process infrastructure, parallelization boundaries, or CI orchestration still require complete affected-project coverage and final exact-head GitHub-hosted Ubuntu x64 CI. The CI integration step may supply the complete integration-project coverage instead of duplicating it locally unless one of the local full-suite conditions above applies.

## Parallel execution boundary

The integration assembly has three xUnit workers. Two named process lanes serialize their own members, the long workspace-timeout case is prioritized as a separate scheduling unit, and host-wide real process observation remains globally exclusive because it inventories state outside one owned subtree. This configuration has been observed to pass on the required Ubuntu runner, but the collection names do not classify every test that can launch `dotnet`, BuildHost, CLI, or Loader fixture processes and do not prove a global subprocess-concurrency cap.

CI runner parallelism is separate from xUnit worker parallelism. The four integration partitions run on different hosted runners, so they cannot share writable roots, process trees, build servers, environment mutation, console signals, loopback servers, or process inventories. Each runner restores/builds independently and executes one integration command with the existing three workers and collection isolation. Do not raise the assembly worker count as a CI-speed shortcut. Any later partition change must establish its complete producer/consumer and shared-state boundaries and qualify identical discovered and executed expanded full-name multisets against an applicable unfiltered result.

The four required jobs use one exhaustive, disjoint ownership rule. `integration_campaign_crash` selects `ContractScribe.Roslyn.IntegrationTests.CampaignCliProcessTests.EveryFrozenBoundary_IsAcknowledgedTerminatedAndReinvokedAgainstTheSameCheckpoint` by equality. `integration_http_loss` selects `ContractScribe.Roslyn.IntegrationTests.GitHubProposalCliProcessTests.Every_actual_HTTP_mutation_response_loss_is_reconciled_by_fresh_CLI_processes` by equality, retaining every Theory row. `integration_github_proposal` selects the class-qualified prefix `ContractScribe.Roslyn.IntegrationTests.GitHubProposalCliProcessTests.` with `~` and excludes the HTTP method with `!=`. `integration_other` excludes the crash method with `!=` and that proposal prefix with `!~`. New proposal methods belong to the proposal partition; other additions belong to the complement. An exact-method rename fails its zero-result guard rather than silently transferring its workload.

Every integration worker requires one parseable, nonzero TRX with complete all-pass counters and executed rows linked to their expanded test identities. Missing, malformed, incomplete, skipped, failing, or extra results cannot pass the guard; command/build/guard outcomes and artifact upload remain independently required. `Counters.completed` is not the completeness signal: successful VSTest files can report zero there. After a filter or discovery change, compare each executed result's class, method, and parameterized test name with multiplicity, proving that the partition union equals an applicable unfiltered execution and that different partitions do not overlap. Do not deduplicate Theory rows or freeze test counts. Reuse an existing unfiltered run when production, tests/data, fixtures, projects/packages, and build inputs are unchanged; a workflow or prose-only commit does not by itself require another full execution. Otherwise use the existing `workflow_dispatch` input `include_unfiltered_evidence=true` once to obtain the missing control. This development qualification is not a permanent ordinary-PR equality gate or a separate validation framework.

Do not add another active process group merely because a test is outside the named lanes. Identify its actual shared boundaries first, including filesystem roots, MSBuild registration or build servers, environment mutation, real process inventory, console-signal routing, and other process-global seams. Retain three workers unless exact-head complete Ubuntu integration evidence exposes a failure that requires a narrower scheduling correction; do not infer a need for assembly-wide serialization from unsupported native-Windows behavior alone.

Do not remove or merge these collections merely to shorten one filtered command. A new test may use ordinary class-level parallelism only when it has distinct filesystem roots and does not share MSBuild registration, environment mutation, real process inventory, console-signal routing, or another process-global seam. Otherwise assign it to the matching process lane or the exclusive observation collection and document the shared-state reason.

Prose-only documentation may omit .NET tests only when it changes prose, links, or tracker metadata and does not change a normative contract, schema, registry, fixture meaning, generated/checkable output, CLI help or behavior, executable command, architecture dependency rule, or other test-enforced behavior. Contract documentation and generated/checkable documentation use their corresponding contract tests.

## Fixture reuse boundary

Prepared-state reuse is intentionally limited to the built-in default two-project fixture and the built-in ordinary-generator fixture. Both categories have direct tests that prove one preparation, distinct writable consumer roots, template-unavailable relocation, token absence, and successful repository loading on required Ubuntu CI. Earlier Windows observations remain evidence only for their exact revisions and conditions.

Custom project XML remains fresh because it can add preparation targets or absolute bindings outside the closed relocation proof. Process-sensitive, self-observing, many-output, and colliding-output generators remain fresh because their observable behavior or output topology is part of the preparation boundary. Two-dependency reference-order variants and the Loader lifecycle probe remain fresh because their graph order or process topology requires category-specific relocation proof. These are deliberate isolation dispositions, not cache misses; do not broaden the reusable classifier without adding the matching supported-runner reuse and behavior test.

Fixture preparation disables MSBuild node reuse for `dotnet restore`, `dotnet build`, and direct `dotnet msbuild` commands. Keep that ownership boundary when adding preparation commands: a reusable MSBuild node can outlive the command root, retain redirected output handles, and make the test runner wait on a process it no longer owns through the original root. Preserve shared compilation and ordinary file/restore caches: hosted evidence showed that disabling all build servers or shared compilation expanded integration latency from roughly 7 minutes to roughly 15 minutes. After a fixture command exits successfully and both redirected streams drain, its observed `VBCSCompiler` server is shared infrastructure rather than an owned leak and must remain available to other builds. Failure, cancellation, timeout, or incomplete stream drain still uses strict owned-tree termination; compiler clients can recover if that exceptional cleanup also terminates the server.

The cache allocates an ownership container before invoking reusable preparation. The prepared template, relocation qualification root, and temporary offline path must all remain inside that container so cancellation, qualification failure, or restore/build failure reaches one strict cleanup barrier. A failed live-host deletion keeps the shape entry faulted and blocks replacement preparation; only process-exit cleanup is best effort.

## Build validity

Use `--no-build --no-restore` only after the current working tree completed the applicable build with the same SDK and configuration. Rebuild when production or test source, a `.csproj` or `.slnx`, `Directory.*`, `global.json`, package-version inputs, a generator/helper project, or shared fixture/process infrastructure changed. Restore again when projects, package sources, package versions, SDK selection, lock inputs, or restore properties changed.

Do not reuse Release outputs for Debug commands or outputs from another SDK, worktree, commit, or changed build input. When validity is uncertain, rebuild instead of treating a fast stale run as evidence.

## Result isolation and duration evidence

Give every measured run a unique results directory and TRX prefix. For example:

```text
dotnet test tests/ContractScribe.Tests/ContractScribe.Tests.csproj --configuration Release --no-build --no-restore --logger "trx;LogFilePrefix=fast-head-01" --results-directory TestResults/test-feedback/head/01/fast
dotnet test tests/ContractScribe.IntegrationTests/ContractScribe.IntegrationTests.csproj --configuration Release --no-build --no-restore --logger "trx;LogFilePrefix=integration-head-01" --results-directory TestResults/test-feedback/head/01/integration
```

Read per-test and runner duration from TRX rather than console timestamps, and capture the outer wall clock separately for performance comparisons. For a performance investigation, report total suite time, per-class totals, the slowest 20 executed cases, and every case taking at least 30 seconds from one representative measured run; ordinary focused correctness checks need only their result and duration. A PowerShell inspection can parse each `UnitTestResult.duration`, join its `testId` to `TestDefinitions/UnitTest`, group by the fully qualified class portion, and sort descending. Keep raw local TRX under ignored `TestResults/`; do not commit it.

For a performance comparison, record exact commit, OS, resolved dotnet host, SDK, configuration, command, cold/warm classification, discovered and executed names, and wall time. Compare sorted full-name multisets as well as unique-name sets. Counts and hashes are useful summaries but do not authorize a missing test, reduced Theory data, filter, quarantine, or rename.

## Long-run observation and non-overlap

Before a long run, assign the table's outer observation budget and preserve the terminal or process handle that launched it. The warm full-test-suite budget must be at least twice the measured expected duration; the current 25-minute budget covers the 9–12 minute range. Cold guidance separately includes restore, build, and first template qualification.

If the outer observation budget expires, inspect the original terminal, process handle, and any known directly owned child subtree. Continue waiting when the original run is active or its state is uncertain. Do not automatically kill or restart it. Start a second run only after the first run completed or bounded owned-process evidence proves non-overlap. Never terminate unrelated `dotnet` processes through a global process-name scan.

## Full validation

Use unique TRX paths in an evidence run:

```text
dotnet restore ContractScribe.slnx
dotnet build ContractScribe.slnx --configuration Release --no-restore
dotnet test tests/ContractScribe.Tests/ContractScribe.Tests.csproj --configuration Release --no-build --no-restore --logger "trx;LogFilePrefix=fast-full" --results-directory TestResults/test-feedback/full/fast
dotnet test tests/ContractScribe.IntegrationTests/ContractScribe.IntegrationTests.csproj --configuration Release --no-build --no-restore --logger "trx;LogFilePrefix=integration-full" --results-directory TestResults/test-feedback/full/integration
dotnet format ContractScribe.slnx --verify-no-changes --no-restore
dotnet run --project src/ContractScribe.Cli/ContractScribe.Cli.csproj --configuration Release --no-build -- --help
dotnet run --project src/ContractScribe.Cli/ContractScribe.Cli.csproj --configuration Release --no-build -- --version
dotnet run --project src/ContractScribe.Cli/ContractScribe.Cli.csproj --configuration Release --no-build -- doctor
```

Exact-head GitHub-hosted Ubuntu x64 CI is the final required platform leg, but it does not replace local focused validation. In CI the complete fast suite, formatting, and all CLI smokes share one runner; a continued fast-suite failure does not suppress the static/smoke observations. Four integration workers run on separate runners with distinct TRX artifacts. Each worker aggregates its original build/test/guard `outcome`, including failures retained by step-level `continue-on-error`; a successful post-error step `conclusion` is not authority. The stable `validate` job succeeds only when all four integration workers and all existing required fast/static, payload, and Action jobs succeed. Failed, cancelled, skipped, or missing required jobs fail closed. The existing independent CodeQL check remains unchanged.

The campaign crash, HTTP response-loss, and other GitHub proposal commands have 35-minute hard timeouts; the other integration command has 45 minutes. The opt-in complete unfiltered control has 65 minutes and the same complete-result guard, and is excluded from ordinary `validate` dependencies. These are finite diagnostic safety bounds, not a promised latency or performance threshold. Integration commands retain per-test progress for interrupted-run diagnostics. No automatic retry is added. CI hard caps are distinct from the local outer observation budgets above, which require inspecting and preserving an active original run rather than killing and restarting it. Artifact names include `github.run_attempt`, so an explicitly initiated rerun preserves its own TRX without colliding with an earlier attempt.

The merged M7 fixed-batch coverage remains mandatory: checkpoint admission and changed-base/final-successor capacity, fresh-process fixed-batch reconstruction, provider reservation and bounded completion, and proposal no-op/stale-batch cases retain their production seams and Theory rows. Positive capacity fixtures keep their explicit compliant type, finite source size, and all excluded/deferred targets. This layout replaces the temporary 60-minute serial remaining margin; it does not relax any product/provider/campaign budget. The successful C2 diagnostic run [37196502514](https://github.com/SolusQuest/contract-scribe/actions/runs/37196502514) spent about 40 minutes in the old remaining job and 10 minutes in crash recovery. Its other-partition cases summed to about 35 minutes; the 45-minute command cap supplies finite margin even over that sum. The 65-minute full-control cap supplies about 15 minutes beyond the two observed command workloads. These diagnostics size the caps only: the original C2 head differs from merged main in production/build inputs and cannot qualify final performance or coverage. Final qualification uses an applicable merged-input old-layout observation, complete four-partition execution, and complete unfiltered control.

Pull-request CI uses one concurrency group per PR and cancels obsolete heads. Manual evidence and push runs use the unique workflow run ID and are never cancelled by a sibling sample. Logs record the computed group and cancellation policy with the runner/toolchain facts.

Pre-release performance comparisons use a small, prospectively selected set of complete first-attempt runs without overlapping measured workloads. Ordinary PR CI can count; do not require additional manual runs solely for their event type. Complete the ordinary PR latency observation before dispatching an additional unfiltered control so that its extra hosted job does not affect queue or scheduling observations. Keep the runner class, relevant toolchain, configuration, and test workload comparable, and record source/workflow revisions, image facts, all selected results, and any failures. Distinguish summed case/lane time, test-step elapsed time, longest required integration job, integration-group makespan, ordinary complete-CI wall time, and summed runner/setup time; report the control-inclusive dispatch separately. An exact hosted-image build match is not a gate: image rollout alone does not invalidate evidence. Investigate demonstrated relevant differences and state the limits of a small observational sample instead of claiming statistical significance or a hosted-runner SLA.

Reuse evidence for unchanged inputs and refresh only what a correction invalidates. A missing result from an infrastructure failure can be replaced with a disclosed independent run; a test failure requires diagnosis and correction, never a retry-until-green policy. Documentation or tracker changes do not restart performance sampling, and one unrelated or incomplete run does not erase valid observations. [Issue #168](https://github.com/SolusQuest/contract-scribe/issues/168) records the bounded comparison for the initial split; it does not create a permanent multi-run qualification gate for later changes.
