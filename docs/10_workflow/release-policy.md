# Release policy

The repository has not published a downstream-consumable package, binary, GitHub Action, or release. Source-based development and synthetic validation may continue before publication, but no pre-release validation artifact is advertised as an installable or supported product.

ADR 0001 selected a framework-dependent semantic execution baseline for the tested profile, and ADR 0002 selected an in-process M1 topology. Native AOT, self-contained publication, and child-process topologies are deferred alternatives, not preferred release targets. The payload channel and artifact layout remain separate distribution decisions.

ADR 0004 selects GitHub-hosted Ubuntu x64 as the sole required M1-M5 pre-release source-validation runner and the planned initial M6 target. It is not a released support claim. Native Windows and other unvalidated environments remain unsupported and non-gating; the future M6 wrapper must reject unsupported runners clearly and quickly.

Before the first downstream-consumable release, NuGet package or GitHub Action publication, or merge of external code contributions, the project must make and record a license and contribution-policy decision. Until then, do not solicit contributions or encourage third-party adoption.

## Release gates

The first consumable GitHub Action release requires:

- a recorded license and contribution-policy disposition;
- authority and third-party inventory evidence sufficient for that disposition;
- a selected payload distribution channel with semantic-fidelity evidence;
- a selected composite or TypeScript/JavaScript Action host with executable acquisition, supported-runner invocation, and unsupported-runner rejection evidence;
- a validated thin GitHub Action wrapper bound to the exact payload identity and free of duplicated campaign, ledger, patch, or GitHub-publication logic;
- least-privilege permissions and an explicit secret model;
- source, workflow, toolchain, artifact, and release provenance;
- install, pin, update, rollback, and retirement behavior;
- a consumer-repository smoke;
- maintainer approval of the release candidate.

The exact release candidate must pass these gates on GitHub-hosted Ubuntu x64 against a target repository whose caller-prepared prerequisites and design-time load satisfy ADR 0004. Green source CI or successful pre-release validation does not constitute release approval.

## Contract compatibility freeze

Repository visibility does not by itself freeze a draft machine contract. Pre-release contract revisions follow [Contract lifecycle](../00_project/contract-lifecycle.md) and are identified by commit plus artifact version.

The first downstream-consumable release establishes the external compatibility freeze for every contract it exposes. After that point, incompatible changes require new artifact versions and explicit compatibility or migration behavior.

Milestone evidence before release remains meaningful for the exact revision that produced it, but is not an active compatibility or authorization state. A later draft change runs the checks affected by that change and does not create a successor baseline unless a real coexistence or external-consumer boundary requires one.

## Scheduling and provider cost

The GitHub Action may be invoked by a caller-owned schedule, manual dispatch, or another workflow. The repository does not promise that any wall-clock period has lower provider pricing. Provider batch modes, pricing inputs, deadlines, and cost ceilings are adapter configuration and recorded run provenance.
