# Test validation

Use this procedure to select validation from the failure surface changed by the work. Do not default to the complete solution merely because a file changed, and do not omit a contract test merely because the changed contract is stored as documentation or data.

## Suite definitions

- The **fast suite** is the complete `tests/ContractScribe.Tests/ContractScribe.Tests.csproj` project.
- The **integration suite** is the complete `tests/ContractScribe.IntegrationTests/ContractScribe.IntegrationTests.csproj` project.
- The **full test suite** is one applicable successful build followed by the complete fast-suite command and then the complete integration-suite command, without filters or skipped projects.
- **Full validation** is restore, a Release build, the full test suite, format verification, and the existing CLI help, version, and doctor smoke commands.

## Select the narrowest sufficient command

The ranges below are evidence-based guidance for the current local Windows development host. They are developer feedback estimates, not platform-support evidence or performance gates. The outer budget is an observation budget for the original process, not a deadline that authorizes killing and retrying it.

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

After local focused tests pass, cross-cutting changes to solution/build configuration, fixture lifecycle, shared process infrastructure, parallelization boundaries, or CI orchestration still require the complete affected projects and final exact-head GitHub-hosted Ubuntu x64 CI.

## Parallel execution boundary

The integration assembly has three xUnit workers. Two named process lanes serialize their own members, the long workspace-timeout case is prioritized as a separate scheduling unit, and host-wide real process observation remains globally exclusive because it inventories state outside one owned subtree. This configuration has been observed to pass on the required Ubuntu runner, but the collection names do not classify every test that can launch `dotnet`, BuildHost, CLI, or Loader fixture processes and do not prove a global subprocess-concurrency cap.

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

Read per-test and runner duration from TRX rather than console timestamps, and capture the outer wall clock separately for performance comparisons. Report total suite time, per-class totals, the slowest 20 executed cases, and every case taking at least 30 seconds. A PowerShell inspection can parse each `UnitTestResult.duration`, join its `testId` to `TestDefinitions/UnitTest`, group by the fully qualified class portion, and sort descending. Keep raw local TRX under ignored `TestResults/`; do not commit it.

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

Exact-head GitHub-hosted Ubuntu x64 CI is the final required platform leg, but it does not replace local focused validation. In CI the fast and integration suites remain separately named, write separate TRX artifacts, both run after a successful build even if one fails, and an ordinary final step aggregates their original outcomes. The integration step has a 25-minute outer timeout and no automatic retry. Artifact names include `github.run_attempt`, so an explicitly initiated rerun preserves its own TRX without colliding with an earlier attempt.
