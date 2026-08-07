# Pre-release engineering proportionality

## Purpose

Before the first downstream-consumable release, ContractScribe optimizes for learning, correctness, and reversible progress. It does not maintain release-grade process, compatibility, identity, or review machinery merely because that machinery may become useful later.

Pre-release status is not a waiver for security, privacy, destructive operations, evidence integrity, or an actual external compatibility promise. It is a requirement to use the minimum process that protects the concrete current risk.

This rule applies to architecture decisions, issue decomposition, contract lifecycle, validation and certification design, pull-request boundaries, independent review, closure records, and agent workflows.

## Default process budget

One independently acceptable executable outcome should ordinarily require:

- one implementation issue;
- one implementation pull request;
- one human merge decision;
- one independent review boundary for each distinct risk-bearing artifact or state transition;
- validation selected from the changed failure surface.

One coherent review may cover multiple related artifacts, transitions, and risks. Separate review boundaries only when a distinct authority, expertise, or independence requirement prevents one reviewer from accepting them together.

Repository visibility, a milestone label, a possible future consumer, a desire for perfect lineage, or the availability of automation does not by itself justify more process.

An existing frozen issue remains authoritative until it is explicitly amended. This rule does not silently reinterpret work already in progress; an agent reports the conflict and proposed simplification before changing the accepted issue contract.

## Reuse proven patterns before invention

Before creating a new architecture boundary, machine contract, protocol, persisted format, validation or security mechanism, distribution design, or workflow mechanism, inspect mature systems or standards that solve a materially similar problem. Prefer the smallest pattern with public evidence of real use over a repository-specific invention.

Use primary sources when available: standards, official documentation, source code, and public design records. A non-trivial decision records a bounded precedent check:

```text
Precedent check
- Similar systems or standards inspected:
- Pattern adopted:
- Parts intentionally omitted:
- Project constraint requiring any divergence:
```

The check is not a market survey and routine local implementation does not require it. Stop when one or a small number of sufficiently comparable, verifiable patterns establish a viable default. Established use is evidence for a starting point, not proof that a pattern fits ContractScribe; validate it against the current product and security boundaries. Do not copy compatibility layers, migration frameworks, enterprise governance, scale machinery, or operational infrastructure whose triggering constraints do not exist in ContractScribe. If the design diverges, name the concrete repository constraint that makes the established pattern insufficient. Pattern reuse does not authorize copying code or assets without satisfying their licenses.

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

## Mandatory process-complexity checkpoint

An architecture proposal, executable issue, or pull-request plan is not ready until it completes a process-complexity checkpoint when any of the following is planned:

- more than one pull request for one primary outcome, whether serial or parallel;
- more than one human merge before the outcome becomes usable;
- any required post-merge mutation stage beyond the human merge itself;
- repeated independent review of the same unchanged risk-bearing bytes;
- repeated full build, test, or platform matrices after a change limited to metadata outside the runtime, public contract, protected-input set, or content-identity preimage;
- compatibility, migration, coexistence, deprecation, or multiple artifact versions before a real consumer requires them;
- reopening a closed design, contract, or historical-authority issue because a later producer or consumer exposed a defect;
- manual ancestry, tracker, or closure ceremony that duplicates machine-verifiable repository or CI state;
- a new credential, paid resource, deployment, release, destructive action, privacy boundary, or security-sensitive authority.

The issue or decision record must contain this profile:

```text
Pre-release process profile
- Planned implementation PRs:
- Planned human merges:
- Independent review boundaries:
- Full validation repetitions:
- Post-merge mutations:
- Closed issues proposed for reopening:
- Concrete failure prevented by each item above the default budget:
- Simpler reversible alternative considered:
- Evidence that the simpler alternative is insufficient:
- Owner and removal or simplification condition:
```

Every item above the default budget must protect a distinct current failure boundary. If the evidence is absent, combine or remove the extra stage. An agent that encounters an unjustified item reports `PROCESS_COMPLEXITY_BLOCKER`, presents the minimum sufficient alternative, and does not silently normalize the heavier process.

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

Repository-only milestone evidence, an internal validation handoff, ordinary CI artifacts, or a later issue that may consume the same code are reversible development state. They do not justify a checked-in protected-input bundle, artifact lock, pending or accepted review record, review-only commit, or certification lifecycle before the first downstream-consumable release. Bind such validation to the exact repository revision and uniquely identified workflow run and attempt that produced it instead.

## Separate validation design from release freeze

When a validation contract, schema, harness, or adapter will be consumed by a production implementation, use three different decision points:

1. **Validation design gate** — early work fixes the validation goal, invariants, evidence ownership, anti-cheating boundary, platform matrix, and fail-closed behavior needed for implementation to begin.
2. **Integration evidence gate** — after the real producer and consumer path is executable, run the exact-revision path end to end and fix contradictions in the current implementation, schemas, fixtures, adapters, and validators without creating a persistent authorization bundle.
3. **Release freeze or certification gate** — the first downstream-consumable release decides whether its concrete external-consumer or irreversible-publication boundary requires fixed release inputs, a persistent content identity, independent acceptance, signing, or attestation.

Do not freeze an implementation-level schema, fixture projection, protected-input set, or final bundle identity during a pre-release milestone solely because the repository has an oracle, synthetic consumer, internal evidence handoff, or completed integration run. Before release, run one end-to-end satisfiability sweep proving that the production producer's output is accepted and interpreted by the production consumer, and that every applicable schema, semantic validator, fixture, observer, and harness required by the accepted design agrees with that same contract without vector- or oracle-specific special cases. Preserve the resulting evidence against its exact revision and run instead of turning it into authorization for later development.

Defects found by that sweep should be collected into the narrowest coherent correction. Do not automatically create and merge one correction pull request per serially exposed contradiction when the remaining producer-consumer surface can be audited together.

## Identity and certification

When a justified release or external boundary requires persistent identity, prefer a content identity such as a canonical digest, release bundle ID, or verified tree identity that protects the actual integrity boundary. Binding an acceptance record to a future squash-merge commit is allowed only when the commit identity itself is consumed or authorizes an irreversible action and a content identity is insufficient.

Before the first downstream-consumable release, repository-local validation uses the exact source commit, workflow revision, run and attempt, runner and toolchain observations, and run-generated manifest or artifact digests as provenance. Those digests may make one run internally consistent, but they are outputs of that run rather than a checked-in authorization object. They do not create a protected-input set that later pull requests must refresh, an accepted snapshot that later development must preserve, or a repository review record that authorizes execution.

Do not maintain a checked-in validation bundle, artifact lock, protected-input manifest, pending or accepted review fixture, review-only commit, or independent bundle-certification gate solely for pre-release milestone validation. Ordinary code review may bind the exact pull-request head; execution evidence binds the exact revision and workflow run that executed. The first downstream-consumable release gate decides whether its concrete distribution, publication, rollback, or external-consumer boundary requires a persistent release bundle, attestation, or signature. Introduce that machinery there from the then-current release inputs rather than carrying a placeholder through earlier milestones.

If a post-merge identity requirement structurally creates another pull request or human merge, the process-complexity checkpoint must compare it with a content-bound or externally stored attestation. The heavier option must identify the concrete failure that only the post-merge repository commit prevents.

When such a justified boundary uses independent acceptance, one accepted review should protect one unchanged content identity. A later metadata-only record must not cause another broad design review or full validation cycle unless it changes authorization semantics that those checks exercise.

## Validation proportionality

Select validation by the changed failure surface:

| Change class | Minimum expected validation |
| --- | --- |
| Runtime behavior, public contract, protected input, content-identity preimage, schema, fixture semantics, or platform behavior | Affected build, focused tests, contract or schema checks, relevant integration and platform coverage, and repository CI. |
| Review or provenance metadata outside the protected-input and content-identity preimages | Schema and canonicality checks, identity recomputation, authorization-gate tests, focused lifecycle coverage, diff checks, and existing repository CI. Do not duplicate a full local product test matrix without a concrete affected path. |
| Documentation or tracker metadata with no executable contract change | Documentation, link, and rendering checks appropriate to the changed surface; add remote readback only when the changed surface is a remote tracker record or rendered remote representation. |

Existing repository CI may still run a broader fixed matrix. That does not justify repeating the same broad matrix manually, adding another independent review, or turning a metadata-only change into a second implementation cycle.

A failing broader check is still investigated. Proportionality controls planned gates; it does not authorize ignoring observed failures.

## Closed work and later defects

A closed issue that established a historical design or contract baseline remains closed historical authority. A defect exposed later by a real producer, consumer, or integration belongs to the current failing boundary and is not automatically assigned back to the origin issue.

Before reopening closed work, report:

- the new defect and current failing producer-consumer path;
- whether the original acceptance contract was actually incomplete or the later consumer discovered a new requirement;
- impact on current and historical evidence;
- a new correction issue or current-owner fix as the default alternative;
- whether a broader satisfiability sweep can prevent another serial correction;
- the user's explicit decision when reopening would rewrite completed milestone ownership or historical authority.

Agents must not reopen a closed historical-authority issue, mutate its completion contract, or create a chain of replacement corrections without that decision.

### Recurrence stop rule

The first defect discovered after a freeze may receive a narrow correction when its boundary and impact are complete. Before creating a second serial correction pull request, proposing a second reopen, or returning to the same closed authority for another newly exposed contradiction, stop dependent work and classify the pattern as a process defect.

That stop requires:

- a complete producer-schema-validator-fixture-consumer satisfiability sweep over the remaining boundary;
- one aggregated finding list, including likely adjacent contradictions rather than only the latest failing assertion;
- identification of the lifecycle or ownership decision that allowed the boundary to freeze too early;
- comparison of one coherent correction, moving the freeze gate, or simplifying the identity/review design;
- an explicit user decision before another correction or reopen is published.

Do not publish a third isolated serial correction against the same frozen boundary unless new concrete evidence requires another cycle and the user explicitly accepts it. When the remaining failures could have been discovered together, aggregate them into one coherent correction instead of continuing the serial pattern.

The recurrence stop does not delay immediate containment or the narrowest safe correction of an observed security, privacy, legal, destructive-operation, publication-authority, or evidence-integrity failure. It stops normal dependent work and prevents the emergency correction from becoming justification for another unexamined serial cycle.

## Review and closure proportionality

Each independent review boundary must name a different artifact, authority transition, or failure that it protects. Repeating review against an unchanged head or content identity is only for closing a verified finding, not for accumulating confidence through ceremony.

Closure evidence should link machine-verifiable commits, checks, identities, and remaining owners. Do not manually reproduce long ancestry or tracker state when Git, GitHub, or a canonical manifest already proves it; record the concise query result and authoritative links instead.

If a process repeatedly delays implementation without discovering distinct defects, treat the process itself as a design problem. Open a bounded workflow or architecture correction rather than accepting repeated reopen, re-review, and re-certification as normal pre-release maintenance.
