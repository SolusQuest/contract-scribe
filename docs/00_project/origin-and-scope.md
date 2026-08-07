# Origin and scope

ContractScribe was extracted from downstream-specific documentation automation planning because the underlying problem is reusable across C# repositories. Downstream projects retain their own targets, terminology, language preferences, evidence escalation, schedules, and integration validation.

`CS1591` is a candidate signal, not a documentation-quality decision. Deterministic audit comes before proposal generation so that future results can be explained, replayed, and validated without a network, model, provider secret, or GitHub write token.

The product is not a general coding agent. Its proposal capability is the Documentation Scribe, a narrow repository-bounded role with project-owned context construction, semantic read tools, budgets, structured output, and evidence requirements. The Scribe cannot use a shell, edit arbitrary files, or publish changes.

A deterministic patch engine, not the model, owns source modification. It renders a structured documentation proposal at the selected declaration and rejects any patch that changes production tokens, signatures, tests, project files, or an unselected documentation target.

The core does not own GitHub write side effects. A platform adapter may publish a validated proposal only after the audit, proposal, patch, and campaign-state boundaries are satisfied.

GitHub-specific records are not core abstractions. Any durable campaign or run state needed by the implemented workflow remains platform-neutral, while a GitHub Issue may act as an adapter surface. M4 and M5 select the minimum state, identity, retry, batching, and reconciliation shapes from executable failure evidence.

## Historical bootstrap boundary

The initial bootstrap intentionally delivered only governance and a minimal .NET skeleton. M0 subsequently added provisional contracts and execution experiments without promoting the experiment hosts into production APIs.

## Product boundaries

- Deterministic audit is read-only and independent of a provider, model secret, GitHub write token, and declared network-dependent operation.
- Proposal generation may use an explicitly configured model provider but cannot write source or call GitHub.
- Patch rendering and validation are deterministic and offline.
- Campaign planning and state are platform-neutral and deterministic by default.
- GitHub mutations are optional adapter behavior with least-privilege permissions.
- Scheduling is caller-owned workflow configuration; ContractScribe does not promise provider-specific off-peak pricing.
- M3 selects the smallest proposal-provider transport and bounded evaluation set supported by current executable evidence; no provider name, compatibility corpus, or support claim is frozen before that refinement.
- Bounded provider input and observable usage, cost, and latency are evaluation concerns where the selected transport exposes them; provider cache retention and any prompt-prefix mechanism are never correctness state.
- Automatic merge is not part of the initial product.

## Open decisions

- The first released payload channel used by the GitHub Action.
- The composite-versus-TypeScript Action host selected after payload and cross-platform invocation evidence exists.
- The provider transport, model corpus, context limits, and practical economic thresholds selected by M3 executable evaluation.
- The minimum platform-neutral state and GitHub reconciliation shapes selected by M4 and M5 failure evidence.
- Whether and when a Native AOT or child-process topology becomes eligible for reconsideration.

The loader and semantic-analysis process boundary is decided by [ADR 0002](../20_architecture/decisions/0002-process-topology.md): the M1 deterministic audit uses an in-process production loader, with child-process topologies deferred pending their eligibility experiment. [ADR 0003](../20_architecture/decisions/0003-target-profiles-and-documentation-observation.md) accepted the M1 target-profile and direct XML-documentation observation semantics. Remaining open decisions belong to their implementing roadmap or release track.
