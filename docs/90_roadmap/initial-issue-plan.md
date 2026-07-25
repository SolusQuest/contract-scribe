# Initial M0 issue plan

Status: completed historical plan.

All M0 items are separately tracked, public, repository-local tasks under the M0 parent plan.

| Item | Goal | Dependencies |
| --- | --- | --- |
| M0.1 | Define policy/configuration v1, precedence, paths, strict parsing, public fixtures, and a test-only conformance oracle. | None |
| M0.2 | Define Audit Result v1, canonical JSON, diagnostics, and public fixtures. | None |
| M0.3 | Define symbol identity, target taxonomy, evidence taxonomy, generated-code policy, locators, and fixture strategy. | None |
| M0.4 | Prove framework-dependent Roslyn/MSBuild loading and deterministic semantic payload on the primary synthetic fixture. | None |
| M0.5 | Attempt Native AOT on the exact M0.4 semantic path and record supported or bounded not-feasible evidence. | M0.4 |
| M0.6 | Compare M0.4 and M0.5 evidence and record the selected or no-selection loader/distribution baseline in an ADR. | M0.4, M0.5 |
| M0.7 | Validate the M0.6 selected baseline on an independent synthetic repository. | M0.6 |

M0.1–M0.3 define contract inputs to M1 planning. M0.4–M0.6 experiment and select an evidence-based execution/distribution candidate; M0.7 independently validates that selected baseline against an independent synthetic repository shape. A failed M0.7 smoke requires the ADR or selected baseline to be revised and revalidated. The bootstrap does not perform these tasks.

M0 closed after every item above completed. The contract artifacts remain a commit-pinned M0 milestone baseline governed by [Contract lifecycle](../00_project/contract-lifecycle.md); M1 may complete the pre-release v1 semantics through a coordinated amendment and revalidation without reopening M0 or incrementing versions solely for development churn.
