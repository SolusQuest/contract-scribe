# Origin and scope

ContractScribe was extracted from downstream-specific documentation automation planning because the underlying problem is reusable across C# repositories. Downstream projects retain their own targets, terminology, language preferences, evidence escalation, schedules, and integration validation.

`CS1591` is a candidate signal, not a documentation-quality decision. Deterministic audit comes before proposal generation so that future results can be explained, replayed, and validated without a network, model, provider secret, or GitHub write token.

The core does not own GitHub write side effects. A future platform adapter may publish a proposal only after the core contracts and safety boundaries are established.

An issue ledger is not a core abstraction. If state becomes necessary, the project will first define a platform-neutral, versioned state contract; an adapter can then choose an appropriate storage surface.

## Non-goals for bootstrap

- Roslyn or MSBuild workspace loading.
- Native AOT feasibility claims.
- Audit JSON or policy schemas.
- Provider/model integration, secrets, or automatic pull requests.
- GitHub Action distribution, package publishing, or external contribution intake.

## Open decisions

- Loader and semantic-analysis process boundary.
- Feasible Native AOT distribution path.
- Initial distribution form.

These decisions require the M0 experiments and ADR described in the roadmap.
