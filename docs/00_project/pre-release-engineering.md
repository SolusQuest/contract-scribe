# Pre-release engineering proportionality

## Purpose

Before the first downstream-consumable release, ContractScribe optimizes for learning, correctness, and reversible progress. It does not maintain release-grade process, compatibility, identity, or review machinery merely because that machinery may become useful later.

Pre-release status is not a waiver for security, privacy, destructive operations, evidence integrity, or an actual external compatibility promise. It is a requirement to use the minimum process that protects the concrete current risk.

This rule applies to architecture decisions, issue decomposition, contract lifecycle, validation and certification design, pull-request boundaries, independent review, closure records, and agent workflows.

## Default process budget

One independently acceptable executable outcome should ordinarily require:

- zero or one implementation issue, depending on whether separate planning or coordination is useful;
- one implementation pull request;
- one human merge decision;
- one coherent review of the complete change;
- validation selected from the changed failure surface.

One coherent review covers the related code, contracts, tests, documentation, and state transitions in the pull request. Add another independent review only when a distinct current authority cannot be accepted through that review, such as release authorization, a security or privacy authority, a destructive external mutation, a legal obligation, or a genuinely independent attestation.

Repository visibility, a milestone label, a possible future consumer, a desire for perfect lineage, or the availability of automation does not by itself justify more process.

An issue defines its primary outcome, acceptance boundary, and product decisions; it does not freeze an implementation mechanism merely because the body described one. An implementation may use a simpler design that preserves those decisions and acceptance behavior. Amend the issue before implementation only when the simplification changes product semantics, external commitments, security or privacy boundaries, destructive-operation authority, dependencies, or the independently acceptable outcome.

## Reuse proven patterns before invention

Before creating a durable architecture boundary, externally consumed contract, persisted format, security mechanism, distribution design, or irreversible workflow authority, inspect mature systems or standards that solve a materially similar problem. Prefer the smallest pattern with public evidence of real use over a repository-specific invention. Routine internal implementation, ordinary tests, reversible draft schemas, and local refactoring do not require a recorded research artifact.

Use primary sources when available: standards, official documentation, source code, and public design records. When the precedent materially affects a durable decision, record a bounded check:

```text
Precedent check
- Similar systems or standards inspected:
- Pattern adopted:
- Parts intentionally omitted:
- Project constraint requiring any divergence:
```

The check is not a market survey, issue-body section, or universal gate. Stop when one or a small number of sufficiently comparable, verifiable patterns establish a viable default. Established use is evidence for a starting point, not proof that a pattern fits ContractScribe; validate it against the current product and security boundaries. Do not copy compatibility layers, migration frameworks, enterprise governance, scale machinery, or operational infrastructure whose triggering constraints do not exist in ContractScribe. If a durable design diverges, name the concrete repository constraint that makes the established pattern insufficient. Pattern reuse does not authorize copying code or assets without satisfying their licenses.

### Precedent check for this rule

- **Similar systems or standards inspected:** [GitHub flow](https://docs.github.com/en/get-started/using-github/github-flow), Google's [Small CLs](https://google.github.io/eng-practices/review/developer/small-cls.html) and [code review standard](https://google.github.io/eng-practices/review/reviewer/standard.html), and [Semantic Versioning 2.0.0](https://semver.org/).
- **Patterns adopted:** use a lightweight branch, pull request, review, and merge path; keep one change self-contained with the related validation needed to understand it; accept a change when it materially improves the current system rather than delaying for theoretical perfection; and treat initial-development interfaces as unstable until an external compatibility boundary is declared.
- **Parts intentionally omitted:** GitHub deployment steps, Google-scale stacked-change and reviewer-organization practices, and released-version deprecation or migration machinery. ContractScribe does not inherit those mechanisms without their current deployment, team-scale, or consumer constraints.
- **ContractScribe-specific divergence:** externally consumed or irreversible production evidence may require content identity, fail-closed validation, or authority separation beyond an ordinary code review. Those additions remain only where the issue records the concrete evidence-integrity failure they prevent. Repository-only milestone evidence, ordinary CI artifacts, and a possible later release do not qualify by themselves.

## Maintain one current pre-release version

Before the first downstream-consumable release, ContractScribe maintains one current production implementation per capability and one current shape for each draft contract by default. When a draft API, schema, fixture, configuration, identifier, or implementation path is superseded, replace or delete it in the same coherent change that updates its producers, consumers, validators, tests, and documentation.

Do not add a compatibility layer, old-version reader or writer, deprecated alias, dual-read or dual-write path, migration framework, silent fallback to superseded behavior, or multiple active artifact versions merely to preserve an unreleased repository revision. A one-time mechanical repository rewrite is allowed when needed to update checked-in state; it must not leave a permanent migration surface behind. Unsupported obsolete shapes fail closed instead of silently selecting the old path.

Compatibility before release requires evidence of a current boundary that cannot be updated atomically, such as a real external consumer, persisted state that cannot be discarded, an explicit public commitment, or an irreversible data, authorization, security, or publication risk. The owning issue or decision records that boundary, why a coordinated replacement is insufficient, the compatibility lifetime, its owner, and the condition for removing it. Possible future consumers and generic best practice are insufficient.

This rule removes obsolete active behavior, not historical truth. Git history and closed tracker records are not rewritten. An immutable evidence artifact remains in the current tree only when another current claim or external obligation explicitly depends on it; retention does not make it a supported runtime entrypoint. It also does not prohibit test-only alternatives, intentional product defaults, or fail-closed error handling defined by the current contract; it prohibits compatibility fallback to superseded behavior.

## Exception-only process-complexity checkpoint

Record a process exception only when an architecture proposal, executable issue, or pull-request plan includes one of the following:

- more than one pull request for one primary outcome, whether serial or parallel;
- more than one human merge before the outcome becomes usable;
- any required post-merge mutation stage beyond the human merge itself;
- an additional independent review beyond the coherent change review;
- repeated full build, test, or platform matrices after a change limited to metadata outside the runtime, public contract, protected-input set, or content-identity preimage;
- compatibility, migration, coexistence, deprecation, or multiple artifact versions before a real consumer requires them;
- reopening a closed design, contract, or historical-authority issue because a later producer or consumer exposed a defect;
- manual ancestry, tracker, or closure ceremony that duplicates machine-verifiable repository or CI state;
- a custom validation evidence schema, manifest, identity, aggregator, validator, mutation corpus, self-test framework, publication record, or certification lifecycle in addition to ordinary tests and CI;
- a new credential, paid resource, deployment, release, destructive action, privacy boundary, or security-sensitive authority.

The issue, decision, or pull-request plan needs only this concise note:

```text
Pre-release process exception
- Extra stage or machinery:
- Current failure it prevents:
- Why the simpler reversible path is insufficient:
- Removal or simplification condition, when applicable:
```

Every item above the default budget must protect a distinct current failure boundary. If the evidence is absent, combine or remove the extra stage. A missing template is not a blocker. An agent reports `PROCESS_COMPLEXITY_BLOCKER` only when an actual planned gate, mutation, or maintained mechanism is unjustified, and presents the minimum sufficient alternative.

When an agent has prior authorization to continue deterministic implementation-local work without another user decision, it may do so, but it must stop before publishing or expanding an unjustified multi-stage process when correction would amend an accepted product, contract, review, or workflow decision.

## Evidence that may justify additional process

Additional process is justified only by current evidence such as:

- a supported external consumer or an explicit compatibility commitment;
- incompatible persisted state that must coexist or migrate;
- an irreversible deployment, release, publication, destructive mutation, or externally consumed evidence record;
- a concrete security, privacy, credential, legal, or regulatory boundary;
- authorization of externally consumed or irreversible production evidence whose integrity depends on an independently reviewed content identity;
- a reproduced failure that a simpler reversible measure does not detect or contain.

The justification must name the exposed consumer or state, the triggering sequence, expected versus unsafe behavior, and why the proposed gate prevents that failure. Generic best practice, future-proofing, or possible later reuse is insufficient.

Repository-only milestone evidence, an internal validation handoff, ordinary CI artifacts, or a later issue that may consume the same code are reversible development state. They do not justify a checked-in Host Validation or release-authorizing protected-input bundle, artifact lock, pending or accepted review record, review-only commit, or certification lifecycle before the first downstream-consumable release. Host Validation and other workflow-based validation bind the applicable repository and workflow revisions and uniquely identified run and attempt that produced the evidence. Other pre-release capabilities bind the minimum provenance defined by their owning contract; they do not inherit GitHub Actions identity merely because Host Validation uses it.

## Separate validation design from release freeze

When validation will be consumed by a production implementation, separate current implementation evidence from a later release freeze:

1. Before the real producer and consumer are executable, define only the validation goal, essential invariants, concrete failure surface, and minimum platform coverage needed to implement safely.
2. Once the real path is executable, test the affected producer-consumer path end to end and correct the current implementation, fixtures, and tests in one coherent change.
3. At the first downstream-consumable release, decide whether a concrete external-consumer or irreversible-publication boundary requires fixed release inputs, persistent identity, independent acceptance, signing, or a signed attestation.

Do not freeze an implementation-level schema, fixture projection, protected-input set, or final bundle identity during a pre-release milestone solely because the repository has an oracle, synthetic consumer, internal evidence handoff, or completed integration run. Test the concrete affected path deeply enough to establish current correctness; do not turn a bounded correction into a universal satisfiability audit unless observed failures show that the broader surface is coupled. Preserve resulting evidence against its exact revision and run instead of turning it into authorization for later development.

Defects found by affected-path validation should be collected into the narrowest coherent correction. Do not automatically create and merge one correction pull request per serially exposed contradiction when the remaining coupled surface can be checked together.

## Identity and certification

When a justified release or other concrete integrity boundary requires persistent identity, prefer a content identity such as a canonical digest, release bundle ID, or verified tree identity that protects the actual boundary. Binding an acceptance record to a future squash-merge commit is allowed only when the commit identity itself is consumed or authorizes an irreversible action and a content identity is insufficient.

Before the first downstream-consumable release, workflow-based repository validation uses the applicable source commit, workflow revision, run and attempt, and required job conclusions as provenance. Record runner, toolchain, or artifact digests only when they affect the claim or protect an actual artifact transfer. Those facts are outputs of a run rather than checked-in authorization objects. They do not create a protected-input set that later pull requests must refresh, an accepted snapshot that later development must preserve, or a repository review record that authorizes execution.

This rule does not prohibit exact-revision milestone evidence, bounded public or private smoke attestations, or an identity demonstrated to be necessary for deterministic replay, stale-state rejection, or idempotent external mutation. Define such an identity when the implementing milestone exposes the concrete state transition; do not reserve a family of identities in an earlier roadmap as placeholder architecture.

Do not maintain a checked-in Host Validation or release-authorizing bundle, artifact lock, protected-input manifest, pending or accepted review fixture, review-only commit, or independent bundle-certification gate solely for pre-release milestone validation. Ordinary code review may bind the exact pull-request head; workflow execution evidence binds the exact revision and run that executed. The first downstream-consumable release gate decides whether its concrete distribution, publication, rollback, or external-consumer boundary requires a persistent release-authorizing bundle, signed release attestation, release protected-input identity, or signature. Introduce that machinery there from the then-current release inputs rather than carrying a placeholder through earlier milestones.

If a post-merge identity requirement structurally creates another pull request or human merge, the process-complexity checkpoint must compare it with a content-bound or externally stored attestation. The heavier option must identify the concrete failure that only the post-merge repository commit prevents.

When such a justified boundary uses independent acceptance, one accepted review should protect one unchanged content identity. A later metadata-only record must not cause another broad design review or full validation cycle unless it changes authorization semantics that those checks exercise.

## Validation proportionality

Select validation by the changed failure surface:

| Change class | Minimum expected validation |
| --- | --- |
| Runtime behavior, public contract, schema, fixture semantics, or platform behavior | Affected build, focused tests, contract or schema checks, relevant integration and platform coverage, and repository CI. |
| Run-local metadata or an artifact envelope used for a real cross-job transfer | Focused producer-consumer, canonicality, stale-state, or digest checks for that transfer, plus existing repository CI. Do not duplicate a full local product test matrix without a concrete affected path. |
| Documentation or tracker metadata with no executable contract change | Documentation, link, and rendering checks appropriate to the changed surface; add remote readback only when the changed surface is a remote tracker record or rendered remote representation. |

Existing repository CI may still run a broader fixed matrix. That does not justify repeating the same broad matrix manually, adding another independent review, or turning a metadata-only change into a second implementation cycle.

A failing broader check is still investigated. Proportionality controls planned gates; it does not authorize ignoring observed failures.

## Validation infrastructure is not a second product

Prefer ordinary unit and integration tests, production entry points, fixed synthetic fixtures, the repository's existing CI jobs, and GitHub's source, workflow, run, attempt, and job facts. A validation tool may add a small run-local envelope when one job actually transfers an artifact to another, but it should not reproduce the CI system as a separate canonical protocol.

Custom evidence schemas, canonical identities, manifests, aggregators, validators, mutation corpora, self-test frameworks, publication records, and lifecycle commands require a concrete current consumer or failure that ordinary tests and CI metadata cannot protect. When that evidence is absent, delete or collapse the machinery instead of preserving it as reusable infrastructure.

Passing tests establish the behavior exercised at that revision; they do not need a second machine contract proving that the test harness itself was reviewed. Security-sensitive observers may keep focused adversarial tests for the real risk they detect, but those tests remain part of the product's validation rather than a general certification platform.

## Retire completed experiments

An experiment may be strict while it is deciding an architecture question. Once a production path supersedes it, remove the experiment from ordinary CI and delete current-tree manifests, allowlists, compatibility modes, tombstone entry points, validators, and tests whose only purpose is to preserve the retired experiment machinery.

Keep reusable production fixtures or regression cases only after moving them under the current production owner. Preserve the experiment's question, tested conditions, result, limitations, and exact historical commit in concise documentation and Git history. A possible future reconsideration does not justify keeping the old experiment executable or coupled to current source.

## Closed work and later defects

A closed issue records the design, contract, implementation, or evidence accepted at that historical revision. It is not current implementation authority. A defect exposed later by a real producer, consumer, or integration belongs to the current failing boundary and is not automatically assigned back to the origin issue.

Before reopening closed work, report:

- the new defect and current failing producer-consumer path;
- whether the original acceptance contract was actually incomplete or the later consumer discovered a new requirement;
- impact on current and historical evidence;
- a new correction issue or current-owner fix as the default alternative;
- whether a broader satisfiability sweep can prevent another serial correction;
- the user's explicit decision when reopening would rewrite completed milestone ownership or historical authority.

Agents must not reopen a closed historical issue, mutate its completion record, or create a chain of replacement corrections without that decision.

### Recurrence stop rule

When repeated defects expose the same current producer-consumer boundary, combine the known related fixes under the current owner instead of reopening historical issues or creating one correction per assertion.

Before materially expanding the correction, identify:

- the concrete coupled path and known adjacent failures;
- the current owner and one coherent correction boundary;
- whether an obsolete lifecycle, identity, or validation layer should be deleted rather than repaired;
- any product, external-commitment, security, privacy, destructive-operation, dependency, or scope decision that requires the user's explicit choice.

The current owner may implement the coherent correction without a new ceremony when those decisions do not change. Require a broader audit or explicit user gate only when observed coupling or scope makes it necessary, not merely because a previous defect existed.

The recurrence stop does not delay immediate containment or the narrowest safe correction of an observed security, privacy, legal, destructive-operation, publication-authority, or evidence-integrity failure. It stops normal dependent work and prevents the emergency correction from becoming justification for another unexamined serial cycle.

## Review and closure proportionality

Review the coherent change as a whole. A follow-up review verifies concrete findings against the new head; it does not restart separate full reviews for each file, artifact, issue body, or unchanged decision.

Closure evidence should link machine-verifiable commits, checks, identities, and remaining owners. Do not manually reproduce long ancestry or tracker state when Git, GitHub, or a canonical manifest already proves it; record the concise query result and authoritative links instead.

If a process repeatedly delays implementation without discovering distinct defects, treat the process itself as a design problem and simplify it in one coherent change. Use a separate workflow or architecture issue only when independent planning or a product decision is genuinely needed.
