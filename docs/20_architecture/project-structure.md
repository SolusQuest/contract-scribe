# Project structure

## Decision

ContractScribe uses project boundaries to enforce dependency and authority boundaries, not to mirror roadmap milestones or create one assembly per feature.

The current candidate long-lived product graph contains six production projects:

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

The six-project product graph and its forbidden reference edges are the default implementation architecture for M1 through M5.

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

`ContractScribe.Core` owns provider-neutral and platform-neutral product contracts and application behavior:

- policy and normalized target-selection inputs;
- normalized symbol facts and documentation observations;
- audit, proposal, patch, project-context identity, usage, work-plan, campaign-state, and publication DTOs;
- canonical serialization and stable product-owned failure codes;
- budget, context-grouping, retry, ordering, and state-transition rules;
- ports such as audit, semantic evidence, proposal generation, patching, state storage, and publication interfaces.

Core contract visibility is authority-sensitive. Pure DTOs, identities, state transitions, and the closed read-only Scribe-port surface may be public. Capability-bearing ports that authorize candidate source writes, state persistence, or platform publication are internal by default and production-friend-visible only to `ContractScribe.Cli`. Patching and GitHub expose their validated high-level operations from their own assemblies; Cli adapts those operations to the internal Core capabilities at the composition boundary. `ContractScribe.Agent` is never a friend assembly for those capabilities. Test-only friend access does not grant a production composition path and is checked separately. If this visibility model becomes unworkable, the authority boundary meets the split threshold for a separately reviewed capability-specific contract project; it must not be weakened by making mutation ports generally public.

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
- context-pack construction inputs and repository/scope content identities;
- implementations of the agent's bounded semantic and repository-read ports;
- deterministic Roslyn diagnostics and normalized failure mapping.

It depends on `ContractScribe.Core` and Roslyn/MSBuild packages. It must not reference:

- `ContractScribe.Patching`;
- `ContractScribe.Agent`;
- `ContractScribe.GitHub`;
- provider or GitHub SDKs;
- a GitHub token;
- arbitrary source-writing APIs.

The project may read repository files only through repository-root-confined services. It does not own candidate source writes.

### `ContractScribe.Patching`

`ContractScribe.Patching` owns the only product path that may create candidate source modifications:

- stale-source and declaration-identity validation;
- XML-documentation rendering and escaping;
- documentation-trivia insertion or replacement;
- candidate-workspace writes;
- syntax, non-documentation-token, symbol, signature, encoding, and idempotency validation;
- patch and patch-validation results.

It depends on `ContractScribe.Core` and may depend on the read-only declaration-resolution facilities in `ContractScribe.Roslyn`. The dependency is one-way: `ContractScribe.Roslyn` never references `ContractScribe.Patching`.

It must not reference a model-provider or GitHub SDK. It accepts only validated structured proposals; it never accepts arbitrary diffs or model-generated source text.

### `ContractScribe.Agent`

`ContractScribe.Agent` owns the narrow Scribe Runtime:

- context and tool-call orchestration;
- repository/scope/target context assembly over Core-owned identities and injected read ports;
- provider-neutral model request and response contracts;
- the closed read-only tool registry;
- stable prompt-prefix construction and local prefix identity;
- the provider transport and usage normalization selected from M3 executable evidence;
- tool, cached/uncached token, cost, attempt, and time budgets;
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

Project-reference direction prevents `Agent` from depending on concrete mutation implementations. Core API visibility separately prevents it from declaring, accepting, returning, or resolving candidate-write, state-storage, or publication capabilities. The CLI composition root injects only the closed read-only Scribe tool registry and read ports. Together with negative compilation and public-API tests, this makes the absence of product mutation capabilities an executable compile/build-time property rather than a prompt convention. It does not claim that an in-process assembly is an operating-system sandbox.

`ProjectContextBootstrapper` and `ProjectContextSession` are component responsibilities, not reasons to create another agent or production project. Core owns provider-neutral identities and rules, Roslyn owns repository-confined discovery and read implementations, Agent owns model-visible assembly and semantic traversal, and Cli composes their lifetimes. See [Scribe context and prompt economics](scribe-context-and-prompt-economics.md).

### `ContractScribe.GitHub`

`ContractScribe.GitHub` owns the GitHub platform adapter:

- Issue checkpoint and append-only run-record reconciliation;
- branch, commit, and pull-request ownership;
- creation or continuation of at most one active bot-owned proposal pull request per campaign, created as draft, with successive snapshot-bound draft generations only after terminal predecessors;
- operation IDs and idempotent mutation;
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

## Dependency graph

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

Architecture tests enumerate project references and fail when a forbidden edge appears. They also inspect the Core and Agent public API surfaces, verify that mutation-capable Core ports are not publicly visible, verify that the production friend allowlist contains only Cli and never Agent, and compile a negative Agent fixture that attempts to reference or receive candidate-write, state-storage, and publication capabilities. That fixture must fail for accessibility or contract-surface reasons, not merely because a concrete implementation assembly is absent.

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

The default test project contains fast deterministic tests:

- schemas, registries, manifests, and canonical serialization;
- pure policy, context identity/grouping, ordering, budget, and state-transition behavior;
- fake-agent and fake-adapter orchestration;
- invalid-input and failure-precedence tests;
- dependency-boundary tests.

It must not require a provider secret, GitHub token, external repository, or live network.

### `ContractScribe.IntegrationTests`

Create this project when M1 production loading begins. It contains tests whose cost or host interaction makes them unsuitable for the fast suite:

- MSBuild and Roslyn workspace loading;
- repository-root and symlink/reparse behavior;
- nested agent-entrypoint discovery and repository/scope context loading;
- candidate workspace and filesystem validation;
- CLI process, prompt-prefix fixture, and cancellation behavior;
- fake HTTP provider and GitHub server integration;
- cross-component audit-to-publication scenarios with deterministic substitutes.

Live provider calls and live GitHub writes do not belong in the ordinary integration suite.

### Fixture projects

Synthetic `.csproj` and `.sln` inputs under `tests/fixtures` are analyzed subjects, not ContractScribe product projects. Their target frameworks and shapes follow fixture requirements rather than the product dependency graph.

### M0 experiment projects

The existing test-only Roslyn and Native AOT experiment projects remain historical evidence inputs. M1 may migrate reusable implementation knowledge into `src/ContractScribe.Roslyn`, but production code must not reference the experiment assemblies.

After M1 establishes replacement evidence, a separate cleanup decision may retire an experiment executable from the active solution while preserving its source, manifests, and historical records where reproducibility requires them.

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

Any split issue must name the dependency or authority problem, demonstrate it in the current graph, define migration and tests, and identify whether an ADR is required.

## GitHub Action host boundary

TypeScript is not required for branch, commit, Issue, or pull-request operations. Those operations belong to `ContractScribe.GitHub` so the same reconciliation behavior can run and be tested outside GitHub Actions.

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

The wrapper and payload have separate identities and provenance. Changing the wrapper does not silently change the payload, and changing the payload does not silently rewrite wrapper behavior.

## Acceptance checks

The project graph is healthy when:

- production reference edges match this document;
- Core's public API exposes only the approved read-only Scribe capabilities, while candidate-write, state-storage, and publication ports remain inaccessible to Agent;
- a negative Agent compilation fixture cannot reference, accept, return, or resolve those mutation-capable ports;
- Agent constructors, properties, methods, and dependency-registration paths accept only the closed read-only Scribe tool registry and read ports;
- deterministic tests can load `Core`, `Roslyn`, and `Patching` without provider or GitHub packages;
- agent tests can run with fake semantic ports and no source-write implementation;
- GitHub adapter tests can run with fake publication plans and no Roslyn or model runtime;
- the CLI is the only production composition root;
- Action-wrapper tests treat the CLI as an external payload contract;
- no M0 experiment assembly is referenced from `src`;
- no empty future-milestone project is added only to reserve a name.

The composite-versus-TypeScript Action host choice is finalized in the payload-distribution ADR after the production CLI and GitHub workflow are executable.
