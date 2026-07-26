# Semantic foundation

## Purpose

M0 established ContractScribe's semantic foundation rather than a production audit implementation. The durable result is a shared language for documentation requirements, classification targets, bounded evidence, deterministic audit judgments, identity, ordering, and provenance. Production stages extend that language through capability-specific contracts instead of redefining it independently.

The semantic foundation is an architecture interpretation of the existing contract families and validated execution evidence. It is not an additional machine artifact, a general graph protocol, or an external compatibility promise.

## Contract roles

The three M0 contract families own different kinds of authority:

| Contract | Role | Question answered |
| --- | --- | --- |
| [Policy/Configuration](contracts/policy-configuration-v1.md) | Normative | Which run-level target profile and documentation expectation apply to the supplied project plus repository-source or generated-output identity? |
| [Symbol and Evidence Taxonomy](contracts/symbol-evidence-taxonomy-v1.md) | Descriptive | What target, component, relation, provenance, support state, and bounded evidence exist? |
| [Audit Result](contracts/audit-result-v1.md) | Judgment | What deterministic outcome follows from the policy, classification, documentation observation, and evidence? |

The relationship is:

```text
normative policy expectation
        +
descriptive classification and bounded evidence
        ↓
deterministic audit judgment
```

Taxonomy is the shared descriptive vocabulary, Policy is the caller-intent boundary, and Audit Result is the deterministic decision handoff. None substitutes for the others, and their importance is not expressed as a ranking.

## Product contract chain

The product evolves as a chain of versioned artifacts and validated transitions:

```text
repository snapshot + policy
  -> classification + bounded audit evidence
  -> canonical audit result
  -> snapshot-scoped work plan
  -> repository/scope context + target evidence + style profile
  -> structured documentation proposal
  -> validated documentation patch
  -> campaign checkpoint + publication record
```

Each transition has its own authority and failure boundary:

- audit determines whether a target can be classified and whether a policy judgment is supported;
- planning selects a stable, budgeted prefix of audit work;
- the Documentation Scribe proposes content for selected targets but does not select new audit targets;
- the patch engine resolves the exact declaration and decides whether a proposal can become a source change;
- campaign state owns resume, retry, snapshot lineage, and budgets;
- the platform adapter owns optional publication side effects.

No single artifact is the source of truth for every stage. Identities and provenance bind the chain together while each owning contract remains authoritative for its own boundary.

## M0 semantic assets

M0 produced five connected classes of durable input:

### Semantic language

- Policy/Configuration v1 expresses documentation expectations and deterministic precedence.
- Symbol and Evidence Taxonomy v1 defines classification, identity, relations, provenance, support state, skips, and evidence vocabulary.
- Audit Result v1 combines those inputs into canonical compliant, violation, or skipped judgments.

### Determinism

- closed identifiers and validation stages;
- canonical UTF-8 encoding and property order;
- ordinal target, relation, contribution, and evidence ordering;
- public schemas, registries, manifests, fixtures, invalid vectors, and test-only conformance oracles;
- byte-identical comparison boundaries for fixed inputs.

### Identity and evidence

- commit-pinned draft contract baselines;
- explicit compilation context and repository-revision provenance;
- `SymbolRef`, classification records, locators, spans, hashes, truncation, and evidence budgets;
- fail-closed handling when identity, context, provenance, or bounded evidence is unavailable.

### Execution evidence

- framework-dependent Roslyn/MSBuild discovery, loading, and deterministic semantic projection on the primary synthetic fixture;
- a bounded historical Native AOT not-feasible result for the exact tested profile, without a general impossibility claim;
- independent Ubuntu and Windows X64 validation against a separately authored fixture and oracle.

### Architecture decisions

- [ADR 0001](decisions/0001-loader-and-distribution-boundary.md) selects the framework-dependent semantic execution baseline within the validated M0 matrix;
- [ADR 0002](decisions/0002-process-topology.md) selects the in-process M1 production topology as an evidence-based decision inference, with implementation and executable production validation still owned by M1.

The experiment hosts and `semantic-payload.json` remain test-only evidence assets. They are not production contracts or migration predecessors.

## Downstream reuse and ownership

Later milestones reuse the semantic foundation while adding independently owned contracts:

| Stage | Reuses from the foundation | Adds or completes |
| --- | --- | --- |
| M1 deterministic audit | Policy, classification, evidence vocabulary, Audit Result, canonical rules, validated execution baseline | Initial target profiles, production documentation observation, production run envelope, host and CLI behavior |
| M2 patch engine | Selected `SymbolRef`, source locator and identity, evidence references, canonical declaration | Patch Request, rendering rules, Patch Validation Result, source-change invariants |
| M3 Documentation Scribe | Selected audit target, semantic relations, evidence identity and authority | Context Pack, Style Profile, Proposal Request, Documentation Proposal, provider-run provenance |
| M4 campaign | Canonical Audit Result, stable target ordering, contract and evidence provenance | Snapshot, Work Plan, Batch, Campaign State, cursor, retry, and budget contracts |
| M5 GitHub workflow | Validated patch and campaign transition identities | State-adapter encoding, publication plan, reconciliation and publication records |
| M6 Action release | Production CLI, publication behavior, payload identity | Wrapper provenance, release inputs, consumer compatibility and installation evidence |

Reusing a vocabulary does not transfer authority. For example, M3 may refer to a taxonomy relation, but it cannot redefine reachability or turn a skipped audit target into selected work.

## Audit evidence and Scribe context

Audit evidence and Scribe context serve different decisions.

Audit evidence proves that a bounded audit judgment is justified. Establishing `documentation.absent`, for example, requires complete declaration evidence and rejection of contradictory documentation evidence. The audit bundle can be sufficient even when it contains too little information to write useful documentation.

Scribe target evidence supports content generation. It may include signature details, nullability, constraints, interface or base documentation, implementation excerpts, tests, usage examples, maintained repository documentation, and nearby style examples. It shares identity, repository-relative locator, source revision, hash, authority, and truncation principles with audit evidence, but it is not the same artifact.

The Scribe starts from an allowlisted context pack and may obtain additional evidence only through bounded semantic and repository-read tools. It never receives unrestricted repository access. Additional reads may justify a proposal or a structured insufficient-evidence skip; they do not authorize the Scribe to change the audit outcome, add an unselected target, or mutate source.

## Identity and provenance

### Symbol identity

`SymbolRef` provides deterministic identity within a pinned compilation context. It is not a permanent entity identifier across arbitrary repository revisions. A rename, containing-type change, signature change, target-framework change, or compilation-context change may produce a different documentation comment ID or context reference.

Cross-snapshot continuation therefore creates a new snapshot, reruns audit, and applies explicit reconciliation. It never assumes that a prior `SymbolRef`, method name, or array position proves continuity.

### Result and execution identity

A canonical Audit Result digest identifies content, not the complete execution context. Two different base commits may legitimately produce byte-identical Audit Result artifacts. Snapshot-scoped work-plan, cursor, checkpoint, batch, operation, and pull-request-generation identities remain distinct and fail closed when replayed under another snapshot.

Draft contract artifacts also bind a separate contract-baseline or provenance identity when they persist, transfer, or are consumed across repository revisions. Integer artifact versions alone do not identify exact pre-release semantics. See [Contract lifecycle](../00_project/contract-lifecycle.md).

## Work-unit terminology

The architecture distinguishes related but non-interchangeable terms:

| Term | Meaning |
| --- | --- |
| Classification target | An independent taxonomy target represented by `TargetClassification` and identified by `SymbolRef`. |
| Taxonomy component | A subordinate semantic object such as a parameter, type parameter, return, value, or accessor represented by `ComponentClassification`. |
| Documentation target | A canonical declaration that can own a complete XML documentation comment and may be selected for proposal and patch processing. |
| Documentation block | The complete XML documentation comment attached to one canonical declaration; the initial campaign work unit. |
| Proposal field | Structured content such as summary, parameter text, return or value text, exception documentation, or remarks. |

Summary and remarks are proposal fields, not `ComponentClassification` records. Parameter and return proposal fields may bind taxonomy component identities without making every field an independent campaign work unit.

The initial work unit remains one complete documentation block per selected documentation target. Changing work-unit granularity requires an explicit campaign and proposal-contract decision.

## Representation boundary

A structured Documentation Proposal is an XML-documentation-specific intermediate representation between model-assisted writing and deterministic rendering. This compiler-like separation is useful: the model does not own trivia, XML escaping, tag order, indentation, or the source diff.

It is not a language-neutral abstract syntax tree. Supporting another documentation format or programming language may require changes to taxonomy, target identity, proposal shape, renderer, locators, and validation invariants rather than only replacing a renderer.

## Non-claims

The current architecture does not claim:

- a general `EvidenceGraph` artifact, graph store, or graph-query protocol;
- that Audit Result is the only authoritative identity or state input;
- that `SymbolRef` survives arbitrary source changes;
- that the Scribe sees only one prebuilt Evidence Bundle or cannot perform bounded repository reads;
- that changing a renderer alone provides another language or documentation format;
- that ADR 0002 already proves the complete production topology;
- that summary, parameter, return, remarks, and other proposal fields are separate campaign targets.

The architecture is evidence-centered and graph-shaped because symbols, components, relations, and evidence references connect to one another. A general graph contract is introduced only if a later independently acceptable capability demonstrates that the existing typed contracts are insufficient.

## Evolution

Issue #35 establishes the coordinated pre-release v1 Policy, Taxonomy, and Audit baseline for target-profile and documentation-observation semantics. The [baseline inventory](contracts/pre-release-v1-baseline.md) preserves the role separation above and binds the normative docs, schemas, registries, fixtures, oracles, dependent contracts, validation, and downstream production disposition.

M2 and later contract families extend the chain rather than silently adding meaning to M0 artifacts. A new cross-stage abstraction is justified only by a concrete consumer, an independently testable contract, and evidence that the existing boundaries cannot express the required behavior.
