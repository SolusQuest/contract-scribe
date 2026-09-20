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

The first downstream-consumable release establishes the external compatibility freeze for every contract it exposes, including the public consumer configuration layer (`schemas/consumer-configuration/v1.schema.json`, documented in [Consumer configuration](../20_architecture/consumer-configuration.md)) and its payload defaults. The internal resolved `campaign-configuration-v1` authority document is an implementation boundary, not a released consumer contract. After that point, incompatible changes require new artifact versions and explicit compatibility or migration behavior.

Milestone evidence before release remains meaningful for the exact revision that produced it, but is not an active compatibility or authorization state. A later draft change runs the checks affected by that change and does not create a successor baseline unless a real coexistence or external-consumer boundary requires one.

## Release control plane (M6-R1)

The release control plane lives in `.github/workflows/release.yml` and is manually initiated (`workflow_dispatch`) only. Ordinary push and pull-request CI has no publication path: no push-triggered job can hold a publication credential, and the operational jobs additionally require a dispatch on `refs/heads/main`.

Three separately initiated operations exist:

- `prepare` — builds and freezes one release candidate from explicit `source_revision`, `payload_source_revision`, `wrapper`, and `release_version` inputs. It runs with `contents: read` only, never sees a publication credential, creates no public tag or Release, and advertises nothing. It emits a `release-candidate` artifact carrying the frozen `candidate.json` identity plus the payload-map pair content that must be recorded through a reviewed change (ADR 0006).
- `stage-draft` — separately authorized draft staging. It downloads the exact producer-run artifact (verified by producer run id, head sha, workflow path, artifact id, and server digest), re-verifies the candidate digest and the map/wrapper state **at `source_revision`** — the published tag target — and creates or adopts exactly one unpublished draft payload Release converging to the exact asset set. It runs under the maintainer-provisioned `release-candidate` environment and the repository-level `RELEASE_DRAFT_ENABLED` variable.
- `promote` — separately authorized publication of the exact qualified candidate. It requires the `release-publication` environment, the `RELEASE_PROMOTION_ENABLED` variable, the current explicit maintainer approval (`approval_reference`), the governance disposition reference (`governance_reference`, the #29 obligation), a completed-success CI run for `source_revision` containing a successful `action_packaged` job, and the qualified release/asset object ids from R2. Order is payload tag → publish draft → version tag.

Authorization properties: approval binds the exact `candidate_digest`; a workflow rerun is not fresh approval (`run_attempt` must be `1` and `actor` must equal `triggering_actor`); green CI alone, issue closure, stale environment values, artifact metadata, and dispatch inputs never authorize anything. The publication credential `RELEASE_PUBLICATION_TOKEN` reaches only the mutation step's environment as `CONTRACTSCRIBE_RELEASE_TOKEN`; every other credential use is the read-only `GITHUB_TOKEN` (`actions: read`/`contents: read`).

Publication mechanics: all versions are fixed `vX.Y.Z`; the payload tag `payload-<toolVersion>` points at the payload build revision and the version tag points at `source_revision`; both are created through `/git/refs` and must point directly at commit objects. Every mutation is pre-read → one write → exact readback; an ambiguous (lost) response permits exact-state adoption only, never a same-run retry, and recovery is a fresh separately authorized dispatch. No tag is moved, no same-version release or asset is replaced, and no release object is deleted. See [release-runbook.md](release-runbook.md) for the operator procedure and recovery matrix.
