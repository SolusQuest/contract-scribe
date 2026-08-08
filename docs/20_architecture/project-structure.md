# Project structure

## Decision

ContractScribe uses project boundaries to enforce dependency and authority boundaries, not to mirror roadmap milestones or create one assembly per feature.

The current planning sketch separates up to six production concerns. `Core`, `Roslyn`, and `Cli` are existing M1 projects; the other names are candidate future splits rather than project contracts:

1. `ContractScribe.Core`
2. `ContractScribe.Roslyn`
3. `ContractScribe.Patching`
4. `ContractScribe.Agent`
5. `ContractScribe.GitHub`
6. `ContractScribe.Cli`

The existing M1 project boundaries are current architecture. The M2-M5 entries are candidates, not an instruction to create empty projects or preserve a planned assembly name. Each implementing milestone applies the split thresholds below and may keep a capability in an existing project or choose a narrower boundary when executable dependencies and authority require it.

The normal test graph contains two long-lived projects:

1. `ContractScribe.Tests`
2. `ContractScribe.IntegrationTests`

Fixture projects, M0 experiment projects, and an optional live evaluation tool are counted separately because they are not product assemblies.

## Decision status

The existing M1 `Core` / `Roslyn` / `Cli` graph and its implemented dependency constraints are current architecture. The six-concern M2-M5 graph, future project names, reference edges, API boundaries, friend access, and negative-test mechanisms are candidates selected by each implementing milestone from executable dependency and authority evidence.

The GitHub Action host remains an open distribution decision. This document constrains the host boundary, but it does not select composite action or TypeScript/JavaScript action before executable payload evidence exists. That selection is recorded in the payload-distribution ADR.

## Design goals

The project graph must:

- preserve the deterministic audit, model-assisted proposal, deterministic patch, and GitHub mutation boundaries;
- make forbidden dependencies visible to the compiler and dependency tests;
- keep provider and GitHub packages out of deterministic code paths;
- keep GitHub Actions hosting choices outside the product domain model;
- avoid speculative abstractions, empty assemblies, and one-project-per-milestone growth;
- allow the production CLI to run outside GitHub Actions;
- allow campaign and GitHub behavior to be tested without a real provider or live repository;
- preserve M0 evidence without promoting experiment assemblies into production APIs.

## Alternatives

### Keep only `Core` and `Cli`

This remains appropriate for the bootstrap but not for the completed product. Roslyn/MSBuild dependencies, provider network access, candidate source writes, and GitHub mutation would otherwise share one library boundary and make forbidden dependency tests ineffective.

### Create one project per milestone

Rejected. Milestones describe delivery order, not dependency boundaries. In particular, M4 campaign behavior belongs in the platform-neutral Core rather than a project named after orchestration, and M6 normally adds a host artifact rather than another product assembly.

### Create one project per contract, service, or provider immediately

Deferred. Separate contract, application, shared-Roslyn, and provider projects may become justified by distribution, dependency, authority, or coexistence evidence. Creating them before such evidence increases reference and versioning cost without strengthening a current boundary.

### Put GitHub publication in TypeScript

Rejected for product behavior. It would duplicate campaign and publication contracts across runtimes and make local, CLI, and Action execution follow different reconciliation paths.

A thin TypeScript Action host remains a valid distribution candidate because host input/output mapping and payload acquisition are separate from GitHub publication semantics.

## Non-goals

This project structure does not:

- select the first payload distribution channel;
- promise a TypeScript or Node runtime;
- create independently published NuGet packages;
- make assembly boundaries equivalent to an operating-system sandbox;
- split every port from its implementation;
- require projects that contain no current production behavior;
- decide whether future provider adapters need separate assemblies.

## Production projects

### `ContractScribe.Core`

`ContractScribe.Core` owns current provider-neutral and platform-neutral product contracts and application behavior. Future milestones add only the contracts their executable paths require:

- policy and normalized target-selection inputs;
- normalized symbol facts and documentation observations;
- current audit DTOs, plus proposal, patch, context, usage, work, state, or publication DTOs only when their owning milestone implements a real producer-consumer boundary;
- canonical serialization and stable product-owned failure codes;
- the budget, context, retry, ordering, and state-transition rules selected by current executable evidence;
- ports needed by implemented audit, evidence, proposal, patch, state, or publication use cases.

Core contract visibility is authority-sensitive. The implemented Scribe must receive only closed read-only capabilities, while source-write, state-persistence, and platform-publication authority remains inaccessible to it and is composed only by the CLI. Each implementing milestone selects the smallest visibility, project, and negative-test mechanism that makes that boundary executable; the pre-M3 roadmap does not require a future friend-assembly allowlist, public API shape, or project name. If an implemented boundary becomes unworkable, apply the split thresholds rather than making mutation capabilities generally public.

It may use the .NET base class libraries and narrowly justified contract libraries. It must not reference:

- Roslyn or MSBuild;
- a model-provider SDK;
- a GitHub SDK;
- Git commands or GitHub Actions APIs;
- process-global environment discovery;
- source-writing implementations.

`ContractScribe.Core` is not a miscellaneous utilities project. Code belongs here only when its semantics are independent of C#, a model provider, GitHub, and the CLI host.

### `ContractScribe.Roslyn`

`ContractScribe.Roslyn` owns the production C# and MSBuild read path:

- explicit solution and project loading;
- SDK and MSBuild registration required by the selected in-process topology;
- symbol discovery and canonical declaration resolution;
- effective-accessibility and target-profile classification;
- XML-documentation observation;
- bounded evidence extraction;
- deterministic repository-entrypoint and nested-`AGENTS.md` discovery;
- repository-confined evidence inputs selected by M3;
- implementations of bounded semantic and repository-read ports when M3 assigns them here;
- deterministic Roslyn diagnostics and normalized failure mapping.

It depends on `ContractScribe.Core` and Roslyn/MSBuild packages. It must not reference:

- `ContractScribe.Patching`;
- `ContractScribe.Agent`;
- `ContractScribe.GitHub`;
- provider or GitHub SDKs;
- a GitHub token;
- arbitrary source-writing APIs.

The project may read repository files only through repository-root-confined services. It does not own candidate source writes.

### Candidate `ContractScribe.Patching`

If M2 evidence meets the split thresholds, `ContractScribe.Patching` owns the only product path that may create candidate source modifications:

- stale-source and declaration-identity validation;
- XML-documentation rendering and escaping;
- documentation-trivia insertion or replacement;
- candidate-workspace writes;
- syntax, non-documentation-token, symbol, signature, encoding, and idempotency validation;
- patch and patch-validation results.

It depends on `ContractScribe.Core` and may depend on the read-only declaration-resolution facilities in `ContractScribe.Roslyn`. The dependency is one-way: `ContractScribe.Roslyn` never references `ContractScribe.Patching`.

It must not reference a model-provider or GitHub SDK. It accepts only validated structured proposals; it never accepts arbitrary diffs or model-generated source text.

### Candidate `ContractScribe.Agent`

If M3 evidence meets the split thresholds, `ContractScribe.Agent` owns the narrow Scribe Runtime:

- context and tool-call orchestration;
- repository/scope/target context assembly over the minimum M3-selected inputs and injected read ports;
- provider-neutral model request and response contracts;
- the closed read-only tool registry;
- the provider transport and usage normalization selected from M3 executable evidence;
- tool, input/output, cost when observable, attempt, and time budgets;
- structured proposal submission and validation;
- deterministic fake runtime;
- the initial real provider transport unless its dependency footprint justifies a separate adapter project.

It depends on `ContractScribe.Core`. Semantic tool implementations are injected through the closed public read-only Scribe-port surface; the agent does not need a direct Roslyn dependency.

It must not reference:

- `ContractScribe.Patching`;
- `ContractScribe.GitHub`;
- source-writing services;
- GitHub tokens or APIs;
- shell or general-purpose edit runtimes.

The implemented project/reference and API boundaries must prevent the Scribe runtime from receiving concrete mutation, state-storage, or publication capabilities. The CLI composition root injects only the selected closed read-only tool registry and read ports. M3 chooses the minimum negative compilation, API-surface, or composition tests needed to make this an executable property rather than a prompt convention. It does not claim that an in-process assembly is an operating-system sandbox.

Project-context bootstrap and bounded semantic traversal are component responsibilities, not reasons to create another agent. M3 places them across the existing or justified projects according to the implemented dependency graph, while Cli composes their lifetimes. See [Scribe context and prompt economics](scribe-context-and-prompt-economics.md).

### Candidate `ContractScribe.GitHub`

If M5 evidence meets the split thresholds, `ContractScribe.GitHub` owns the GitHub platform adapter:

- the minimum durable state needed to reconcile GitHub Issues, branches, commits, and pull requests safely;
- explicit branch and pull-request ownership;
- at most one compatible active bot-owned proposal pull request for current work, created as draft;
- idempotent mutation and safe retry using only the identity or operation mechanism demonstrated necessary by M5;
- base drift, conflict, human-change, corruption, closure, and replay handling;
- GitHub permission, API failure, rate-limit, and response validation;
- publication records expressed through Core contracts.

It depends on `ContractScribe.Core` and a narrowly selected GitHub HTTP or SDK implementation. It must not reference Roslyn, `ContractScribe.Agent`, or provider SDKs.

The adapter consumes a validated publication plan. It does not select audit targets, generate documentation, validate patches, or own the platform-neutral campaign state machine.

### `ContractScribe.Cli`

`ContractScribe.Cli` is the executable composition root:

- command and option parsing;
- configuration binding;
- construction of concrete Core ports;
- cancellation and process lifetime;
- human diagnostics, structured run envelopes, and exit-code mapping;
- invocation of audit, proposal, patch, campaign, and publication use cases as they become available.

It may reference every production project because it composes them. It must not become the home of product rules, provider-specific policy, GitHub reconciliation, Roslyn analysis, or patch semantics.

The same CLI is the payload invoked locally, from validation workflows, and by a future GitHub Action wrapper.

## Candidate future dependency graph

The following graph is one candidate if M2, M3, and M5 independently meet their split thresholds. Only edges between projects that actually exist are current architecture requirements.

```text
ContractScribe.Cli
  |
  +-- ContractScribe.Core
  +-- ContractScribe.Roslyn -------> ContractScribe.Core
  +-- ContractScribe.Patching -----> ContractScribe.Core
  |                                  + ContractScribe.Roslyn
  +-- ContractScribe.Agent --------> ContractScribe.Core
  +-- ContractScribe.GitHub -------> ContractScribe.Core
```

Forbidden references:

```text
Agent -X-> Patching
Agent -X-> GitHub
Roslyn -X-> Patching
Roslyn -X-> Agent
GitHub -X-> Roslyn
GitHub -X-> Agent
Core -X-> any infrastructure project
```

Current architecture tests enumerate existing project references and fail when a forbidden edge appears. When a future Scribe, patching, state, or GitHub boundary is implemented, its milestone adds the minimum API-surface, negative-capability, and composition tests needed to prove that the Scribe cannot receive mutation authority. Do not create future assemblies, friend allowlists, or negative fixtures solely to satisfy this candidate graph.

## Milestone evolution

| Milestone | Production-project change |
| --- | --- |
| M0 | `Core` and `Cli` remain the minimal product skeleton; Roslyn projects remain test-only experiments. |
| M1 | Add production `ContractScribe.Roslyn`; keep `Core` and `Cli`; add integration tests when real workspace/process behavior requires them. |
| M2 | Candidate: add `ContractScribe.Patching` if the source-write authority and dependency graph meet the split thresholds. |
| M3 | Candidate: add `ContractScribe.Agent` if the read-only Scribe runtime boundary meets the split thresholds. |
| M4 | Candidate: keep platform-neutral campaign behavior in `Core`; create no milestone-named project without an observed split need. |
| M5 | Candidate: add `ContractScribe.GitHub` if platform mutation and dependency isolation meet the split thresholds. |
| M6 | Add the selected Action wrapper and release artifacts; add no C# project without an observed split need. |

This candidate sequence prevents later projects from becoming speculative dependencies of M1; milestone implementation evidence, not this table alone, decides the final placement.

## Test, fixture, experiment, and evaluation projects

### `ContractScribe.Tests`

The default test project contains fast deterministic tests for current behavior and adds future capability tests only when that capability is implemented:

- schemas, registries, manifests, and canonical serialization;
- pure policy and current contract behavior, plus context, ordering, budget, or state rules selected by later milestones;
- fake Scribe or platform-adapter orchestration when those components exist;
- invalid-input and failure-precedence tests;
- dependency-boundary tests.

It must not require a provider secret, GitHub token, external repository, or live network.

### `ContractScribe.IntegrationTests`

This existing project contains tests whose cost or host interaction makes them unsuitable for the fast suite:

- MSBuild and Roslyn workspace loading;
- repository-root and symlink/reparse behavior;
- nested agent-entrypoint discovery and repository/scope context loading;
- candidate workspace and filesystem validation;
- CLI process and cancellation behavior, plus any provider/context behavior selected by M3;
- fake HTTP provider and GitHub server integration when their milestones implement them;
- implemented cross-component scenarios with deterministic substitutes.

Live provider calls and live GitHub writes do not belong in the ordinary integration suite.

### Fixture projects

Synthetic `.csproj` and `.sln` inputs under `tests/fixtures` are analyzed subjects, not ContractScribe product projects. Their target frameworks and shapes follow fixture requirements rather than the product dependency graph.

### M0 experiment projects

The removed test-only Roslyn, Native AOT, and independent-validation projects remain historical M0 evidence in Git, not current validation inputs. Production code does not reference experiment assemblies.

Issue #75 removed their executable, ordinary-CI, manifest, compatibility, tombstone, provenance, aggregation, publication, and preservation-only machinery. Reusable semantic fixtures and regression cases moved under the current production owner; the experiment questions, conditions, results, limitations, and exact commits remain in documentation and Git history without a separate cleanup decision.

### Optional evaluation tool

M3 may add `tools/ContractScribe.Evaluation` when a repeatable real-provider evaluation needs an executable harness. It is not a product library and is not part of ordinary `dotnet test`.

The tool must require explicit opt-in, use synthetic inputs without secrets or private repository data by default, record only the bounded provider usage, cost, and provenance needed by the current evaluation claim, and never make live provider availability a merge prerequisite.

## Split thresholds

Do not create `ContractScribe.Contracts`, `ContractScribe.Application`, `ContractScribe.Roslyn.Shared`, or one project per provider merely for conceptual neatness.

Create an additional project only when at least one concrete condition exists:

- an independently distributed consumer requires a smaller contract assembly;
- incompatible dependency sets must not load into the same host;
- a secret or mutation authority needs a compile-time boundary;
- two implementations need the same stable port but cannot safely share dependencies;
- build, trimming, or runtime evidence requires separate artifacts;
- a second provider makes SDK isolation, optional dependencies, or support policy materially clearer;
- circular dependencies cannot be removed through a Core-owned port.

Any split issue must name the dependency or authority problem, demonstrate it in the current graph, define the coherent code movement and tests, and identify whether an ADR is required. Compatibility or migration machinery is included only for a real boundary that cannot be updated atomically.

## GitHub Action host boundary

TypeScript is not required for branch, commit, Issue, or pull-request operations. Those operations belong to the M5 GitHub adapter, whose final project placement is selected from dependency and authority evidence, so the same reconciliation behavior can run and be tested outside GitHub Actions.

The initial Action-host candidates are:

1. a composite action that acquires/configures the selected .NET payload and invokes the CLI;
2. a JavaScript action produced from a small TypeScript source package when payload acquisition or cross-platform host behavior is too complex for a maintainable composite action.

The payload-distribution decision selects between those candidates with executable evidence. Source development through M5 does not require TypeScript.

If a TypeScript wrapper is selected, it is a thin host only:

- parse Action inputs;
- locate, download, verify, or cache the exact .NET payload;
- start the CLI and propagate cancellation;
- receive and forward explicitly allowlisted environment inputs and credentials to the CLI;
- mask secrets;
- map CLI outcomes to Action outputs, annotations, summaries, and exit status.

It must not:

- call a model provider directly;
- parse audit results to choose work;
- implement campaign transitions;
- store or reconcile the Issue ledger;
- create branches, commits, or pull requests;
- interpret, persist, log, or independently use forwarded credentials;
- duplicate Core contracts or GitHub adapter rules;
- turn Action event payloads into trusted instructions without validation.

If the selected distribution boundary requires the wrapper and payload to be verified independently, M6 defines the minimum separate identity and provenance needed to prevent substitution. The pre-release project graph does not reserve that mechanism in advance.

## Acceptance checks

The current project graph is healthy when:

- existing production reference edges match the current boundaries in this document;
- when the Scribe is implemented, its public API, constructors, dependency registration, and negative-capability tests prove that it receives only the selected closed read-only tools and ports;
- when patching is implemented, deterministic tests can load its implemented boundary without provider or GitHub packages;
- when the Scribe is implemented, its tests can run with fake semantic ports and no source-write implementation;
- when the GitHub adapter is implemented, its tests can run with fake publication plans and no Roslyn or model runtime;
- the CLI is the only production composition root;
- when an Action wrapper is implemented, its tests treat the CLI as an external payload contract;
- no M0 experiment assembly is referenced from `src`;
- no empty future-milestone project is added only to reserve a name.

The composite-versus-TypeScript Action host choice is finalized in the payload-distribution ADR after the production CLI and GitHub workflow are executable.
