# Origin and scope

ContractScribe was extracted from downstream-specific documentation automation planning because the underlying problem is reusable across C# repositories. Downstream projects retain their own targets, terminology, language preferences, evidence escalation, schedules, and integration validation.

`CS1591` is a candidate signal, not a documentation-quality decision. Deterministic audit comes before proposal generation so that future results can be explained, replayed, and validated without a network, model, provider secret, or GitHub write token.

The product is not a general coding agent. Its proposal capability is the Documentation Scribe, a narrow repository-bounded role with project-owned context construction, semantic read tools, budgets, structured output, and evidence requirements. The Scribe cannot use a shell, edit arbitrary files, or publish changes.

A deterministic patch engine, not the model, owns source modification. It renders a structured documentation proposal at the selected declaration and rejects any patch that changes production tokens, signatures, tests, project files, or an unselected documentation target.

The core does not own GitHub write side effects. A platform adapter may publish a validated proposal only after the audit, proposal, patch, and campaign-state boundaries are satisfied.

An issue ledger is not a core abstraction. Campaign and run state are platform-neutral machine contracts. A GitHub Issue may be the first state-storage adapter, but it must not define core identity, retry, batching, or lifecycle semantics.

## Historical bootstrap boundary

The initial bootstrap intentionally delivered only governance and a minimal .NET skeleton. M0 subsequently added provisional contracts and execution experiments without promoting the experiment hosts into production APIs.

## Product boundaries

- Deterministic audit is read-only and independent of a provider, model secret, GitHub write token, and declared network-dependent operation.
- Proposal generation may use an explicitly configured model provider but cannot write source or call GitHub.
- Patch rendering and validation are deterministic and offline.
- Campaign planning and state are platform-neutral and deterministic by default.
- GitHub mutations are optional adapter behavior with least-privilege permissions.
- Scheduling is caller-owned workflow configuration; ContractScribe does not promise provider-specific off-peak pricing.
- The planned initial proposal runtime uses an OpenAI-compatible protocol, with DeepSeek as the primary evaluation provider and MiMo as the compatibility-evaluation provider; support claims require the M3 executable provider corpus and evidence.
- Stable reusable prompt prefixes, bounded uncached input, and observable token economics are product requirements; provider cache retention is not product state.
- Automatic merge is not part of the initial product.

## Open decisions

- The M1 target surface and XML-documentation observation semantics, including assembly-visible targets.
- The first released payload channel used by the GitHub Action.
- The composite-versus-TypeScript Action host selected after payload and cross-platform invocation evidence exists.
- The exact OpenAI-compatible transport library, model identifiers, context limits, and cache-economics thresholds used by the M3 implementation.
- The exact campaign-state storage encoding used by the GitHub Issue adapter.
- Whether and when a Native AOT or child-process topology becomes eligible for reconsideration.

The loader and semantic-analysis process boundary is decided by [ADR 0002](../20_architecture/decisions/0002-process-topology.md): the M1 deterministic audit uses an in-process production loader, with child-process topologies deferred pending their eligibility experiment. The remaining open decisions are owned by the follow-up issues referenced from ADR 0001 and ADR 0002.
