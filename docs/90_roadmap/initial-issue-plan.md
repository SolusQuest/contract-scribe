# Initial issue plan

All M0 items are separately tracked, public, repository-local tasks under the M0 parent plan.

| Item | Purpose | Dependencies |
| --- | --- | --- |
| M0.1 | Define policy/config v1. | None |
| M0.2 | Define audit-result v1 schema. | None |
| M0.3 | Define symbol and evidence taxonomy. | None |
| M0.4 | Validate framework-dependent Roslyn/MSBuild loading. | None |
| M0.5 | Validate Native AOT distribution feasibility on the M0.4 semantic path. | M0.4 |
| M0.6 | Decide loader and distribution architecture in ADR 0001. | M0.4, M0.5 |
| M0.7 | Validate the M0.6 selected baseline on an independent synthetic repository. | M0.6 |

M0.1–M0.3 define contract inputs to M1 implementation planning. M0.4–M0.6 experiment and select an evidence-based execution/distribution candidate; M0.7 independently validates that selected baseline before M0 can exit. A failed M0.7 smoke requires the ADR or selected baseline to be revised and revalidated. The bootstrap does not perform these tasks.
