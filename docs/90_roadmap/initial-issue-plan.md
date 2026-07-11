# Initial issue plan

All M0 items are independent, public, repository-local tasks under the M0 parent plan.

| Item | Purpose | Dependencies |
| --- | --- | --- |
| M0.1 | Define policy/config v1. | None |
| M0.2 | Define audit-result v1 schema. | None |
| M0.3 | Define symbol and evidence taxonomy. | None |
| M0.4 | Validate framework-dependent Roslyn/MSBuild loading. | None |
| M0.5 | Validate Native AOT distribution feasibility on the M0.4 semantic path. | M0.4 |
| M0.6 | Decide loader and distribution architecture in ADR 0001. | M0.4, M0.5 |

M0.1–M0.3 inform M1 implementation planning. M0.4–M0.6 decide the execution and distribution boundary. The bootstrap does not perform these tasks.
