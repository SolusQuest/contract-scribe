# ADR 0004: Initial runner platform support

Status: Accepted for pre-release validation; planned M6 target, not a released support claim

Date: 2026-08-12

Decision owner: Repository owner; this ADR becomes a repository decision through human-reviewed PR merge.

## Context

ContractScribe has not published a downstream-consumable package, binary, GitHub Action, or release. M1 through M5 validate source-built product behavior, while M6 will create the first consumable Action. Green source CI is therefore pre-release engineering evidence, not release qualification or a compatibility promise.

Ordinary CI previously required both GitHub-hosted Ubuntu and native Windows. The first `main` run after Issues #80 and #83 completed Ubuntu in about ten minutes, including an 8-minute-44-second integration step, while Windows entered cascading CoreCLR startup failures and remained active until cancellation after about 46 minutes. The preceding successful run also took about 22 minutes 51 seconds on Windows versus 14 minutes 55 seconds on Ubuntu. Maintaining native Windows as a required cell would add platform-specific serialization, hang diagnostics, stress coverage, and substantially longer feedback before any current consumer requires that platform.

The first intended downstream consumer is a GitHub Actions workflow. The initial support boundary should match that delivery surface precisely without claiming every Linux environment or preserving an unsupported runner as a release gate.

## Decision

The initial runner matrix is:

| Environment | Status |
| --- | --- |
| GitHub-hosted Ubuntu x64 (`ubuntu-latest`) | Sole required M1-M5 pre-release source-validation runner and planned initial M6 supported-runner target; not yet a released or release-qualified support claim |
| WSL2 on a Windows host | Suggested local Linux route; not independently qualified and not native-Windows evidence |
| Native GitHub-hosted Windows | Unsupported and non-gating; it may happen to run but carries no CI, maintenance, compatibility, or support obligation |
| macOS, ARM64, other Linux environments, containers, and self-hosted runners | Unvalidated and outside the initial matrix |

M1 through M5 use GitHub-hosted Ubuntu x64 as the sole required repository CI and pre-release production-validation platform. `ubuntu-latest` is a moving GitHub-hosted runner label; changes to its resolved image, SDK, runtime, MSBuild, or other relevant toolchain facts can require fresh evidence for the claim that consumes them.

M6 may claim released Ubuntu support only after one exact release candidate passes the selected payload acquisition and integrity path, Action-host and wrapper/payload composition, a supported target-repository consumer smoke, release controls, and maintainer approval. Ordinary source or candidate CI cannot substitute for those gates.

## Target-repository boundary

Initial target-repository support is limited to an explicit C# `.sln`, `.slnx`, or `.csproj` input whose caller-prepared restore and build prerequisites and design-time MSBuild/Roslyn loading succeed under the supported SDK policy on the selected GitHub-hosted Ubuntu x64 runner.

Repositories that require native-Windows-only workloads, MSBuild targets, tooling, filesystem behavior, process behavior, or other Windows-host assumptions are outside the initial boundary. A Windows-targeted target framework is not excluded solely by its name when the required targeting packs, caller-prepared prerequisites, and design-time load succeed on the supported Ubuntu runner.

## Implementation boundary

- Ordinary required CI has one Ubuntu `validate` job. It keeps separate complete fast and integration suites, separate TRX artifacts, and final outcome aggregation.
- The integration step has a 25-minute outer timeout and no automatic retry. A timeout remains an integration failure, and already-produced TRX remains eligible for the ordinary `always()` upload.
- The Windows-only thirty-iteration causal-topology qualification guard is retired. Linux lifecycle, cleanup, cancellation, timeout, process-observation, and pipe-closure coverage remains.
- The retained three-worker integration schedule and named process lanes are observed Ubuntu scheduling, not proof that every subprocess launch is globally bounded. New process tests still identify their real filesystem, MSBuild, environment, process-inventory, signal, and other shared boundaries.
- Low-cost production OS branches and platform-specific signal or process handling remain when they express product behavior. Their presence does not establish support.
- The current source CLI does not gain an eager platform guard. When M6 implements the wrapper, it must reject unsupported runners clearly and quickly before presenting them as a supported Action environment.
- No nightly, manual, optional, or shadow Windows qualification workflow is created.

## Historical evidence

Windows observations in ADR 0001, ADR 0002, completed issues and pull requests, validation records, and past workflow runs remain true for their exact revisions, runner conditions, and evidence boundaries. They are not rewritten and do not create an ongoing Windows support obligation.

The current Host calibrated bounds, their machine-readable authority, digest, and direct production regression coverage remain current implementation inputs. The recorded Windows calibration measurements are revision-bound historical observations; current required platform confidence comes from direct production tests and ordinary exact-revision Ubuntu CI.

## Re-entry conditions

Native Windows may re-enter required pre-release CI only through a dedicated decision with:

- a concrete current consumer need or reproduced engineering failure;
- an explicit owner;
- representative fixtures for the affected repository and runtime shapes;
- bounded expected maintenance and feedback cost; and
- stable CI evidence over the affected paths.

A released M6 Windows support claim additionally requires exact-candidate payload acquisition and integrity evidence, wrapper/payload composition, supported target-repository consumer smoke, release controls, and maintainer approval. Other operating systems, architectures, runner types, and Linux environments require equivalent qualification appropriate to their claimed boundary.

## Consequences

The repository removes native Windows from its ordinary required CI and avoids #84's Windows-specific serialization and hang-diagnostic machinery. Exact-head Ubuntu CI becomes the authoritative complete pre-release platform run. Local Windows development remains useful for focused feedback, and WSL2 is the suggested Linux route for developers who need the required execution environment.

This choice is reversible before the first consumable release, but expansion is evidence-driven rather than implied by source portability. No migration, deprecation period, compatibility reader, dual matrix, or Windows fallback is required because no released Windows consumer or persisted release state exists.

## References

- [Issue #85](https://github.com/SolusQuest/contract-scribe/issues/85)
- [Issue #84](https://github.com/SolusQuest/contract-scribe/issues/84)
- [Release policy](../../10_workflow/release-policy.md)
- [Distribution](../distribution.md)
- [Roadmap](../../90_roadmap/roadmap.md)
- [Test validation](../../50_ai/skills/test-validation.md)
- [ADR 0001](0001-loader-and-distribution-boundary.md)
- [ADR 0002](0002-process-topology.md)
